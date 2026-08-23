namespace Yession.Domain

/// Terminal output as styled text. A terminal's bytes are not plain text — they carry
/// SGR escapes that colour and weight what follows, carriage returns that rewrite the
/// current line, and control sequences that mean nothing to a transcript reader. This
/// turns that stream into the only shape a renderer can use: lines of styled runs.
///
/// It is a parser, not an emulator. It answers "what does this output *say*", which is
/// what a block of command output needs; it does not maintain a screen, a cursor grid,
/// or an alternate buffer, which is what a full-screen program needs (that arrives with
/// the pty, where a real emulator does the job properly). The line between the two is
/// deliberate: half an emulator would be a worse emulator and a worse parser.
///
/// The line is drawn at what a STREAM can say exactly, and two things are on this side of
/// it. A carriage return rewrites the line being written. A cursor-forward moves right over
/// cells nothing has written, which is a run of blanks under any emulator — and it is here
/// because this renders the LIVE SCREEN too, and a serialized screen is mostly that: the
/// emulator writes every gap between two pieces of content as `ESC[nC`. Everything past
/// that boundary — the other cursor moves, the erases — needs a grid to mean anything, and
/// is dropped.
///
/// Pure and total: unknown escapes are dropped rather than rendered, because printing
/// `ESC[?25l` at a person is strictly worse than printing nothing.
type AnsiColour =
    /// The terminal's own default — the renderer's foreground/background.
    | DefaultColour
    /// A palette index: 0-7 standard, 8-15 bright, 16-255 the xterm cube and greys.
    | IndexedColour of int
    | RgbColour of r: int * g: int * b: int

type AnsiStyle =
    { Bold : bool
      Dim : bool
      Italic : bool
      Underline : bool
      /// Swap foreground and background at render time. Kept as a flag rather than
      /// applied here: the renderer knows the actual default colours, this does not.
      Inverse : bool
      Foreground : AnsiColour
      Background : AnsiColour }

/// A run of text sharing one style. The unit a renderer emits as a single element.
type AnsiSpan = { Text : string; Style : AnsiStyle }

/// One output line: its spans in order. An empty line has no spans.
type AnsiLine = { Spans : AnsiSpan list }

module AnsiStyle =

    let plain : AnsiStyle =
        { Bold = false
          Dim = false
          Italic = false
          Underline = false
          Inverse = false
          Foreground = DefaultColour
          Background = DefaultColour }

module AnsiColour =

    /// The 6×6×6 colour cube's component levels (xterm's, which every terminal follows).
    let private cubeLevels = [| 0; 95; 135; 175; 215; 255 |]

    /// The RGB an indexed colour denotes, for the arithmetic part of the palette: the
    /// 216-colour cube (16-231) and the 24-step greyscale ramp (232-255).
    ///
    /// Indices 0-15 deliberately return `None`. Those are the terminal's *named* colours
    /// — "red" means whatever red the theme says — so resolving them here would hard-code
    /// a palette into the domain and put terminal output outside the theme's contrast
    /// guarantees. The renderer owns those sixteen; this owns the other 240.
    let rgbOf (colour: AnsiColour) : (int * int * int) option =
        match colour with
        | DefaultColour -> None
        | RgbColour (r, g, b) -> Some (r, g, b)
        | IndexedColour i when i >= 16 && i <= 231 ->
            let i = i - 16
            Some (cubeLevels.[i / 36], cubeLevels.[(i / 6) % 6], cubeLevels.[i % 6])
        | IndexedColour i when i >= 232 && i <= 255 ->
            let level = 8 + (i - 232) * 10
            Some (level, level, level)
        | IndexedColour _ -> None

