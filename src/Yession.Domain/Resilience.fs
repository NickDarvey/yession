namespace Yession.Domain

open System

/// Transient-fault handling as ordinary values — what Polly is for .NET, minus the .NET.
/// Nothing in the Polly family can serve this code: it is compiled by Fable and runs in a
/// browser, and every resilience library on NuGet is CLR-only (Polly, Polly.Core, the
/// Microsoft.Extensions.*.Resilience wrappers). So the vocabulary is borrowed and the
/// mechanism is two small pieces, both pure:
///
///   * a `Schedule` — attempt number -> how long to wait before the next attempt, `None`
///     to stop. Backoff, capping, and jitter are functions over that, so a retry policy is
///     a VALUE: composable, printable, and testable without an operation to run it against.
///   * `Policy.guard` — the one interpreter. It decorates `'a -> Async<Result<'b, 'e>>` and
///     returns the SAME shape. That is what lets resilience be composed once, where the
///     transport is assembled, and stay invisible to every caller downstream.
///
/// Waiting is injected (`Policy.Sleep`), never ambient, and so is jitter's entropy. A test
/// therefore drives a policy through its whole backoff sequence in zero real time — the
/// only way retry logic gets deterministic coverage instead of a sleep and a hope.
module Resilience =

    /// The delay before attempt `n + 1`, given that attempt `n` (1-based) just failed.
    /// `None` retires the schedule: no further attempt is made.
    type Schedule = int -> TimeSpan option

    module Schedule =

        /// `retries` further attempts, each one `delay` after the last failure.
        let constant (delay: TimeSpan) (retries: int) : Schedule =
            fun attempt -> if attempt <= retries then Some delay else None

        /// Exponential backoff: `first`, then multiplied by `factor` after each failure,
        /// never above `cap`, for `retries` further attempts.
        let exponential (first: TimeSpan) (factor: float) (cap: TimeSpan) (retries: int) : Schedule =
            fun attempt ->
                if attempt > retries then None
                else
                    let grown = first.TotalMilliseconds * (factor ** float (attempt - 1))
                    Some (TimeSpan.FromMilliseconds (min grown cap.TotalMilliseconds))

        /// Spread an inner schedule's delays uniformly over `[(1 - spread)·d, d]`, so peers
        /// that failed together come back apart. That case is real here: a Session Process
        /// restart drops every peer of a session at the same instant, and an unjittered
        /// backoff would have all of them retry the tail chunk in lockstep, forever in step.
        /// `random` yields uniform `[0, 1)`.
        let jittered (spread: float) (random: unit -> float) (inner: Schedule) : Schedule =
            fun attempt ->
                inner attempt
                |> Option.map (fun delay ->
                    let full = delay.TotalMilliseconds
                    TimeSpan.FromMilliseconds (full - full * spread * random ()))

    /// One failed attempt, as the policy saw it: which attempt it was, what went wrong, and
    /// the delay before the next one — `None` meaning the policy is out of retries and this
    /// failure is final.
    type Attempt<'error> =
        { Number   : int
          Error    : 'error
          Retrying : TimeSpan option }

    /// How to survive a flaky operation.
    ///
    /// `Retryable` is the "handle" clause: a fault the policy does not handle fails on its
    /// first attempt, so an unauthorized read is never hammered. `Observe` is the only way
    /// out for progress the caller cannot otherwise see (attempt 3 of 5, and why) — a
    /// guarded operation is silent until it settles, which is exactly what makes it safe to
    /// compose, and exactly why degradation needs a channel of its own.
    type Policy<'error> =
        { Schedule  : Schedule
          Retryable : 'error -> bool
          Sleep     : TimeSpan -> Async<unit>
          Observe   : Attempt<'error> -> unit }

    module Policy =

        /// Real waiting — what the shipped composition passes for `Sleep`.
        let sleep (delay: TimeSpan) : Async<unit> = Async.Sleep (int delay.TotalMilliseconds)

        /// Decorate an operation so handled faults are retried on the policy's schedule.
        /// The result has the SAME type as the operation: the caller keeps its railway and
        /// learns nothing about retrying. A success is returned as-is; once the schedule
        /// retires, the LAST error is returned — a settled outcome, never a fabricated
        /// success.
        let guard
            (policy: Policy<'error>)
            (operation: 'a -> Async<Result<'b, 'error>>)
            : 'a -> Async<Result<'b, 'error>> =
            fun input ->
                let rec attempt (number: int) =
                    async {
                        match! operation input with
                        | Ok value -> return Ok value
                        | Error error ->
                            let retrying =
                                if policy.Retryable error then policy.Schedule number else None
                            policy.Observe { Number = number; Error = error; Retrying = retrying }
                            match retrying with
                            | Some delay ->
                                do! policy.Sleep delay
                                return! attempt (number + 1)
                            | None -> return Error error
                    }
                attempt 1
