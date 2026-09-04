namespace Yession.Domain

open System

/// Transient-fault handling as ordinary values — what Polly is for .NET, minus the .NET.
/// Nothing in the Polly family can serve this code: it is compiled by Fable and runs in a
/// browser, and every resilience library on NuGet is CLR-only (Polly, Polly.Core, the
/// Microsoft.Extensions.*.Resilience wrappers). So the vocabulary is borrowed and the
/// mechanism is a few small pieces, all but one of them pure:
///
///   * a `Schedule` — attempt number -> how long to wait before the next attempt, `None`
///     to stop. Backoff, capping, and jitter are functions over that, so a retry policy is
///     a VALUE: composable, printable, and testable without an operation to run it against.
///   * a `Verdict` — what one fault says about trying again, which is a decision with three
///     answers rather than two (see the type).
///   * `Policy.guard` and `Policy.deadline` — decorators. Each takes an operation
///     `'a -> Async<Result<'b, 'e>>` and returns the SAME shape, so they compose with `|>`
///     in whatever order a resource wants and stay invisible to every caller downstream.
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

    /// What one fault says about trying again — Polly's "handle" clause, as a total
    /// function, with the case a predicate cannot express.
    ///
    /// `RetryAfter` is that case: providers name their own window (GitHub's
    /// `x-ratelimit-reset`, the device flow's `slow_down`), and a `bool` forced every caller
    /// that respected one to hold the wait OUTSIDE the policy — so the pace was decided in
    /// two places and the policy's schedule was quietly not the schedule. A window a
    /// provider named beats any backoff invented here, and it belongs where the rest of the
    /// retry decision is.
    type Verdict =
        /// Worth another attempt, at whatever pace the schedule sets.
        | Retry
        /// Worth another attempt, but not before this long: the provider said so.
        | RetryAfter of TimeSpan
        /// A decision rather than a hiccup. No schedule can help — an unauthorized read is
        /// never hammered, and a chunk that will not decode will not decode next time either.
        | Fatal

    /// One failed attempt, as the policy saw it: which attempt it was, what went wrong, and
    /// the delay before the next one — `None` meaning the policy is out of retries and this
    /// failure is final.
    type Attempt<'error> =
        { Number   : int
          Error    : 'error
          Retrying : TimeSpan option }

    /// How to survive a flaky operation.
    ///
    /// `Classify` decides, per fault, whether trying again can help and how soon. `Observe`
    /// is the only way out for progress the caller cannot otherwise see (attempt 3 of 5, and
    /// why) — a guarded operation is silent until it settles, which is exactly what makes it
    /// safe to compose, and exactly why degradation needs a channel of its own.
    type Policy<'error> =
        { Schedule  : Schedule
          Classify  : 'error -> Verdict
          Sleep     : TimeSpan -> Async<unit>
          Observe   : Attempt<'error> -> unit }

    module Policy =

        /// Real waiting — what the shipped composition passes for `Sleep`.
        let sleep (delay: TimeSpan) : Async<unit> = Async.Sleep (int delay.TotalMilliseconds)

        /// Bound ONE attempt in time, reporting `timedOut` when it runs over.
        ///
        /// An operation that never answers is not a failure any schedule can act on:
        /// `guard` only ever sees a settled `Error`, so a request that hangs is a policy
        /// that never runs — and, one layer up, a panel that says `working…` with nothing
        /// to press. A deadline is what turns "no answer yet" into a fault, which the
        /// policy's `Classify` then rules on like any other.
        ///
        /// Compose it INSIDE `guard` (`op |> deadline … |> guard …`), so the limit is per
        /// attempt: three attempts of ten seconds is the intended reading of a ten-second
        /// deadline under a policy with two retries. Outside it, one slow attempt would eat
        /// the whole budget and the retries would never be spent.
        ///
        /// The loser of the race is CANCELLED, not abandoned. A pending timer keeps the JS
        /// event loop alive, so a deadline that outlived every success it lost to would hold
        /// a short-lived process open for its own length after the work was done.
        let deadline
            (sleep: TimeSpan -> Async<unit>)
            (limit: TimeSpan)
            (timedOut: 'error)
            (operation: 'a -> Async<Result<'b, 'error>>)
            : 'a -> Async<Result<'b, 'error>> =
            fun input ->
                Async.FromContinuations (fun (settle, fail, _) ->
                    let losing = new System.Threading.CancellationTokenSource ()
                    // Both sides can finish; only the first is anybody's answer. The late one
                    // is dropped rather than delivered, because a continuation called twice
                    // is a caller resumed twice.
                    let mutable answered = false
                    let first (answer: unit -> unit) =
                        if not answered then
                            answered <- true
                            losing.Cancel ()
                            answer ()
                    Async.StartWithContinuations (
                        operation input,
                        (fun outcome -> first (fun () -> settle outcome)),
                        (fun error -> first (fun () -> fail error)),
                        ignore,
                        losing.Token)
                    Async.StartWithContinuations (
                        sleep limit,
                        (fun () -> first (fun () -> settle (Error timedOut))),
                        ignore,
                        ignore,
                        losing.Token))

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
                                match policy.Classify error with
                                | Fatal -> None
                                // The BUDGET stays the schedule's and the PACE becomes the
                                // provider's: a window it named replaces the delay, never
                                // the decision to stop. Otherwise a provider that answered
                                // `slow_down` for ever would buy unbounded attempts by
                                // saying so.
                                | RetryAfter delay -> policy.Schedule number |> Option.map (fun _ -> delay)
                                | Retry -> policy.Schedule number
                            policy.Observe { Number = number; Error = error; Retrying = retrying }
                            match retrying with
                            | Some delay ->
                                do! policy.Sleep delay
                                return! attempt (number + 1)
                            | None -> return Error error
                    }
                attempt 1