module Ansi =

    /// The widest gap a cursor-forward is rendered as. Wider than any terminal anybody
    /// renders, and the point is the ceiling rather than the number: `ESC[nC` is the one
    /// sequence here that expands, `n` is written by whatever ran, and unbounded expansion of
    /// attacker-reachable output is a way to turn a few bytes of a command's stdout into a
    /// browser that stops responding.
    let private cufCap = 1000

    /// Apply one SGR parameter list to a style. Total: a parameter this does not know
    /// leaves the style alone rather than failing the parse.
    let private applySgr (style: AnsiStyle) (ps: int list) : AnsiStyle =
        // Extended colour (`38`/`48`) consumes following parameters, so this walks the
        // list rather than folding over it independently.
        let rec walk (style: AnsiStyle) (ps: int list) : AnsiStyle =
            match ps with
            | [] -> style
            | 0 :: rest -> walk AnsiStyle.plain rest
            | 1 :: rest -> walk { style with Bold = true } rest
            | 2 :: rest -> walk { style with Dim = true } rest
            | 3 :: rest -> walk { style with Italic = true } rest
            | 4 :: rest -> walk { style with Underline = true } rest
            | 7 :: rest -> walk { style with Inverse = true } rest
            | 22 :: rest -> walk { style with Bold = false; Dim = false } rest
            | 23 :: rest -> walk { style with Italic = false } rest
            | 24 :: rest -> walk { style with Underline = false } rest
            | 27 :: rest -> walk { style with Inverse = false } rest
            | 39 :: rest -> walk { style with Foreground = DefaultColour } rest
            | 49 :: rest -> walk { style with Background = DefaultColour } rest
            | 38 :: 5 :: n :: rest -> walk { style with Foreground = IndexedColour n } rest
            | 48 :: 5 :: n :: rest -> walk { style with Background = IndexedColour n } rest
            | 38 :: 2 :: r :: g :: b :: rest -> walk { style with Foreground = RgbColour (r, g, b) } rest
            | 48 :: 2 :: r :: g :: b :: rest -> walk { style with Background = RgbColour (r, g, b) } rest
            | n :: rest when n >= 30 && n <= 37 -> walk { style with Foreground = IndexedColour (n - 30) } rest
            | n :: rest when n >= 40 && n <= 47 -> walk { style with Background = IndexedColour (n - 40) } rest
            | n :: rest when n >= 90 && n <= 97 -> walk { style with Foreground = IndexedColour (n - 90 + 8) } rest
            | n :: rest when n >= 100 && n <= 107 -> walk { style with Background = IndexedColour (n - 100 + 8) } rest
            // An unknown parameter is skipped, not fatal: terminals ignore what they do
            // not implement, and so does this.
            | _ :: rest -> walk style rest
        // A bare `ESC[m` is `ESC[0m` — the reset every prompt ends with.
        walk style (if List.isEmpty ps then [ 0 ] else ps)

    /// Parse the CSI parameter bytes (`0-9`, `;`, and the private markers `?<=>`) starting
    /// at `i`, returning the parameters, the final byte, and the index just past it.
    /// A sequence with a private marker is recognised and dropped: `ESC[?25l` (hide
    /// cursor) must not reach the screen as text.
    let private readCsi (text: string) (i: int) : int list * char * int =
        let mutable j = i
        let mutable isPrivate = false
        let ps = ResizeArray<int> ()
        // Plain strings rather than a `StringBuilder`: this compiles to JavaScript, where a
        // string IS the builder, and `StringBuilder.Remove` (which the overwrite rules below
        // need) is not in Fable's library at all.
        let mutable current = ""
        let flush () =
            // An empty parameter is 0 (`ESC[;31m` = `ESC[0;31m`), which is what makes the
            // reset-then-set idiom work.
            ps.Add (if current = "" then 0 else int current)
            current <- ""
        let mutable finalByte = '\000'
        let mutable go = true
        while go && j < text.Length do
            let c = text.[j]
            if c >= '0' && c <= '9' then
                current <- current + string c
                j <- j + 1
            elif c = ';' then
                flush ()
                j <- j + 1
            elif c = '?' || c = '<' || c = '=' || c = '>' then
                isPrivate <- true
                j <- j + 1
            elif c >= '@' && c <= '~' then
                if current <> "" || ps.Count > 0 then flush ()
                finalByte <- c
                j <- j + 1
                go <- false
            else
                // Not a legal CSI byte: the sequence is malformed, so stop where it went
                // wrong rather than consuming the rest of the output looking for a final.
                finalByte <- '\000'
                go <- false
        // A private sequence is never an SGR, whatever its final byte says.
        (if isPrivate then [] else List.ofSeq ps), (if isPrivate then '\000' else finalByte), j

    /// Parse terminal output into styled lines, starting from `initial` and returning the
    /// style left in force — so a stream can be parsed in pieces without a colour bleeding
    /// or resetting at an arbitrary chunk boundary.
    ///
    /// Carriage return does what a terminal does: it returns to the start of the line, so
    /// what follows overwrites it. That is why a progress bar renders as its final state
    /// rather than as two hundred stacked lines.
    let parseFrom (initial: AnsiStyle) (text: string) : AnsiLine list * AnsiStyle =
        let lines = ResizeArray<AnsiLine> ()
        // Spans completed on the current line, plus the run being accumulated.
        let mutable spans = ResizeArray<AnsiSpan> ()
        let mutable run = ""
        let mutable style = initial

        let closeRun () =
            if run <> "" then
                spans.Add { Text = run; Style = style }
                run <- ""

        let endLine () =
            closeRun ()
            lines.Add { Spans = List.ofSeq spans }
            spans <- ResizeArray<AnsiSpan> ()

        // Carriage return: drop everything written on this line so far. The overwrite is
        // total rather than per-column because a column-accurate overwrite is emulator
        // work, and every real use of `\r` in piped output rewrites the whole line.
        let carriageReturn () =
            run <- ""
            spans <- ResizeArray<AnsiSpan> ()

        let mutable i = 0
        while i < text.Length do
            let c = text.[i]
            if c = '\n' then
                endLine ()
                i <- i + 1
            elif c = '\r' then
                // `\r\n` is one line break, not an erase followed by one.
                if i + 1 < text.Length && text.[i + 1] = '\n' then
                    endLine ()
                    i <- i + 2
                else
                    carriageReturn ()
                    i <- i + 1
            elif c = '\027' && i + 1 < text.Length && text.[i + 1] = '[' then
                let ps, finalByte, next = readCsi text (i + 2)
                if finalByte = 'm' then
                    closeRun ()
                    style <- applySgr style ps
                elif finalByte = 'C' then
                    // Cursor forward, as that many spaces. The one cursor move a stream can
                    // express exactly: it goes right, on the line being written, over cells
                    // nothing has put anything in — which is a run of blanks however the
                    // screen is maintained.
                    //
                    // It earns the exception because a SERIALIZED SCREEN is made of it. The
                    // live viewport renders what `@xterm/headless` serializes, and the
                    // serializer writes every gap between two pieces of content as `ESC[nC`
                    // — so dropping it collapsed a full-screen program's whole layout to the
                    // left margin, under a renderer that was only ever asked about a block's
                    // output. Block output gains the same thing: a program that indents this
                    // way now indents.
                    //
                    // Default 1 with no parameter, as the control does. Everything else still
                    // goes: `CUU`, `CUP` and the erases need a grid to be meaningful, and the
                    // serializer emits them only as the trailing cursor restoration a static
                    // render has no cursor to restore.
                    //
                    // Bounded, because this is the one branch here that turns a few bytes of
                    // output into many, and output is whatever a command printed. A real one
                    // cannot exceed the screen's width — the serializer never writes a gap
                    // wider than the terminal — so anything past `cufCap` is not a gap to
                    // render, it is an amplifier.
                    let by = match ps with | n :: _ when n > 0 -> min n cufCap | _ -> 1
                    run <- run + System.String (' ', by)
                // Every other CSI (the rest of the cursor moves, erases, mode sets) is
                // dropped: this parses output, it does not maintain a screen.
                i <- next
            elif c = '\027' && i + 1 < text.Length && (text.[i + 1] = ']' || text.[i + 1] = 'P') then
                // OSC / DCS: a string-terminated sequence (BEL or ESC \). Dropped whole —
                // window titles and semantic prompt marks are not output.
                let mutable j = i + 2
                let mutable go = true
                while go && j < text.Length do
                    if text.[j] = '\007' then
                        j <- j + 1
                        go <- false
                    elif text.[j] = '\027' && j + 1 < text.Length && text.[j + 1] = '\\' then
                        j <- j + 2
                        go <- false
                    else j <- j + 1
                i <- j
            elif c = '\027' then
                // A lone ESC, or a two-byte escape: drop the ESC and whatever follows it.
                i <- min text.Length (i + 2)
            elif c = '\008' then
                // Backspace: erase the last character written, which is how `\b` is used
                // in practice (spinner frames, over-typed passwords).
                if run <> "" then run <- run.Substring (0, run.Length - 1)
                i <- i + 1
            elif c = '\000' then
                // NUL is padding, never content.
                i <- i + 1
            else
                run <- run + string c
                i <- i + 1

        // A trailing partial line is still a line — output that has not ended with a
        // newline is the normal case for a prompt, and for the tail of live output.
        closeRun ()
        if spans.Count > 0 then lines.Add { Spans = List.ofSeq spans }
        List.ofSeq lines, style

    /// Parse a complete piece of output from a clean style.
    let parse (text: string) : AnsiLine list = parseFrom AnsiStyle.plain text |> fst

    /// The plain text a parse denotes — every span's text, lines rejoined. This is what a
    /// search, a copy, or an agent reading its own command output wants: the words without
    /// the paint.
    let plainText (lines: AnsiLine list) : string =
        lines
        |> List.map (fun line -> line.Spans |> List.map (fun s -> s.Text) |> String.concat "")
        |> String.concat "\n"
