namespace Yession.Manager

// How a session's listening port is chosen (Plan 11).
//
// A session's port is part of its ORIGIN, and a browser partitions storage by origin. So a
// session that comes back on a different port comes back to an EMPTY IndexedDB, with
// whatever its user wrote while offline stranded in a database nothing will ever open
// again. That is survivable while a resume is a rare, deliberate act. It is not survivable
// once idle sessions are reaped on a timer, which is what Plan 11 introduces.
//
// Both answers are legitimate — a laptop that never serves the same browser twice wants no
// configuration at all — so the choice is a VALUE, not a flag on the side. `Ephemeral` is
// the default and behaves exactly as every release so far; `Pinned` buys address stability
// with a reserved range the operator undertakes to keep clear.
//
// Neither can be half-configured: `Pinned` cannot exist without a range, `PortRange` has no
// public constructor, and `SessionPorts.create` is the only way to obtain either. A caller
// that holds a `Pinned` therefore holds a range that was validated at boot, and no code
// path can ask for a pinned port and discover none was configured.

open System

/// A contiguous, non-empty range of unprivileged TCP ports.
type PortRange = private PortRange of first: int * last: int

module PortRange =

    let first (PortRange (f, _)) = f
    let last (PortRange (_, l)) = l
    let contains (port: int) (PortRange (f, l)) = port >= f && port <= l

    /// Every port in the range, ascending.
    let toList (PortRange (f, l)) = [ f..l ]

    /// How the range is written in configuration — round-trips through `SessionPorts.create`.
    let describe (PortRange (f, l)) = if f = l then string f else sprintf "%d-%d" f l

/// How a session's listening port is chosen.
type SessionPorts =
    /// OS-assigned per launch. Zero configuration, and a session's address — with the
    /// client-side storage partitioned by it — does not survive a relaunch.
    | Ephemeral
    /// One address for the life of the session, drawn from a reserved range.
    | Pinned of PortRange

module SessionPorts =

    /// Privileged ports need privilege the Manager deliberately does not have (it is a user
    /// agent, not a daemon), so a range that reaches below 1024 is a configuration error
    /// rather than a permission error at the first launch.
    let private lowest = 1024
    let private highest = 65535

    /// Total: every rejection names the offending text. `int` is only reached once the
    /// string is known to be short and all-digits, so it cannot throw or overflow.
    let private parsePort (label: string) (raw: string) : Result<int, string> =
        if raw.Length = 0 || raw.Length > 5 || not (raw |> Seq.forall Char.IsDigit) then
            Error (sprintf "%s '%s' is not a port number" label raw)
        else
            let value = int raw
            if value < lowest || value > highest then
                Error (sprintf "%s %d is outside %d-%d" label value lowest highest)
            else
                Ok value

    /// The ONLY constructor. Unset (or blank) is `Ephemeral` — the zero-config default, so
    /// an operator who has never heard of this setting gets today's behaviour exactly.
    ///
    /// `managerPort` is a parameter rather than something checked later because a range that
    /// contains the Manager's own port is a deployment that works until the day a session is
    /// allocated 8321 and the Manager cannot rebind after a restart. That belongs in a
    /// refused boot, next to the typo that caused it.
    let create (managerPort: int) (raw: string) : Result<SessionPorts, string> =
        let trimmed = raw.Trim ()
        if trimmed = "" then
            Ok Ephemeral
        else
            let bounds =
                match trimmed.Split '-' with
                | [| single |] -> parsePort "session port" single |> Result.map (fun p -> p, p)
                | [| a; b |] ->
                    parsePort "session port range start" a
                    |> Result.bind (fun f -> parsePort "session port range end" b |> Result.map (fun l -> f, l))
                | _ ->
                    Error (
                        sprintf
                            "session port range '%s' is not a port or a <first>-<last> range"
                            trimmed
                    )
            match bounds with
            | Error e -> Error e
            | Ok (f, l) when f > l ->
                Error (sprintf "session port range %d-%d ends before it starts" f l)
            | Ok (f, l) when managerPort >= f && managerPort <= l ->
                Error (
                    sprintf
                        "session port range %d-%d contains the Manager's own port %d — the Manager could not rebind after a session took it"
                        f
                        l
                        managerPort
                )
            | Ok (f, l) -> Ok (Pinned (PortRange (f, l)))
