namespace Yession.Domain

/// OSC 133 semantic marks: how the Session Process learns where a block starts, where it
/// ends, and with what code (Plan 13, stage 2d).
///
/// The marks are emitted by the shell's own prompt hooks — not by a sentinel the drain
/// appends to the command line. A sentinel is the tempting cheaper mechanism and it is wrong
/// for ordinary command lines a person is invited to type and edit: a trailing comment
/// (`ls # check`) swallows it, a heredoc puts it on the delimiter line, a trailing `&`
/// changes what `$?` refers to. Repairing that lands a parser for shell grammar in the
/// drain, which fails silently as a block that never closes.
///
/// EVERY MARK CARRIES A NONCE, and that is the whole of the integrity story. `ESC ] 133 ; D
/// ; 0 BEL` is a dozen bytes of ASCII and the output stream is full of bytes we did not
/// write — a file someone `cat`s, a build log, a filename. Here a mark is not a rendering
/// nicety: it writes the event log. A forged `D` would close the running block early with an
/// exit code nobody produced and a truncated transcript range — a failed command recorded as
/// successful, in the record that exists to be trusted. So a mark without this terminal's
/// nonce is not a mark; it is bytes that happen to look like one, and it stays in the output.
///
/// Warp, whose block model this is, does not use OSC 133 for boundaries at all: it carries
/// hex-encoded JSON on a private DCS channel, because it must move USER DATA out of a shell it
/// does not control — a cwd or a PS1 containing `ESC` would otherwise terminate the sequence
/// carrying it. We need none of that. The drain COMPOSED the command line and already recorded
/// it in `TerminalBlockStarted`, so all that has to come back is "it started" and "it finished,
/// with this code" — which is exactly what `C` and `D` say, and what
/// `registerOscHandler(133, …)` reads without a parser of ours. Anything richer means
/// encoding, and encoding means a payload.
type TerminalMark =
    /// `A` — the shell is about to print its prompt. The handshake the open-probe waits for,
    /// and the only evidence that instrumentation took at all.
    | MarkPromptStart
    /// `C` — the shell has STARTED running a command. Its timing does not depend on how long
    /// the command runs, which is what makes a missing one a reliable signal that the marks
    /// are gone rather than that the command is slow.
    | MarkCommandStart
    /// `D;<code>` — the command finished, with this exit status.
    | MarkCommandDone of exitCode: int

