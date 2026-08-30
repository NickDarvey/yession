namespace Yession.Domain.Prs

open System
open Yession.Domain

/// The session's watched pull requests, projected from events — `ReposProjection`'s
/// sibling, and the durable half of transition detection. The poller compares fresh
/// provider snapshots against `Known`, which folds from the LOG (a watch's `Initial`
/// advanced by each recorded `PrTransitioned`), never from process memory: a restart
/// re-folds the same log and re-announces nothing, while a change that happened during
/// the downtime is still detected, because the log still says the state before it.

/// Where a pull request stands with the thing that would merge it for us.
///
/// `Stalled` is not something a provider reports — it is `Queued` followed by not queued,
/// on a pull request still open, which is what a merge queue ejecting an entry looks like
/// from outside. So it can only be known from HISTORY, which is why it lives in the
/// baseline rather than in the snapshot.
type PrQueue =
    | NotQueued
    | Queued
    | Stalled

/// What the log has recorded about one pull request: the baseline the next detection
/// compares against. Deliberately not the whole snapshot — title, head sha and
/// mergeability are display facts whose movement is not news.
type PrKnown =
    { State : PrState
      Checks : ChecksRollup
      Queue : PrQueue }

/// The one word for where a pull request stands, and how loudly to say it. ONE home,
/// because the settings panel, the roster summary and the header strip must not each
/// invent their own vocabulary for the same fact — they read it from here.
module PrStatus =

    /// The last thing that happened to this pull request, in a single past-tense word.
    /// Queue first while it is open, because "queued" and "stalled" are the news; a
    /// merged or closed pull request has stopped caring what any queue thought.
    let word (queue: PrQueue) (state: PrState) : string =
        match state with
        | PrMerged -> "merged"
        | PrClosed -> "closed"
        | PrOpen ->
            match queue with
            | Queued -> "queued"
            | Stalled -> "stalled"
            | NotQueued -> "open"

    /// What a watch says when the session cannot currently read it — a dead credential, a
    /// pull request it cannot see, a rate-limit window. The panel's status column says
    /// WHICH; a one-line summary has room only for the fact that nobody is driving this
    /// one, and for a worse reason than a stall.
    let unreachable : string = "unreachable"

    /// Worst first. What "worst" means here is how much it wants a person: an unreachable
    /// watch is not being driven at all, a stalled pull request has nobody driving it, an
    /// open one is waiting on somebody, a queued one is waiting on machines, and merged or
    /// closed is over.
    let order : string list = [ unreachable; "stalled"; "open"; "queued"; "merged"; "closed" ]

    /// A pull request that is still owed. Merged and closed ones are history: they are why
    /// a summary of six watches can honestly be silent.
    let live (word: string) : bool = word <> "merged" && word <> "closed"

    /// Which of two status words wants a person more. Unknown words rank last rather than
    /// first: a surface should not shout about a word this module has never heard of.
    let worse (left: string) (right: string) : string =
        let rank word =
            match order |> List.tryFindIndex (fun w -> w = word) with
            | Some index -> index
            | None -> List.length order
        if rank left <= rank right then left else right

    /// One line a session says about its pull requests, for a surface that has room for
    /// one line and no more — the Manager's roster, the session page's header strip.
    ///
    /// Only what is still OWED is counted. A session whose watches have all merged has
    /// nothing to say, and says nothing, rather than reporting a number that is really a
    /// history. Silence here means "nothing waiting", which is what makes a line that IS
    /// there worth reading.
    let summarize (standings: (PrRef * string) list) : string =
        match standings |> List.filter (snd >> live) with
        | [] -> ""
        // One is named, because with a single pull request the number IS the answer and a
        // count of one says less than the thing it counted.
        | [ pr, word ] -> sprintf "#%d %s" pr.Number word
        | several ->
            let worst = several |> List.map snd |> List.reduce worse
            sprintf "%d PRs · %d %s" (List.length several) (several |> List.filter (snd >> (=) worst) |> List.length) worst

type PrWatch =
    { Pr : PrRef
      /// Whose watch — see `PrWatched.Actor`.
      Watcher : ActorRef
      Known : PrKnown
      /// When this pull request last became what it now is: the envelope timestamp of the
      /// watch's start, advanced by each recorded transition. Read from the LOG for the
      /// baseline's reason — a poll that finds nothing new must not make a watch look
      /// fresher than it is, and only an event says something happened.
      Since : DateTimeOffset }

type PrWatchesProjection = { Watches : PrWatch list }

