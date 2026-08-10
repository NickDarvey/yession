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

/// Where one terminal's bytes come from.
type TerminalSource =
    /// A shell in one of this session's named WorkSandboxes — instrumentable, resizable,
    /// exit codes. Opening one IS a need, so it ensures that sandbox exists. The name rides
    /// the case rather than a parallel argument, because "which sandbox" is only a question
    /// a shell has: an attached stream is somebody else's process and has no answer.
    | SandboxShell of SandboxName
    /// A stream somebody else is producing. It does NOT ensure the sandbox: a session that
    /// only talks to a serial port should not start a container.
    | Attached of AttachTicket

module TerminalSource =

    let capabilities =
        function
        | SandboxShell _ -> SourceCapabilities.shell
        | Attached ticket -> ticket.Capabilities

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
