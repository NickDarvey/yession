namespace Yession.Domain.Prs

open Yession.Domain

/// The facts a watched pull request records. Provider-lean like `RepoRef`: "pull
/// request" is a term every forge speaks, and nothing here names an endpoint — the
/// GitHub REST knowledge that produces these values lives in the session host
/// (`app/GitHubPrs.fs`), the way `RepoRef.cloneUrl` keeps github.com out of the types
/// that carry a repo. They sit BELOW `SessionEvent` because the union names them, and
/// the projection that folds that union (`PrWatches.fs`) sits above it.

/// One pull request, named the way `add_repo` names a repo: owner/repo plus number.
type PrRef = { Repo : RepoRef; Number : int }

module PrRef =

    /// Numbers are provider-assigned and start at 1; zero or negative is a paste mistake
    /// worth refusing before it becomes a watch that can never resolve.
    let create (repo: RepoRef) (number: int) : Result<PrRef, string> =
        if number >= 1 then Ok { Repo = repo; Number = number }
        else Error (sprintf "%d is not a pull request number" number)

    /// The canonical rendering — "owner/repo#12" — used by notes, gates and queries alike.
    let render (pr: PrRef) : string = sprintf "%s#%d" (RepoRef.value pr.Repo) pr.Number

type PrState =
    | PrOpen
    | PrMerged
    | PrClosed

module PrState =
    let describe (state: PrState) : string =
        match state with
        | PrOpen -> "open"
        | PrMerged -> "merged"
        | PrClosed -> "closed"

/// The checks rollup on the head commit. `ChecksNone` is its own case rather than a
/// pending that never resolves: a commit with zero check runs is common (no CI
/// configured, or none triggered), and "pending forever" would be a lie about it.
type ChecksRollup =
    | ChecksNone
    | ChecksPending
    | ChecksGreen
    | ChecksRed

module ChecksRollup =
    let describe (rollup: ChecksRollup) : string =
        match rollup with
        | ChecksNone -> "no checks"
        | ChecksPending -> "checks pending"
        | ChecksGreen -> "checks green"
        | ChecksRed -> "checks red"

/// What one look at the provider answered. Minimal on purpose: `Mergeable` is carried
/// for the query surface and NEVER drives a transition — GitHub computes it lazily and
/// answers null until it has, so a fact this unreliable may be displayed but never
/// announced.
type PrSnapshot =
    { State : PrState
      Title : string
      HeadSha : string
      Checks : ChecksRollup
      Mergeable : bool option }

/// A watched pull request's state changes — the vocabulary grows HERE, not inside
/// `SessionEvent`, which carries one `PrTransitioned` case whatever is announced.
type PrTransition =
    | PrWasMerged
    | PrWasClosed
    | PrWasReopened
    | ChecksTurnedGreen
    | ChecksTurnedRed

module PrTransition =
    let describe (transition: PrTransition) : string =
        match transition with
        | PrWasMerged -> "was merged"
        | PrWasClosed -> "was closed"
        | PrWasReopened -> "was reopened"
        | ChecksTurnedGreen -> "checks went green"
        | ChecksTurnedRed -> "checks went red"

// --- event payloads (the RepoFacts shape: MessageId + payload + attribution) -----------

/// A party started watching a pull request.
type PrWatchStarted =
    { /// The timeline note's identity, minted by the Process at append time.
      MessageId : MessageId
      Pr : PrRef
      /// The state at the moment the watch began — the durable BASELINE transition
      /// detection compares against. Folded from the log (`PrWatches.fs`), this is what
      /// makes a restart re-announce nothing and a merge that happened while the process
      /// was down still get announced: the log says what was last known, not memory.
      Initial : PrSnapshot
      /// Whose watch: the note's attribution, the credential every later poll resolves
      /// on behalf of, and the actor any wake this watch causes would run as.
      Actor : ActorRef }

and PrWatchStopped =
    { MessageId : MessageId
      Pr : PrRef
      Actor : ActorRef }

/// The session OBSERVED a watched pull request change. Appended by the Process under
/// `ActorRef.System` — nobody in the session did it — while the payload names whose
/// watch noticed, because the projection reads events, not envelopes, and "whose news
/// is this" is the fact attribution and credentials both hang off.
and PrTransitioned =
    { MessageId : MessageId
      Pr : PrRef
      Transition : PrTransition
      /// The state after the change, carried so a note can say it without a query —
      /// and so the fold can advance its baseline from the event alone.
      State : PrState
      Checks : ChecksRollup
      Watcher : ActorRef }