module PrTransitions =

    /// The baseline a watch starts from: its `Initial` snapshot, reduced to what
    /// transitions are detected on.
    ///
    /// A watch that begins on an already-ejected pull request reads `NotQueued`, not
    /// `Stalled`, and that is honest: nobody watching saw it fall out, and claiming
    /// otherwise would announce a stall that this session cannot know happened.
    let knownOf (snapshot: PrSnapshot) : PrKnown =
        { State = snapshot.State
          Checks = snapshot.Checks
          Queue = (if snapshot.Queued then Queued else NotQueued) }

    /// Advance a baseline by one announced transition — the projection's fold, and the
    /// poller's, so the two cannot disagree about what has been said.
    let advance (known: PrKnown) (transition: PrTransition) : PrKnown =
        match transition with
        | PrTransition.Merged -> { known with State = PrMerged }
        | PrTransition.Closed -> { known with State = PrClosed }
        | PrTransition.Reopened -> { known with State = PrOpen }
        | PrTransition.ChecksPassed -> { known with Checks = ChecksGreen }
        | PrTransition.ChecksFailed -> { known with Checks = ChecksRed }
        | PrTransition.Queued -> { known with Queue = Queued }
        | PrTransition.Stalled -> { known with Queue = Stalled }

    /// What a fresh snapshot means against the last recorded baseline: at most one state
    /// transition, at most one checks transition and at most one queue transition, in
    /// that order.
    ///
    /// Only ARRIVALS at green or red are checks news — a new push resetting checks to
    /// pending is the ordinary rhythm of work, not an announcement. And checks movement
    /// on a pull request that is no longer open is suppressed entirely: CI going red on
    /// a merged PR is not something the watcher can act on from here.
    let detect (known: PrKnown) (fresh: PrSnapshot) : PrTransition list =
        let state =
            match known.State, fresh.State with
            | PrOpen, PrMerged -> [ PrTransition.Merged ]
            | PrOpen, PrClosed -> [ PrTransition.Closed ]
            | PrClosed, PrOpen -> [ PrTransition.Reopened ]
            // Closed-to-merged: GitHub reports a merged PR as closed+merged, so a watch
            // whose baseline is closed learning of a merge is real (reopened-then-merged
            // between polls collapses to this) and merged is the fact that matters.
            | PrClosed, PrMerged -> [ PrTransition.Merged ]
            | _ -> []
        let stateAfter = state |> List.fold advance known
        let checks =
            match stateAfter.State with
            | PrOpen ->
                match known.Checks, fresh.Checks with
                | ChecksGreen, ChecksGreen
                | ChecksRed, ChecksRed -> []
                | _, ChecksGreen -> [ PrTransition.ChecksPassed ]
                | _, ChecksRed -> [ PrTransition.ChecksFailed ]
                | _ -> []
            | PrMerged | PrClosed -> []
        // Queue news, on the same terms as checks news and for the same reason: a merged
        // pull request left the queue by going through it, and saying "stalled" about that
        // would be reporting the success as a failure. A re-arm after a stall announces
        // `Queued` again, because it is again true that nobody is needed.
        let queue =
            match stateAfter.State with
            | PrOpen ->
                match known.Queue, fresh.Queued with
                | Queued, true -> []
                | _, true -> [ PrTransition.Queued ]
                | Queued, false -> [ PrTransition.Stalled ]
                | _, false -> []
            | PrMerged | PrClosed -> []
        state @ checks @ queue

module PrWatchesProjection =

    let empty : PrWatchesProjection = { Watches = [] }

    /// Fold one event. Re-watching an existing pull request replaces its entry (the
    /// newest baseline wins — the `add_repo` rule); a transition advances that watch's
    /// baseline by exactly what was announced.
    let applyEvent (proj: PrWatchesProjection) (envelope: EventEnvelope<SessionEvent>) : PrWatchesProjection =
        match envelope.Event with
        | PrWatched p ->
            let entry =
                { Pr = p.Pr
                  Watcher = p.Actor
                  Known = PrTransitions.knownOf p.Initial
                  Since = envelope.Timestamp }
            if proj.Watches |> List.exists (fun w -> w.Pr = p.Pr) then
                { Watches = proj.Watches |> List.map (fun w -> if w.Pr = p.Pr then entry else w) }
            else
                { Watches = proj.Watches @ [ entry ] }
        | PrUnwatched p ->
            { Watches = proj.Watches |> List.filter (fun w -> w.Pr <> p.Pr) }
        | PrTransitioned p ->
            { Watches =
                proj.Watches
                |> List.map (fun w ->
                    if w.Pr = p.Pr then
                        { w with
                            Known = PrTransitions.advance w.Known p.Transition
                            Since = envelope.Timestamp }
                    else w) }
        | _ -> proj

    let tryFind (pr: PrRef) (proj: PrWatchesProjection) : PrWatch option =
        proj.Watches |> List.tryFind (fun w -> w.Pr = pr)