module TerminalMarks =

    [<Literal>]
    let private Esc = '\u001b'

    [<Literal>]
    let private Bel = '\u0007'

    /// The prefix every mark shares: `ESC ] 133 ;`.
    let private prefix = "\u001b]133;"

    /// The shell-side emitter for one mark, as the text a prompt hook prints. `%d` where the
    /// exit code goes; the nonce is baked in by `rcFor`.
    let private emit (nonce: string) (body: string) = sprintf "\\033]133;%s;y=%s\\007" body nonce

    /// Parse one mark body (everything between `ESC ] 133 ;` and the terminator), returning
    /// the mark only when the nonce matches.
    ///
    /// The body is `<kind>[;<param>]*`, and the nonce rides as a `y=` parameter so the shape
    /// stays a legal OSC 133 sequence — a terminal that does not know about `y=` ignores it,
    /// which keeps the emitter usable in a plain terminal for debugging.
    let parseBody (nonce: string) (body: string) : TerminalMark option =
        let parts = body.Split ';' |> Array.toList
        let hasNonce = parts |> List.exists (fun p -> p = "y=" + nonce)
        if not hasNonce then None
        else
            match parts with
            | "A" :: _ -> Some MarkPromptStart
            | "C" :: _ -> Some MarkCommandStart
            | "D" :: code :: _ ->
                // A `D` whose code will not parse is still a completion — the shell said the
                // command ended and it holds the nonce. Reporting -1 keeps the block closing,
                // which is better than leaving it open for ever over an unreadable integer.
                match System.Int32.TryParse code with
                | true, value -> Some (MarkCommandDone value)
                | _ -> Some (MarkCommandDone -1)
            // `D` with no code at all: same reasoning.
            | [ "D" ] -> Some (MarkCommandDone -1)
            | _ -> None

    /// Scan a chunk of output: the marks it carried, the bytes with our marks REMOVED, and
    /// whatever trailing partial sequence must be carried into the next chunk.
    ///
    /// Three things this has to get right, and each of them is a bug if it does not.
    ///
    /// **Marks are stripped**, so the nonce never reaches the transcript. Chunks are
    /// fetchable over HTTP and, after stage 3b, readable by the agent through
    /// `read_terminal_block` — an unstripped nonce would let one approved command buy the
    /// ability to forge the outcome of every later one. It is independently right: marks are
    /// protocol rather than content, and an asciicast replayed in `asciinema` should not
    /// carry them.
    ///
    /// **A partial sequence is carried, not dropped.** A pty delivers whatever the kernel
    /// had, so a mark can arrive split across two reads. Scanning each chunk independently
    /// would both miss the mark and leave half an escape sequence in the transcript.
    ///
    /// **A foreign mark is left alone.** Anything without our nonce — including a nested
    /// shell's own OSC 133 integration — passes through as ordinary output, because it IS
    /// ordinary output as far as this terminal is concerned.
    let scan (nonce: string) (carry: string) (data: string) : TerminalMark list * string * string =
        let input = carry + data
        let out = System.Text.StringBuilder ()
        let marks = ResizeArray<TerminalMark> ()

        let rec walk (index: int) : string =
            if index >= input.Length then ""
            else
                let start = input.IndexOf (prefix, index)
                if start < 0 then
                    // No mark ahead. Everything is output except a trailing fragment that
                    // could still BECOME our prefix once the next chunk arrives.
                    let keepFrom =
                        let candidate = max index (input.Length - prefix.Length)
                        // The longest suffix that is a proper prefix of `ESC ] 133 ;`.
                        [ candidate .. input.Length - 1 ]
                        |> List.tryFind (fun i -> prefix.StartsWith (input.Substring i))
                        |> Option.defaultValue input.Length
                    out.Append (input.Substring (index, keepFrom - index)) |> ignore
                    input.Substring keepFrom
                else
                    out.Append (input.Substring (index, start - index)) |> ignore
                    let bodyStart = start + prefix.Length
                    // A mark ends at BEL or at ST (`ESC \`); both spellings are legal and a
                    // shell's `printf` may emit either.
                    let bel = input.IndexOf (Bel, bodyStart)
                    let st = input.IndexOf ("\u001b\\", bodyStart)
                    let terminator =
                        match bel, st with
                        | -1, -1 -> -1
                        | -1, s -> s
                        | b, -1 -> b
                        | b, s -> min b s
                    if terminator < 0 then
                        // Truncated: carry the whole thing, terminator and all, to the next
                        // chunk. Emitting it as output here would leak a half-written mark
                        // into the transcript and lose the mark itself.
                        input.Substring start
                    else
                        let body = input.Substring (bodyStart, terminator - bodyStart)
                        let width = if input.[terminator] = Bel then 1 else 2
                        match parseBody nonce body with
                        | Some mark ->
                            marks.Add mark
                            walk (terminator + width)
                        | None ->
                            // Not ours. It is output, verbatim — including its terminator.
                            out.Append (input.Substring (start, terminator + width - start)) |> ignore
                            walk (terminator + width)

        let rest = walk 0
        List.ofSeq marks, string out, rest

    /// The shell instrumentation, as the text of an rc file. Parameterised by the terminal's
    /// nonce, which is minted per terminal and never leaves the Session Process except into
    /// this file.
    ///
    /// Two habits here cost nothing and are taken from Warp's hooks. Every line begins with a
    /// SPACE, so `HISTCONTROL=ignorespace` / `HIST_IGNORE_SPACE` keeps our instrumentation
    /// out of the user's shell history. And external binaries are invoked through
    /// `command -p`, so a clobbered `PATH` in someone's image cannot break the marks.
    ///
    /// The exit code is captured as the FIRST statement of the prompt hook. Anything run
    /// before it overwrites `$?`, and then every block reports the status of our own
    /// bookkeeping rather than of the command — which would be worse than no mark at all,
    /// because it looks like an answer.
    let rcFor (shell: string) (nonce: string) : string option =
        let promptStart = emit nonce "A"
        let commandStart = emit nonce "C"
        let commandDone = emit nonce "D;%d"
        match shell with
        | "bash" ->
            // bash has no `preexec`; the DEBUG trap is the standard emulation, guarded so it
            // fires once per submitted command rather than once per word. It also fires while
            // the shell starts up, which shows as one spurious C/D pair before any command —
            // dropped by the same rule that drops a duplicate.
            Some (
                String.concat "\n"
                    [ " __y_pre() { [ -n \"$__y_armed\" ] && { command -p printf '" + commandStart + "'; unset __y_armed; }; }"
                      " __y_post() { __y_code=$?; command -p printf '" + commandDone + "' \"$__y_code\"; __y_armed=1; }"
                      " PROMPT_COMMAND='__y_post'"
                      " PS1='\\[" + promptStart + "\\]'\"$PS1\""
                      " __y_armed=1"
                      " trap '__y_pre' DEBUG" ])
        | "zsh" ->
            // zsh has real hooks. They are APPENDED to whatever the image's shell already
            // registered, never substituted: replacing a shell's existing hooks breaks the
            // shell (Warp special-cases powerlevel10k for exactly this).
            Some (
                String.concat "\n"
                    [ " __y_pre() { command -p printf '" + commandStart + "'; }"
                      " __y_post() { __y_code=$?; command -p printf '" + commandDone + "' \"$__y_code\"; }"
                      " autoload -Uz add-zsh-hook"
                      " add-zsh-hook preexec __y_pre"
                      " add-zsh-hook precmd __y_post"
                      " PS1='" + promptStart + "'\"$PS1\"" ])
        | "sh"
        | "dash" ->
            // A bare POSIX shell has NO prompt hook, so the marks ride inside PS1, which the
            // shell re-expands before each prompt. This is the shakiest of the three — there
            // is no `preexec` to hang `C` on at all — and it is exactly why the probe decides
            // by observation rather than by shell name.
            Some (" PS1='$(command -p printf \"" + commandDone + "\" $?)" + promptStart + "'")
        | _ -> None
