namespace Yession.Domain.Prs

open Yession.Domain

/// The session's watched pull requests, projected from events — `ReposProjection`'s
/// sibling, and the durable half of transition detection. The poller compares fresh
/// provider snapshots against `Known`, which folds from the LOG (a watch's `Initial`
/// advanced by each recorded `PrTransitioned`), never from process memory: a restart
/// re-folds the same log and re-announces nothing, while a change that happened during
/// the downtime is still detected, because the log still says the state before it.

/// What the log has recorded about one pull request: the baseline the next detection
/// compares against. Deliberately not the whole snapshot — title, head sha and
/// mergeability are display facts whose movement is not news.
type PrKnown =
    { State : PrState
      Checks : ChecksRollup }

type PrWatch =
    { Pr : PrRef
      /// Whose watch — see `PrWatched.Actor`.
      Watcher : ActorRef
      Known : PrKnown }

type PrWatchesProjection = { Watches : PrWatch list }

module PrTransitions =

    /// The baseline a watch starts from: its `Initial` snapshot, reduced to what
    /// transitions are detected on.
    let knownOf (snapshot: PrSnapshot) : PrKnown =
        { State = snapshot.State; Checks = snapshot.Checks }

    /// Advance a baseline by one announced transition — the projection's fold, and the
    /// poller's, so the two cannot disagree about what has been said.
    let advance (known: PrKnown) (transition: PrTransition) : PrKnown =
        match transition with
        | PrTransition.Merged -> { known with State = PrMerged }
        | PrTransition.Closed -> { known with State = PrClosed }
        | PrTransition.Reopened -> { known with State = PrOpen }
        | PrTransition.ChecksPassed -> { known with Checks = ChecksGreen }
        | PrTransition.ChecksFailed -> { known with Checks = ChecksRed }

    /// What a fresh snapshot means against the last recorded baseline: at most one state
    /// transition and at most one checks transition, state first.
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
        state @ checks

module PrWatchesProjection =

    let empty : PrWatchesProjection = { Watches = [] }

    /// Fold one event. Re-watching an existing pull request replaces its entry (the
    /// newest baseline wins — the `add_repo` rule); a transition advances that watch's
    /// baseline by exactly what was announced.
    let applyEvent (proj: PrWatchesProjection) (event: SessionEvent) : PrWatchesProjection =
        match event with
        | PrWatched p ->
            let entry = { Pr = p.Pr; Watcher = p.Actor; Known = PrTransitions.knownOf p.Initial }
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
                    if w.Pr = p.Pr then { w with Known = PrTransitions.advance w.Known p.Transition }
                    else w) }
        | _ -> proj

    let tryFind (pr: PrRef) (proj: PrWatchesProjection) : PrWatch option =
        proj.Watches |> List.tryFind (fun w -> w.Pr = pr)
