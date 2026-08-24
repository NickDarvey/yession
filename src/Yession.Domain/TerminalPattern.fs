namespace Yession.Domain.Terminals

open Yession.Domain

// Matching a pattern against what a terminal said (Plan 25, step 9).
//
// This exists because the obvious thing is not available. A regular expression here would be
// a JS `RegExp` — the Session Process is Fable on Node — and JS has no match timeout. A
// catastrophic pattern would not fail a turn, it would wedge the event loop that owns every
// terminal in the session, the Yjs document and the WebRTC pump. .NET's `Regex` takes a
// `MatchTimeout`; the runtime this actually runs on does not. So "use a regex and bound it"
// is not an option, and that — rather than any squeamishness about what a model might write —
// is why the first cut of `wait_for` took literal text only.
//
// What IS available is a matcher that cannot blow up, rather than one we hope will not.
// Thompson's construction compiles the pattern to a small instruction list and simulates ALL
// live states at once, so the cost is O(pattern x input) with no backtracking and no stack.
// `(a+)+$` against a long run of `a` is the case that hangs a backtracking engine and is
// merely a match here; there is a test that says so.
//
// The subset is small on purpose. Backreferences cannot be simulated this way at all, and
// lookaround and non-greedy quantifiers could be but would each buy one more thing a caller
// has to learn. They are refused BY NAME, so the subset is learnable rather than guessable.

open System

/// The instruction set. `PatternSplit` is the only branch, and the reason the simulation
/// stays linear: it does not choose, it runs both.
type internal PatternInst =
    | PatternChar of (char -> bool)
    /// Start of the input or just after a newline — a console is lines, so an anchor that
    /// meant "the whole transcript" would be an anchor nobody could use.
    | PatternLineStart
    /// End of the input or just before a newline.
    | PatternLineEnd
    | PatternSplit of int * int
    | PatternJmp of int
    | PatternMatch

/// One compiled pattern. Opaque: what a caller does with it is ask whether text matches.
type TerminalPattern = internal { Program : PatternInst list }

