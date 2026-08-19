namespace Yession.Domain

/// What a queued act is ABOUT: the key its entry is stored and drained under.
///
/// The approval modes that used to live beside this are gone (Plan 23): every act passes
/// the classifier (`Classify.fs`) on its way to happening, and there is no per-subject
/// policy to store. What remains is the subject itself — the routing between the kinds of
/// act, and the wire form persisted docs already carry.
type GateSubject =
    /// One terminal.
    | ForTerminal of TerminalId
    /// One command, by its MCP tool name. Nothing writes this any more (structured
    /// commands classify and dispatch synchronously); the case remains so a doc written
    /// before the cut still decodes.
    | ForCommand of string

module GateSubject =

    let describe =
        function
        | ForTerminal id -> "terminal:" + TerminalId.value id
        | ForCommand name -> "command:" + name

    let parse (raw: string) : GateSubject option =
        if raw.StartsWith "terminal:" then
            match TerminalId.create (raw.Substring 9) with
            | Ok id -> Some (ForTerminal id)
            | Error _ -> None
        elif raw.StartsWith "command:" then
            match raw.Substring 8 with
            | "" -> None
            | name -> Some (ForCommand name)
        else None
