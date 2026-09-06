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

    // --- What an HTTP answer says about trying again -------------------------------------

    /// One classification of an HTTP-shaped outcome, because it is one question and this
    /// repository was answering it four times: the event feed's `isTransient`, the broker's
    /// `isFinalRefusal`, the pull-request poller's `failureOf`, and a boolean pair inside
    /// sandbox verification. Four answers to "is this status worth another go" drift, and the
    /// drift is invisible — each one looks right beside the port it was written for.
    ///
    /// A resource still owns what is peculiar to IT (`invalid_grant` is a dead authorization
    /// whatever its status; a 404 on a pull request is not a hiccup), by composing over this
    /// rather than by answering it again.
    module Http =

        /// What an attempt came back as. `Unreached` is the case a status cannot express and
        /// the one most often flattened away: DNS, a refused socket, a connection dropped
        /// mid-body — nothing answered, which is the most retryable thing that can happen and
        /// reads as a fatal zero when it is folded into a status code.
        type Outcome =
            | Unreached
            | Answered of status: int * retryAfter: TimeSpan option

        /// The verdict an HTTP outcome earns on its own.
        ///
        /// A window the provider named is honoured wherever the status is one providers name
        /// one WITH — 429 and 503 by the RFC, 403 because that is how GitHub says "too many"
        /// — and ignored elsewhere, because a `retry-after` beside a 401 is a header on a
        /// decision, not a promise to reconsider it.
        ///
        /// Anything under 400 is `Fatal` for want of a fifth case rather than as a judgement:
        /// a classifier is asked about FAULTS, and an answer that already arrived is not made
        /// better by asking again.
        let verdict (outcome: Outcome) : Verdict =
            match outcome with
            | Unreached -> Retry
            | Answered (status, Some window) when status = 429 || status = 503 || status = 403 ->
                RetryAfter window
            // 5xx is the other end in trouble; 408 and 429 are it asking to be left alone
            // briefly. Every other status is a decision.
            | Answered (status, _) when status >= 500 || status = 408 || status = 429 -> Retry
            | Answered _ -> Fatal

    // --- What a provider says is left ------------------------------------------------------

    /// What a provider last said about the budget behind a credential.
    ///
    /// READ, never kept: this is the provider's own counter as of its last reply, not a tally
    /// maintained here. That distinction is the whole design. A count of our own would have to
    /// know every rule the provider prices requests by — GitHub does not charge for a
    /// conditional request that answers 304, does charge for the redirect it serves on a
    /// renamed repo, and charges once more for the request that follows it — and any rule we
    /// failed to learn would show up as a tally that drifts from the truth in the direction of
    /// spending more than we think. The header is already right, and it is already SHARED: a
    /// budget belongs to a credential rather than to a process, so a reply here reports what
    /// every other process holding that credential has spent too, with nothing to coordinate.
    ///
    /// Two states and no third, because remaining and reset are only meaningful together — a
    /// number with no window to wait for is a hold nobody can end.
    type Allowance =
        /// No reply has been read yet.
        | Unknown
        /// What the last reply said: how many are left, and when the window turns over.
        | Seen of remaining: int * resets: DateTimeOffset

    /// What a call is FOR, which is the only thing that decides whether it may spend the last
    /// of a budget.
    ///
    /// One pooled allowance means a poller can starve the verb a person is waiting on: they
    /// draw on the same number, and the poller draws on it every few seconds while nobody
    /// watches. So background work stops early and leaves a reserve, and work somebody asked
    /// for spends it.
    type Spend =
        | Background
        | Foreground

    /// Whether a call may be made now, and when to come back if not. A moment rather than a
    /// delay, because the provider names a moment and everything downstream schedules on one.
    type Permit =
        | Go
        | Hold of until: DateTimeOffset

    /// How much of a budget background work must leave for everything else.
    type Limits = { Reserve : int }

    module Allowance =

        /// Fold a reply's reading into what was held.
        ///
        /// A reply that said nothing (no headers, or headers for another of the provider's
        /// buckets) teaches nothing and leaves the held reading alone — it is not evidence
        /// that the budget is unknown, only that this reply did not mention it.
        ///
        /// Within one window a budget only falls, so the SMALLER reading wins: replies to
        /// concurrent calls settle in whatever order the network gives them, and believing a
        /// larger number that arrived late is how a client spends what it has already spent.
        /// A later window replaces the reading outright, and an earlier one is a straggler
        /// from a window that has already turned over.
        let observed (reading: Allowance) (held: Allowance) : Allowance =
            match reading, held with
            | Unknown, _ -> held
            | _, Unknown -> reading
            | Seen (fresh, freshResets), Seen (kept, keptResets) ->
                if freshResets > keptResets then reading
                elif freshResets < keptResets then held
                else Seen (min fresh kept, keptResets)

    module Quota =

        /// May a call of this class go now?
        ///
        /// Unknown allows: no reply has been read, so there is no evidence to hold on, and a
        /// ledger that refused until it had some would refuse the very call that would get it.
        /// A window that has turned over allows for the same reason — a spent number from a
        /// window in the past is not news about this one, and waiting on it is waiting for a
        /// moment that has been and gone.
        let decide (now: DateTimeOffset) (limits: Limits) (spend: Spend) (allowance: Allowance) : Permit =
            match allowance with
            | Unknown -> Go
            | Seen (_, resets) when resets <= now -> Go
            | Seen (remaining, resets) ->
                let floor = match spend with | Background -> limits.Reserve | Foreground -> 0
                if remaining > floor then Go else Hold resets

    /// One credential's ledger: the last reading, kept.
    ///
    /// The only stateful thing here, and deliberately the smallest — a cell holding one
    /// `Allowance`. It is created at the composition root and handed to everything that
    /// spends, so a process holds ONE reading per credential rather than one per caller:
    /// the poller learns what the verb just spent, and the verb learns what the poller did.
    ///
    /// Every decision over it stays a pure function of the reading (`Quota.decide`), which is
    /// what keeps the whole rule in the cheap tier: the cell only remembers.
    type Ledger =
        private
            { /// What the last reply said.
              Read : unit -> Allowance
              /// Fold in what a reply just said.
              Observed : Allowance -> unit }

    module Ledger =

        let create () : Ledger =
            let mutable held = Unknown
            { Read = fun () -> held
              Observed = fun reading -> held <- Allowance.observed reading held }

        /// What a reply taught. Called for EVERY reply, including the ones that cost nothing:
        /// a conditional request answering 304 is free and still carries the counter, so a
        /// cadence that spends nothing still keeps the reading current.
        let observed (ledger: Ledger) (reading: Allowance) : unit = ledger.Observed reading

        /// What the ledger says about a call of this class, right now.
        let permit (ledger: Ledger) (now: DateTimeOffset) (limits: Limits) (spend: Spend) : Permit =
            Quota.decide now limits spend (ledger.Read ())

        /// What it is holding, for whoever reports rather than decides.
        let reading (ledger: Ledger) : Allowance = ledger.Read ()

    // --- The circuit ----------------------------------------------------------------------

    /// What a breaker did, for whoever is watching a resource rather than a call. A refusal
    /// costs nothing and says nothing to the caller beyond its own error, so without this the
    /// difference between "the provider is down" and "we stopped asking" is invisible.
    type BreakerEvent =
        /// The circuit opened: nothing is asked until this moment.
        | BreakerOpened of until: DateTimeOffset
        /// A call was turned away at the door, unmade.
        | BreakerRefused of until: DateTimeOffset
        /// A trial got through and the resource answered. Asking resumes.
        | BreakerClosed

    /// When to stop asking a resource that is not answering.
    ///
    /// A retry policy answers "is this call worth making again"; this answers the question
    /// after it — "is this RESOURCE worth calling at all just now". They are not the same
    /// question, and a policy alone cannot ask the second: every caller retries its own way
    /// into a provider that is down, so a dead dependency costs its full backoff budget per
    /// caller, per call, for as long as it stays dead.
    ///
    /// `Trips` is why this has a classifier of its own rather than reusing `Verdict`. A fault
    /// can be worth no retry and still say nothing about the resource's health: one
    /// credential's 401 is that exactly, and counting it would let one bad token stop
    /// everybody else's calls. Evidence the RESOURCE is unwell is a narrower thing than a
    /// fault a retry cannot fix.
    ///
    /// The clock is a port for the reason `Sleep` is: an entire open-then-recover sequence is
    /// then asserted without waiting for one.
    type BreakerPolicy<'error> =
        { /// Consecutive tripping failures that open the circuit.
          Failures : int
          /// How long it stays open before ONE attempt is let through.
          Reset    : TimeSpan
          /// Which failures count as evidence about the resource itself.
          Trips    : 'error -> bool
          Now      : unit -> DateTimeOffset
          Observe  : BreakerEvent -> unit }

    /// One resource's circuit. The state is mutable and deliberately opaque: a breaker IS the
    /// memory of what just happened to a resource, and a value that forgot it between calls
    /// would be a breaker that never opens. `Link.supervise` holds its liveness state the
    /// same way and for the same reason.
    ///
    /// Held per RESOURCE, not per operation: github.com being down is one fact, so the device
    /// flow's begin and its poll share a circuit and the first one to find the provider gone
    /// spares the other the trip.
    type Breaker<'error> =
        private
            { /// May a call go through now? `Error` carries the moment asking resumes.
              Admit   : unit -> Result<unit, DateTimeOffset>
              /// How the call that was admitted went. Every admission is settled exactly
              /// once, or the trial slot never comes back.
              Settled : Result<unit, 'error> -> unit }

    module Breaker =

        /// A breaker that counts every settled failure — the right default for a resource
        /// whose faults are all about reaching it.
        let everyFailure : 'error -> bool = fun _ -> true

        /// Open a circuit over one resource.
        let create (policy: BreakerPolicy<'error>) : Breaker<'error> =
            // Closed with a streak of `failures`; open until `openUntil`; and `trying` for
            // the one trial admitted once that moment passes. Three facts rather than a
            // three-case state, because the streak SURVIVES the open window: a trial that
            // fails re-opens immediately rather than spending the whole budget again.
            let mutable failures = 0
            let mutable openUntil : DateTimeOffset option = None
            let mutable trying = false
            let admit () =
                match openUntil with
                | None -> Ok ()
                | Some until when policy.Now () < until ->
                    policy.Observe (BreakerRefused until)
                    Error until
                | Some until ->
                    // The window has passed. Exactly one call goes through to find out
                    // whether the resource is back; the rest are still turned away, because
                    // a queue released all at once is the stampede this exists to prevent.
                    if trying then
                        policy.Observe (BreakerRefused until)
                        Error until
                    else
                        trying <- true
                        Ok ()
            let settled outcome =
                trying <- false
                match outcome with
                | Ok () ->
                    if openUntil.IsSome then policy.Observe BreakerClosed
                    failures <- 0
                    openUntil <- None
                | Error error when not (policy.Trips error) -> ()
                | Error _ ->
                    failures <- failures + 1
                    if failures >= policy.Failures then
                        let until = (policy.Now ()).Add policy.Reset
                        openUntil <- Some until
                        policy.Observe (BreakerOpened until)
            { Admit = admit; Settled = settled }

        /// Decorate an operation with a breaker's verdict, same shape in and out. `refused`
        /// builds the error a turned-away call reports, from the moment asking resumes — so
        /// what reaches a person is "not until 12:05", never a silent nothing.
        ///
        /// Compose it OUTSIDE `guard`, so the circuit counts SETTLED failures — an operation
        /// that failed once and succeeded on its retry is a resource that works, and a
        /// breaker fed each attempt would open on it.
        let guard
            (breaker: Breaker<'error>)
            (refused: DateTimeOffset -> 'error)
            (operation: 'a -> Async<Result<'b, 'error>>)
            : 'a -> Async<Result<'b, 'error>> =
            fun input ->
                async {
                    match breaker.Admit () with
                    | Error until -> return Error (refused until)
                    | Ok () ->
                        let! outcome = operation input
                        breaker.Settled (outcome |> Result.map ignore)
                        return outcome
                }