module TerminalPattern =

    // --- parsing ----------------------------------------------------------------------
    //
    // A hand-written recursive descent over the subset. Small enough to read in one sitting,
    // which matters more here than elsewhere: this is the code that decides what a caller is
    // allowed to say.

    type private Node =
        | NEmpty
        | NChar of (char -> bool)
        | NLineStart
        | NLineEnd
        | NConcat of Node * Node
        | NAlt of Node * Node
        | NStar of Node
        | NPlus of Node
        | NOpt of Node

    /// What a construct outside the subset is called, so a refusal can name it. A caller told
    /// "unsupported pattern" has to guess which part; one told "a backreference" has learned
    /// the rule.
    let private named (pattern: string) (at: int) : string option =
        let peek offset = if at + offset < pattern.Length then Some pattern.[at + offset] else None
        match pattern.[at], peek 1 with
        | '(', Some '?' ->
            match peek 2 with
            | Some '=' | Some '!' -> Some "a lookahead"
            | Some '<' -> Some "a lookbehind"
            | _ -> Some "an extended group"
        | '\\', Some d when Char.IsDigit d -> Some "a backreference"
        | _ -> None

    let private classAt (pattern: string) (start: int) : Result<(char -> bool) * int, string> =
        // `[^abc]`, `[a-z0-9_]`. A closing bracket immediately after the caret or the opening
        // is a literal `]`, which is the one piece of folklore worth keeping: `[]]` is a
        // bracket, not an empty class.
        let mutable at = start + 1
        let negated = at < pattern.Length && pattern.[at] = '^'
        if negated then at <- at + 1
        let ranges = ResizeArray<char * char> ()
        let mutable first = true
        let mutable closed = false
        let mutable failure = None
        while not closed && failure.IsNone && at < pattern.Length do
            if pattern.[at] = ']' && not first then
                closed <- true
                at <- at + 1
            else
                first <- false
                let c =
                    if pattern.[at] = '\\' && at + 1 < pattern.Length then
                        at <- at + 1
                        pattern.[at]
                    else pattern.[at]
                if at + 2 < pattern.Length && pattern.[at + 1] = '-' && pattern.[at + 2] <> ']' then
                    ranges.Add (c, pattern.[at + 2])
                    at <- at + 3
                else
                    ranges.Add (c, c)
                    at <- at + 1
        match failure with
        | Some reason -> Error reason
        | None when not closed -> Error "a character class that is never closed"
        | None ->
            let inside (c: char) = ranges |> Seq.exists (fun (lo, hi) -> c >= lo && c <= hi)
            Ok ((fun c -> if negated then not (inside c) else inside c), at)

    let private parse (pattern: string) : Result<Node, string> =
        let mutable at = 0
        let mutable failure : string option = None

        let fail reason =
            if failure.IsNone then failure <- Some reason
            NEmpty

        let rec alternation () =
            let left = sequence ()
            if failure.IsSome then left
            elif at < pattern.Length && pattern.[at] = '|' then
                at <- at + 1
                NAlt (left, alternation ())
            else left

        and sequence () =
            let mutable node = NEmpty
            let mutable stop = false
            while not stop && failure.IsNone && at < pattern.Length && pattern.[at] <> '|' && pattern.[at] <> ')' do
                let piece = repeated ()
                node <- (match node with NEmpty -> piece | existing -> NConcat (existing, piece))
                if at >= pattern.Length then stop <- true
            node

        and repeated () =
            let atom' = atom ()
            if failure.IsSome then atom'
            else
                let mutable node = atom'
                let mutable more = true
                while more && failure.IsNone && at < pattern.Length do
                    let quantified =
                        match pattern.[at] with
                        | '*' -> Some (NStar node)
                        | '+' -> Some (NPlus node)
                        | '?' -> Some (NOpt node)
                        | _ -> None
                    match quantified with
                    | None -> more <- false
                    | Some q ->
                        at <- at + 1
                        // `a*?` and friends. Simulable, but each one is another rule a caller
                        // has to know, and greedy-only is the smaller promise to keep.
                        if at < pattern.Length && pattern.[at] = '?' then
                            fail "a non-greedy quantifier (`*?`, `+?`, `??`)" |> ignore
                        node <- q
                node

        and atom () =
            if at >= pattern.Length then NEmpty
            else
                match named pattern at with
                | Some construct -> fail construct
                | None ->
                    match pattern.[at] with
                    | '(' ->
                        at <- at + 1
                        let inner = alternation ()
                        if at < pattern.Length && pattern.[at] = ')' then
                            at <- at + 1
                            inner
                        else fail "a group that is never closed"
                    | ')' -> fail "a `)` with nothing open"
                    | '[' ->
                        match classAt pattern at with
                        | Ok (predicate, next) ->
                            at <- next
                            NChar predicate
                        | Error reason -> fail reason
                    | '.' ->
                        at <- at + 1
                        // Not newlines: a console is lines, and a `.` that ate them would make
                        // every pattern match halfway down the screen.
                        NChar (fun c -> c <> '\n')
                    | '^' ->
                        at <- at + 1
                        NLineStart
                    | '$' ->
                        at <- at + 1
                        NLineEnd
                    | '\\' when at + 1 < pattern.Length ->
                        let escaped = pattern.[at + 1]
                        at <- at + 2
                        match escaped with
                        | 'n' -> NChar (fun c -> c = '\n')
                        | 'r' -> NChar (fun c -> c = '\r')
                        | 't' -> NChar (fun c -> c = '\t')
                        | 'd' -> NChar Char.IsDigit
                        | 'w' -> NChar (fun c -> Char.IsLetterOrDigit c || c = '_')
                        | 's' -> NChar Char.IsWhiteSpace
                        | other -> NChar (fun c -> c = other)
                    | '\\' -> fail "a trailing backslash"
                    | c ->
                        at <- at + 1
                        NChar (fun ch -> ch = c)

        let node = alternation ()
        match failure with
        | Some reason -> Error reason
        | None when at < pattern.Length -> Error "a `)` with nothing open"
        | None -> Ok node

    // --- compiling --------------------------------------------------------------------

    let private compileNode (node: Node) : PatternInst list =
        let program = ResizeArray<PatternInst> ()
        let emit inst =
            program.Add inst
            program.Count - 1
        let rec go node =
            match node with
            | NEmpty -> ()
            | NChar predicate -> emit (PatternChar predicate) |> ignore
            | NLineStart -> emit PatternLineStart |> ignore
            | NLineEnd -> emit PatternLineEnd |> ignore
            | NConcat (a, b) ->
                go a
                go b
            | NAlt (a, b) ->
                let split = emit (PatternSplit (0, 0))
                go a
                let jmp = emit (PatternJmp 0)
                let right = program.Count
                go b
                program.[split] <- PatternSplit (split + 1, right)
                program.[jmp] <- PatternJmp program.Count
            | NStar inner ->
                let split = emit (PatternSplit (0, 0))
                go inner
                emit (PatternJmp split) |> ignore
                program.[split] <- PatternSplit (split + 1, program.Count)
            | NPlus inner ->
                let start = program.Count
                go inner
                let split = emit (PatternSplit (0, 0))
                program.[split] <- PatternSplit (start, program.Count)
            | NOpt inner ->
                let split = emit (PatternSplit (0, 0))
                go inner
                program.[split] <- PatternSplit (split + 1, program.Count)
        go node
        emit PatternMatch |> ignore
        List.ofSeq program

    /// The longest pattern that will be accepted. A bound rather than a judgement: the
    /// simulation is linear in it, so this caps the work per input character at something a
    /// person can reason about.
    [<Literal>]
    let MaxLength = 512

    /// Compile a pattern, or say — by name — which construct put it outside the subset.
    let compile (pattern: string) : Result<TerminalPattern, string> =
        if String.IsNullOrEmpty pattern then Error "an empty pattern"
        elif pattern.Length > MaxLength then
            Error (sprintf "a pattern longer than %d characters" MaxLength)
        else
            parse pattern
            |> Result.map (fun node -> { Program = compileNode node })
            |> Result.mapError (fun construct ->
                sprintf
                    "%s is not something this matcher supports — it takes literal text, `.`, `[classes]`, `*`, `+`, `?`, `|`, groups and the line anchors `^` and `$`"
                    construct)

    // --- running ----------------------------------------------------------------------

    /// What a linear simulation cannot exceed, with room to spare: every input position visits
    /// each instruction at most once, so `program x input` bounds the honest cost and anything
    /// past a small multiple of it is not this algorithm running slowly — it is a different
    /// algorithm.
    ///
    /// This is a guard against OUR bug, not a caller's. The subset cannot express
    /// catastrophic backtracking, so a correct implementation never comes near this; an
    /// implementation that regressed to trying branches one at a time would run for ever, and
    /// on Node "for ever" means the event loop that owns every terminal in the session, the
    /// Yjs document and the WebRTC pump. A wall-clock timeout cannot save that — a
    /// synchronous loop is exactly what stops a timer firing, which is why `RegExp` has no
    /// timeout here either. A step budget is the only bound that can actually be enforced.
    let private budgetFor (program: int) (input: int) = (program * (input + 1) * 4) + 1024

    /// Does `text` contain a match, or — if the matcher somehow exceeded its budget — why it
    /// stopped instead of answering.
    ///
    /// Every live thread is stepped together over one pass of the input, which is what makes
    /// this linear: a `Split` adds both branches to the same set rather than trying one and
    /// coming back. A state already in the set is not added twice, so the set cannot outgrow
    /// the program however the pattern nests.
    let matchesWithin (budget: int) (compiled: TerminalPattern) (text: string) : Result<bool, string> =
        let program = Array.ofList compiled.Program
        let count = program.Length
        let mutable steps = 0
        let mutable exhausted = false
        let current = Array.zeroCreate<bool> count
        let next = Array.zeroCreate<bool> count
        let mutable matched = false

        let rec addThread (states: bool[]) (pc: int) (at: int) =
            steps <- steps + 1
            if steps > budget then exhausted <- true
            elif pc < count && not states.[pc] then
                states.[pc] <- true
                match program.[pc] with
                | PatternJmp target -> addThread states target at
                | PatternSplit (a, b) ->
                    addThread states a at
                    addThread states b at
                | PatternLineStart ->
                    if at = 0 || text.[at - 1] = '\n' then addThread states (pc + 1) at
                | PatternLineEnd ->
                    if at = text.Length || text.[at] = '\n' then addThread states (pc + 1) at
                | PatternMatch -> matched <- true
                | PatternChar _ -> ()

        let mutable at = 0
        // Seeded at every position, which is what makes this "contains a match" rather than
        // "the whole text is one". An anchored pattern rejects the positions it does not like
        // on its own, so there is no second code path for `^`.
        addThread current 0 0
        while not matched && not exhausted && at < text.Length do
            Array.fill next 0 count false
            for pc in 0 .. count - 1 do
                if current.[pc] && not exhausted then
                    match program.[pc] with
                    | PatternChar predicate when predicate text.[at] -> addThread next (pc + 1) (at + 1)
                    | _ -> ()
            Array.blit next 0 current 0 count
            at <- at + 1
            if not matched then addThread current 0 at
        if exhausted then
            Error
                "this matcher ran past the work a linear match can take — the pattern was not the problem, the matcher was"
        else Ok matched

    /// The ordinary way to ask, with the budget a correct simulation cannot reach.
    let matches (compiled: TerminalPattern) (text: string) : Result<bool, string> =
        matchesWithin (budgetFor (List.length compiled.Program) text.Length) compiled text
