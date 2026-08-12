namespace Yession.Domain

// Where a terminal's bytes come from (Plan 16, part D).
//
// A terminal is an emulator, a transcript, a lease and a panel OVER A BYTE STREAM. Until
// now that stream always came from `Sandbox.SpawnPty`, and the open path called it directly
// — so "terminal" and "shell in this session's WorkSandbox" were the same thing by
// construction rather than by choice.
//
// They are not the same thing. A serial port is a byte stream; so is a pty on another host,
// a CI runner, a container somewhere else. What separates them is not WHERE they come from
// but WHAT THEY CAN DO, and that is worth declaring rather than discovering: a source that
// cannot be instrumented should say so at open, not at the third command.

/// What a source can do, declared by whoever produced it.
///
/// Declared the way `Sandbox.SpawnPty : … option` already declares pty support — a backend
/// saying up front what it cannot host, rather than failing the first time somebody needs
/// it. The three are genuinely independent: a serial line has no size and no exit code but
/// carries bytes perfectly well, and a remote shell may have all three.
type SourceCapabilities =
    { /// Can the OSC 133 bootstrap be typed into it, so its output resolves into blocks with
      /// exit codes? False means LIVE ONLY: the transcript records everything, the lease
      /// arbitrates who types, and there are no blocks — which is exactly right for a device.
      CanInstrument : bool
      /// Does it have a size at all? A serial line does not, and telling one it is 80x24
      /// would be inventing a fact.
      CanResize : bool
      /// Does it end with a code, or merely stop?
      HasExitCode : bool }

module SourceCapabilities =

    /// A shell in this session's own sandbox: everything Plan 13 built assumes this.
    let shell : SourceCapabilities =
        { CanInstrument = true; CanResize = true; HasExitCode = true }

    /// The least a source can be and still be a terminal: bytes, both ways, and nothing
    /// else claimed. What a serial port is.
    let byteStream : SourceCapabilities =
        { CanInstrument = false; CanResize = false; HasExitCode = false }

/// What a provider hands back when it gives a session a stream: where to connect, what it
/// can do, and what to call it.
///
/// The ticket is the JOIN between the two protocols. Control — discovering a device,
/// claiming it, setting its baud rate — is request/response and goes over MCP. The bytes are
/// a continuous bidirectional stream and go over their own connection. Forcing both through
/// one protocol is what produced long-polling reads in the plan this supersedes.
type AttachTicket =
    { /// Where the byte stream is. A `ws://`/`wss://` URL today; the session opens it and
      /// nothing else interprets it.
      Url : string
      /// What the other end says it can do.
      Capabilities : SourceCapabilities
      /// A human name for the thing on the other end — the terminal's title, so a panel can
      /// say "USB serial /dev/ttyACM0" rather than an id.
      Label : string }

/// A stream a provider OFFERED, in the answer to a tool call (Plan 19).
///
/// The ticket says where the bytes are; this says what the session may do about getting
/// another one. Together they are the whole of the extension point: a provider that wants
/// its resource to become a terminal hangs one of these off a tool result, and nothing in
/// this repository has to know what the resource is.
type StreamOffer =
    { Ticket : AttachTicket
      /// May the call that produced this be made AGAIN to get another stream, and is doing
      /// so safe? An attach token is usually spent on use, so a terminal that ends cannot be
      /// resumed — re-attaching means asking the provider again, and only the provider knows
      /// whether asking again power-cycles a board. Default `false`, because the cost of
      /// guessing wrong is somebody's hardware.
      Renewable : bool }

