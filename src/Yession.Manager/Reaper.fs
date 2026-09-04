namespace Yession.Manager

// Which running sessions have been idle long enough to stop (Plan 11).
//
// Pure, like `QueueDrain.plan`: the Manager's sweep is then a loop over this result, and
// every rule about WHEN a session may be stopped is decidable without a clock, a timer, or
// a process. The Manager supplies `now` from its own clock — a child's idea of the time
// never enters the decision, so there is no skew to reason about.

open System
open Yession.Domain

/// Why a session was stopped. Both are reaps and both are correct; recording which is the
/// difference between a log line and a diagnosis.
type ReapReason =
    /// It said it was idle, and then stayed that way.
    | Idle
    /// It never said anything at all. A build too old to report, or a wedged process — and
    /// a session that cannot say it is busy does not get the benefit of the doubt, because
    /// the alternative is that anything which stops reporting becomes immortal.
    | NeverReported

module ReapReason =
    /// The wire/telemetry spelling (`yession.session.stop_reason`).
    let describe =
        function
        | Idle -> "idle"
        | NeverReported -> "never-reported"

/// What the Manager knows about one running launch.
type LaunchActivity =
    { SessionId : SessionId
      /// When this launch was last known to be in use. Seeded at LAUNCH — a session is
      /// busy the moment it starts, so a freshly launched one is never reaped before it
      /// has had the whole window to be used.
      LastBusyAt : DateTimeOffset
      /// Whether this launch has ever reported. Only ever distinguishes the REASON; it
      /// never buys a launch more time.
      EverReported : bool }

/// How long a session may go unused before the Manager stops it, as configured.
module IdleWindow =

    /// Parse what `--idle-timeout` was given. Blank is `None` — reaping off, the default,
    /// because stopping a session the operator did not ask to have stopped is not a
    /// behaviour to acquire by upgrading. The option being ABSENT is answered by its caller,
    /// which knows the difference between not asking and asking for nothing.
    ///
    /// `90s` / `30m` / `2h`, or a bare number meaning seconds. Rejected rather than guessed
    /// at: a window silently read as 30 seconds when 30 minutes was meant would reap
    /// sessions out from under people all day, and the misreading would look like a bug in
    /// the reaper rather than in the config.
    let parse (raw: string) : Result<TimeSpan option, string> =
        let trimmed = raw.Trim ()
        if trimmed = "" then
            Ok None
        else
            let digits, unit =
                let last = trimmed.[trimmed.Length - 1]
                if Char.IsDigit last then trimmed, "s"
                else trimmed.Substring (0, trimmed.Length - 1), string (Char.ToLowerInvariant last)
            if digits.Length = 0 || digits.Length > 9 || not (digits |> Seq.forall Char.IsDigit) then
                Error (sprintf "idle timeout '%s' is not a duration like 30m, 90s, or 2h" raw)
            else
                let value = float digits
                match unit with
                | "s" -> Ok (Some (TimeSpan.FromSeconds value))
                | "m" -> Ok (Some (TimeSpan.FromMinutes value))
                | "h" -> Ok (Some (TimeSpan.FromHours value))
                | other -> Error (sprintf "idle timeout '%s' has unknown unit '%s' — use s, m, or h" raw other)
            |> Result.bind (fun window ->
                match window with
                // Zero would reap every session on the next sweep, including one launched a
                // moment ago. That is never what anyone means, and "off" already has a
                // spelling: leave it unset.
                | Some w when w <= TimeSpan.Zero ->
                    Error (sprintf "idle timeout '%s' must be greater than zero (omit --idle-timeout to disable reaping)" raw)
                | _ -> Ok window)

module Reaper =

    /// The sessions whose idle window has elapsed, with why.
    ///
    /// `>=` rather than `>` so the boundary is the documented one: a session idle for
    /// exactly the timeout is reaped by it, not one sweep later.
    let plan
        (now: DateTimeOffset)
        (timeout: TimeSpan)
        (launches: LaunchActivity list)
        : (SessionId * ReapReason) list =
        launches
        |> List.filter (fun launch -> now - launch.LastBusyAt >= timeout)
        |> List.map (fun launch ->
            launch.SessionId, (if launch.EverReported then Idle else NeverReported))