module StreamOffer =

    /// The `_meta` key an offer rides under, on a `tools/call` result.
    ///
    /// `_meta` and not `structuredContent`, twice over: `structuredContent` is validated
    /// against the tool's own `outputSchema`, so this would become part of every provider's
    /// declared contract; and it goes to the MODEL, which would put a socket address in
    /// reach of anything that can talk the model into repeating it. An offer is for the
    /// client.
    ///
    /// Reverse-DNS prefixed because it IS an extension: if MCP grows a standard streaming
    /// affordance, the decoder learns that one, reads both for a release, and this is
    /// deleted. A bare `stream` would make that a flag day.
    let metaKey = "dev.yession/stream"

    /// The host of an absolute url, lowercased, with any port and userinfo removed. `None`
    /// when there is no authority to read — which every caller treats as "not admissible"
    /// rather than as a wildcard.
    let private hostOf (url: string) : string option =
        match url.IndexOf "://" with
        | -1 -> None
        | scheme ->
            let rest = url.Substring (scheme + 3)
            let authority =
                match rest.IndexOfAny [| '/'; '?'; '#' |] with
                | -1 -> rest
                | cut -> rest.Substring (0, cut)
            let afterUserInfo =
                match authority.LastIndexOf '@' with
                | -1 -> authority
                | at -> authority.Substring (at + 1)
            // A bracketed IPv6 literal keeps its colons; everything else loses its port.
            let host =
                if afterUserInfo.StartsWith "[" then
                    match afterUserInfo.IndexOf ']' with
                    | -1 -> afterUserInfo
                    | close -> afterUserInfo.Substring (0, close + 1)
                else
                    match afterUserInfo.IndexOf ':' with
                    | -1 -> afterUserInfo
                    | colon -> afterUserInfo.Substring (0, colon)
            if host = "" then None else Some (host.ToLowerInvariant ())

    /// Give an unlabelled offer a name. A provider that says nothing about what its stream
    /// IS still ends up with a titled panel, and the fallback names the call it came from —
    /// which is the most a client can honestly say about somebody else's resource.
    let named (fallback: string) (offer: StreamOffer) : StreamOffer =
        if offer.Ticket.Label.Trim () <> "" then offer
        else { offer with Ticket = { offer.Ticket with Label = fallback } }

    /// Whether this session may dial what a server offered it.
    ///
    /// Same HOST as the server the operator declared, and nothing else. Same host and a
    /// different PORT is ordinary — the stream leg need not share the control leg's
    /// listener — but a tool result that points the session at another machine is a
    /// declaration, and declarations are the operator's (Plan 17). Credentials in the url
    /// are refused outright rather than carried into a connect.
    ///
    /// A refusal is a string because it is going into the tool's answer: the model finds out
    /// that the stream did not open, instead of the terminal silently never appearing.
    let admit (declaredUrl: string) (offer: StreamOffer) : Result<StreamOffer, string> =
        let url = offer.Ticket.Url
        let scheme = url.ToLowerInvariant ()
        if not (scheme.StartsWith "ws://" || scheme.StartsWith "wss://") then
            Error (sprintf "a stream url must be ws:// or wss://, and this one is '%s'" url)
        elif url.Contains "@" then
            Error "a stream url may not carry credentials"
        else
            match hostOf url, hostOf declaredUrl with
            | Some offered, Some declared when offered = declared -> Ok offer
            | Some offered, Some declared ->
                Error (
                    sprintf
                        "this server is declared at %s and offered a stream on %s; a stream from another host is an operator's declaration, not a tool's answer"
                        declared
                        offered)
            | _ -> Error (sprintf "'%s' is not a url this session can dial" url)

/// Where one terminal's bytes come from.
type TerminalSource =
    /// A shell in one of this session's named WorkSandboxes — instrumentable, resizable,
    /// exit codes. Opening one IS a need, so it ensures that sandbox exists. The name rides
    /// the case rather than a parallel argument, because "which sandbox" is only a question
    /// a shell has: an attached stream is somebody else's process and has no answer.
    | SandboxShell of SandboxName
    /// A stream somebody else OFFERED (Plan 19). It does NOT ensure the sandbox: a session
    /// that only talks to a serial port should not start a container.
    ///
    /// The whole offer rather than its ticket, because "can this be asked for again" is a
    /// fact about the terminal that outlives the attach: a person whose stream ended is
    /// looking at a closed terminal, and whether there is a way back is exactly what the
    /// panel has to know.
    | Attached of StreamOffer

module TerminalSource =

    let capabilities =
        function
        | SandboxShell _ -> SourceCapabilities.shell
        | Attached offer -> offer.Ticket.Capabilities

    /// Which WorkSandbox opening this needs, if any.
    let needsSandbox =
        function
        | SandboxShell name -> Some name
        | Attached _ -> None

/// Reach a byte stream somebody else is producing, and hand back a handle shaped exactly
/// like a pty's.
///
/// `PtyHandle` and not a new type: everything downstream of the open path — the lease, the
/// input route, the resize — already speaks it, and a parallel handle would mean two of
/// every one of those. Where a source genuinely lacks something, its `SourceCapabilities`
/// says so and the corresponding member is a no-op, which is the honest reading: telling a
/// serial line to become 80x24 is not an error, it is nothing.
type AttachTerminal = AttachTicket -> int -> int -> (string -> unit) -> Async<Result<PtyHandle, string>>

module AttachTerminal =

    /// A session that can attach to nothing — the default, and what every composition that
    /// has not been given a provider gets.
    let unavailable : AttachTerminal =
        fun _ _ _ _ -> async { return Error "this session cannot attach foreign terminals" }
