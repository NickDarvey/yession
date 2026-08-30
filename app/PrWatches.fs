module Yession.Host.PrWatches

// Watching a pull request, with nothing in it that names a forge. The Domain says what a
// pull request IS (`PrFacts.fs`) and what its movement MEANS (`PrWatches.fs`); this says
// how a session keeps looking: the cadence, the ETag bookkeeping, the in-flight guard, the
// verbs that start and stop a watch, and the query all of it reads back through.
//
// The whole provider surface is `FetchPr` — one function, one look — plus a `provider`
// label the error copy is written around, because "github rejected this credential" is a
// sentence a person has to read and "the provider rejected this credential" is not. A
// second forge is a second `fetchOver` and a second hook filter (`GitHubPrs.fs` is the
// first), and nothing in this file changes to admit it.
//
// Polling, not webhooks, and that is a decision rather than a stopgap: a repo webhook
// needs admin on every repo somebody wants watched, and inbound delivery needs a
// deployment the provider can reach — which the loopback default is not. A settled watch
// costs two conditional GETs that both answer 304, which is free (GitHub does not count
// one against the rate limit), and it works in every deployment shape there is. `FetchPr`
// is where a future push transport plugs in without anything downstream noticing.

open System
open Yession.Domain
open Yession.Domain.Prs
open Yession.Domain.Tools

/// The ETags a watch carries between polls, one per endpoint. The checks ETag is
/// implicitly per head commit: its URL names the sha, so a push moves the URL and the
/// stale ETag simply never matches.
type PrEtags = { Pr : string; Checks : string }

module PrEtags =
    let none : PrEtags = { Pr = ""; Checks = "" }

/// Why a look at the provider produced no snapshot, folded to what the poller acts on.
type PrFetchFailure =
    /// The credential is dead: the one failure that is news to the broker.
    | PrUnauthorized
    /// Gone, or a credential that cannot see it — a provider cannot tell those apart,
    /// which is why this is one case and the message says so.
    | PrNotFound
    /// Rate limited, with the epoch second the provider says the window resets at, when
    /// it said one.
    | PrRateLimited of resetEpoch: int option
    | PrUnreachable of string

type PrFetchOutcome =
    | PrChanged of PrSnapshot * PrEtags
    /// Both conditional requests answered 304 — nothing to fold, nothing to say.
    | PrUnchanged
    | PrFetchFailed of PrFetchFailure

/// THE SEAM: one look at one pull request, with whatever credential the caller resolved
/// and whatever it last knew — the ETags to ask conditionally with, and the snapshot they
/// were taken alongside. Both, because the two halves of a look move independently: the
/// snapshot is what fills in the half that answered 304.
///
/// The poller, the watch verb and every test hold this signature, so replacing polling
/// with a pushed stream later replaces an implementation rather than a design.
type FetchPr = string option -> PrRef -> PrEtags -> PrSnapshot option -> Async<PrFetchOutcome>

// --- the poller --------------------------------------------------------------------------

/// One watched pull request as the `pull_requests` query reports it.
type PrWatchRow =
    { Pr : PrRef
      Watcher : ActorRef
      Snapshot : PrSnapshot option
      /// Has a delivery ever reached this watch?
      Pushed : bool
      /// `None` while the last look worked; the reason otherwise, so a query reader
      /// learns what is wrong rather than seeing a row that silently stopped moving.
      Health : string option }

/// How often a session re-asks the provider about a pull request it watches — and it
/// depends on what the last look found, because the two waits are not the same wait.
///
/// A watch whose checks are PENDING is the one somebody is sitting in front of: a suite is
/// in flight and about to say something, and fifteen seconds is the difference between
/// noticing and having moved on. Everything else — settled green, settled red, merged,
/// closed, or a look that failed — waits the full minute, which is what the original sixty
/// was chosen against: CI finishing, or a merge landing, and nobody acts on either sooner.
///
/// The ledger, because only the fast cadence costs anything. A settled watch is two
/// conditional requests that both answer 304, and GitHub does not count a 304 against the
/// primary rate limit — so it is free at any interval. A pending watch is not: its checks
/// endpoint really is moving, so it spends four polls a minute out of five thousand an
/// hour. That puts the practical ceiling around ten pull requests with live suites at once
/// per credential, and it is the reason a pushed transport is worth having rather than
/// simply lowering this number again.
let PendingIntervalMs = 15000
let SettledIntervalMs = 60000

/// The driver's tick: the shorter of the two, so a watch is polled within one tick of
/// falling due at either cadence. WHICH watches are due is decided per entry — a tick is
/// an opportunity to poll, not a poll.
///
/// No jitter, and one tick's watches are polled in sequence rather than at once. A single
/// session watching a handful of pull requests is not a thundering herd, and a slow
/// request delaying the next watch is the backpressure worth having — the same argument
/// `McpClient.PollIntervalMs` makes.
let TickIntervalMs = PendingIntervalMs

type private WatchEntry =
    { Pr : PrRef
      Watcher : ActorRef
      mutable Known : PrKnown
      mutable Snapshot : PrSnapshot option
      mutable Etags : PrEtags
      mutable Health : string option
      /// Set when the provider said to come back later; the epoch second it named.
      mutable SkipUntilEpoch : int option
      /// The epoch second this watch is next due, from what its last look found. Zero
      /// until it has had one, which is what makes a fresh watch due immediately.
      ///
      /// Distinct from `SkipUntilEpoch` because they are different facts: that one is the
      /// provider telling us to come back later, this one is our own cadence. Either can
      /// hold a watch, and the later of the two wins by simply both being checked.
      mutable DueAtEpoch : int64
      /// Is a look at this watch in flight? One push delivers several events within a
      /// second, and two overlapping looks could each `detect` the same transition and
      /// record it twice — so a poke arriving mid-look is remembered rather than raced.
      mutable InFlight : bool
      /// A poke that arrived while a look was in flight. The completing look runs once
      /// more for it, which collapses a burst into at most one extra look.
      mutable PokeAgain : bool
      /// Has a delivery ever reached this watch? Reported in the query, because "is my hook
      /// wired up?" is otherwise unanswerable from anywhere: a working hook and a missing
      /// one look identical apart from latency, and latency is what nobody measures.
      mutable Pushed : bool }

/// Every pull request this session watches, and what it last learned about them.
type PrWatchers =
    { /// Reconcile against the projection — at boot, and after every watch or unwatch.
      /// An unchanged entry keeps its ETags and its last snapshot (the `McpConnections`
      /// rule), so reconciling costs nothing and re-watching does not re-fetch.
      Apply : PrWatch list -> unit
      /// One tick over every watch. `true` when anything the query shows moved, so a
      /// caller knows to invalidate and nothing redraws on a quiet tick.
      ///
      /// Transitions are appended HERE rather than handed back, because a driver that
      /// could forget to append them is a driver that eventually does — and the baseline
      /// this compares against is only durable if what advanced it was recorded.
      Poll : unit -> Async<bool>
      /// Look at every watch on this repo NOW, whatever its cadence said — what a pushed
      /// delivery does. It never overrides the PROVIDER's hold: a provider naming the moment
      /// it will answer again is not something a push knows better than.
      ///
      /// A delivery is a poke rather than a payload: it says look, not what to think. So
      /// the ETags, the baseline, the transition detection and the wake stay the one path
      /// they were, and a delivery that never arrives costs an interval rather than a fact.
      Poke : RepoRef -> Async<bool>
      Rows : unit -> PrWatchRow list }

module PrWatchers =

    /// A session watching nothing, and the composition default. Not an error state: a
    /// session with no watches is the ordinary session.
    let none : PrWatchers =
        { Apply = fun _ -> ()
          Poll = fun () -> async { return false }
          Poke = fun _ -> async { return false }
          Rows = fun () -> [] }

/// Build the poller.
///
/// `record` is how a transition becomes durable; `resolveToken` answers with the
/// credential of whoever's watch this is (the per-operation rule every other GitHub verb
/// follows); `onUnauthorized` is the broker's rejection path, so a dead credential is
/// reported by whoever spent it.
let create
    (provider: string)
    (now: unit -> DateTimeOffset)
    (fetch: FetchPr)
    (resolveToken: ActorRef -> Async<string option>)
    (onUnauthorized: ActorRef -> Async<unit>)
    (record: ActorRef -> PrRef -> PrSnapshot -> PrTransition list -> Async<unit>)
    : PrWatchers =

    let mutable entries : WatchEntry list = []

    let apply (watches: PrWatch list) : unit =
        entries <-
            watches
            |> List.map (fun watch ->
                match entries |> List.tryFind (fun e -> e.Pr = watch.Pr && e.Watcher = watch.Watcher) with
                // Kept, ETags and all — the projection's baseline still wins, because a
                // recorded transition advanced both and they cannot disagree.
                | Some existing ->
                    existing.Known <- watch.Known
                    existing
                | None ->
                    { Pr = watch.Pr
                      Watcher = watch.Watcher
                      Known = watch.Known
                      Snapshot = None
                      Etags = PrEtags.none
                      Health = None
                      SkipUntilEpoch = None
                      DueAtEpoch = 0L
                      InFlight = false
                      PokeAgain = false
                      Pushed = false })

    /// How long until this watch is next due, given what a look just found. `None` is a
    /// look that produced no rollup — a failure — and waits the slow interval like a
    /// settled one, so a watch that cannot be read does not hammer at the fast cadence.
    let dueIn (checks: ChecksRollup option) : int64 =
        match checks with
        | Some ChecksPending -> int64 PendingIntervalMs / 1000L
        | _ -> int64 SettledIntervalMs / 1000L

    let pollEntry (force: bool) (entry: WatchEntry) : Async<bool> =
        async {
            let nowEpoch = (now ()).ToUnixTimeSeconds ()
            let heldByProvider = entry.SkipUntilEpoch |> Option.exists (fun until -> int64 until > nowEpoch)
            // A poke overrides OUR cadence and never the provider's hold — asking inside a
            // window the provider already named would spend a request to be refused.
            if heldByProvider || (not force && entry.DueAtEpoch > nowEpoch) then return false
            else
                entry.SkipUntilEpoch <- None
                let! token = resolveToken entry.Watcher
                let! outcome = fetch token entry.Pr entry.Etags entry.Snapshot
                // Whatever the look found, this watch has had its turn: the next one is
                // scheduled from what it now knows, so a suite finishing drops the watch
                // back to the slow cadence on the very poll that noticed.
                let schedule (checks: ChecksRollup option) =
                    entry.DueAtEpoch <- nowEpoch + dueIn checks
                match outcome with
                | PrUnchanged ->
                    schedule (entry.Snapshot |> Option.map (fun s -> s.Checks))
                    return false
                | PrChanged (snapshot, etags) ->
                    let transitions = PrTransitions.detect entry.Known snapshot
                    if not (List.isEmpty transitions) then
                        do! record entry.Watcher entry.Pr snapshot transitions
                        entry.Known <- transitions |> List.fold PrTransitions.advance entry.Known
                    let moved = entry.Snapshot <> Some snapshot || entry.Health <> None
                    entry.Snapshot <- Some snapshot
                    entry.Etags <- etags
                    entry.Health <- None
                    schedule (Some snapshot.Checks)
                    return moved
                | PrFetchFailed failure ->
                    let health =
                        match failure with
                        | PrUnauthorized -> sprintf "%s rejected this credential" provider
                        | PrNotFound ->
                            sprintf
                                "%s cannot see this pull request — it may be gone, or the credential cannot reach it"
                                provider
                        | PrRateLimited _ -> sprintf "rate limited by %s — waiting for the window to reset" provider
                        | PrUnreachable reason -> reason
                    match failure with
                    | PrUnauthorized -> do! onUnauthorized entry.Watcher
                    | PrRateLimited reset ->
                        // The provider names the moment it will answer again, which beats any
                        // backoff invented here. Absent, wait a window's worth.
                        entry.SkipUntilEpoch <-
                            Some (defaultArg reset (int ((now ()).ToUnixTimeSeconds () + 900L)))
                    | PrNotFound | PrUnreachable _ -> ()
                    let moved = entry.Health <> Some health
                    entry.Health <- Some health
                    schedule None
                    return moved
        }

    /// One look at one watch, with the in-flight bookkeeping around it. A poke that lands
    /// while a look is running is remembered and served by that look when it finishes, so a
    /// push delivering five events in a second costs one extra look rather than five — and,
    /// more importantly, never two overlapping ones recording the same transition twice.
    let rec look (force: bool) (entry: WatchEntry) : Async<bool> =
        async {
            if force then entry.Pushed <- true
            if entry.InFlight then
                entry.PokeAgain <- entry.PokeAgain || force
                return false
            else
                entry.InFlight <- true
                let! moved = pollEntry force entry
                entry.InFlight <- false
                if entry.PokeAgain then
                    entry.PokeAgain <- false
                    let! again = look true entry
                    return moved || again
                else
                    return moved
        }

    { Apply = apply
      Poll =
        fun () ->
            async {
                let mutable moved = false
                // A snapshot of the list, so a watch added mid-tick is picked up by the
                // next one rather than mutating what this one is walking.
                for entry in List.ofSeq entries do
                    let! entryMoved = look false entry
                    moved <- moved || entryMoved
                return moved
            }
      Poke =
        fun repo ->
            async {
                let mutable moved = false
                for entry in entries |> List.filter (fun e -> e.Pr.Repo = repo) do
                    let! entryMoved = look true entry
                    moved <- moved || entryMoved
                return moved
            }
      Rows =
        fun () ->
            entries
            |> List.map (fun e ->
                { Pr = e.Pr
                  Watcher = e.Watcher
                  Snapshot = e.Snapshot
                  Pushed = e.Pushed
                  Health = e.Health }) }

// --- the watch verbs ----------------------------------------------------------------------

/// Starting and stopping a watch. Both are ACTS: they change what the session does from
/// now on, they are attributed, and they read back in the timeline — so they go through
/// the same gate every other repo verb does, and the session's own log is where a watch
/// lives rather than any config file.
type PrWatchService =
    { /// Begin watching. Validates by LOOKING once with the caller's credential, which is
      /// also where the baseline comes from: a watch whose provider cannot be read is a
      /// watch that would never say anything, and refusing now beats a silent row.
      Watch : ActorRef -> ActorRef -> PrRef -> Async<Result<string, string>>
      Unwatch : ActorRef -> PrRef -> Async<Result<string, string>> }

/// Build the watch verbs over the session's log and the poller they reconcile into.
///
/// `refold` re-reads the log and hands the watches over, so the projection is the single
/// source of what is watched — the verbs never mutate the poller's list directly, and a
/// restart rebuilding from the same log lands in the same place.
let watchService
    (provider: string)
    (append: ActorRef -> SessionEvent -> Async<unit>)
    (watchesNow: unit -> Async<PrWatch list>)
    (fetch: FetchPr)
    (resolveToken: ActorRef -> Async<string option>)
    (refold: PrWatch list -> unit)
    : PrWatchService =

    let mintId () =
        MessageId.create (string (System.Guid.NewGuid ()))

    let describe (pr: PrRef) (snapshot: PrSnapshot) =
        sprintf
            "%s watched (%s, %s)"
            (PrRef.render pr)
            (PrState.describe snapshot.State)
            (ChecksRollup.describe snapshot.Checks)

    { Watch =
        fun actor credential pr ->
            async {
                let! watches = watchesNow ()
                match watches |> List.tryFind (fun w -> w.Pr = pr) with
                // Already watched: a repeated ask is a question, not an act (the
                // `add_repo` rule). Answer what is known and record nothing.
                | Some existing ->
                    return
                        Ok (
                            sprintf
                                "%s already watched (%s, %s)"
                                (PrRef.render pr)
                                (PrState.describe existing.Known.State)
                                (ChecksRollup.describe existing.Known.Checks))
                | None ->
                    let! token = resolveToken credential
                    let! outcome = fetch token pr PrEtags.none None
                    match outcome with
                    | PrFetchFailed PrNotFound ->
                        return
                            Error (
                                sprintf
                                    "%s cannot see %s — check the number, or whether the connected %s credential can reach that repo"
                                    provider
                                    (PrRef.render pr)
                                    provider)
                    | PrFetchFailed PrUnauthorized ->
                        return Error (sprintf "%s rejected the credential — sign in again from the Connections panel" provider)
                    | PrFetchFailed (PrRateLimited _) ->
                        return Error (sprintf "rate limited by %s — try again shortly" provider)
                    | PrFetchFailed (PrUnreachable reason) -> return Error reason
                    // Unreachable in practice (nothing has an ETag yet), but total: a
                    // provider that answers 304 to a first look has told us nothing to
                    // start a baseline from.
                    | PrUnchanged -> return Error (sprintf "%s answered nothing about that pull request" provider)
                    | PrChanged (snapshot, _) ->
                        match mintId () with
                        | Error e -> return Error e
                        | Ok messageId ->
                            do!
                                append
                                    actor
                                    (SessionEvent.PrWatched
                                        { MessageId = messageId; Pr = pr; Initial = snapshot; Actor = actor })
                            let! watches = watchesNow ()
                            refold watches
                            return Ok (describe pr snapshot)
            }
      Unwatch =
        fun actor pr ->
            async {
                let! watches = watchesNow ()
                if watches |> List.exists (fun w -> w.Pr = pr) |> not then
                    return Error (sprintf "%s not watched" (PrRef.render pr))
                else
                    match mintId () with
                    | Error e -> return Error e
                    | Ok messageId ->
                        do! append actor (SessionEvent.PrUnwatched { MessageId = messageId; Pr = pr; Actor = actor })
                        let! watches = watchesNow ()
                        refold watches
                        return Ok (sprintf "%s unwatched" (PrRef.render pr))
            } }

// --- the query -----------------------------------------------------------------------------
// A QUERY, so registering it IS the UI change (the `mcp_servers` argument): the settings
// surface maps over whatever the session declared, and the registry generates the agent's
// read-only tool from the same declaration. No panel, no route.

let queryName : QueryName =
    match QueryName.create "pull_requests" with
    | Ok name -> name
    | Error e -> failwithf "pull requests query name: %s" e

let private queryDef : QueryDef =
    { Name = queryName
      Title = "Pull requests"
      Description =
        "The pull requests this session is watching, each with its state, the rollup of \
         its checks, and whose credential the session reads it with. Transitions — a \
         merge, a close, checks turning green or red — are announced on the timeline as \
         they happen; this is the current state."
      Shape =
        Rows
            [ QueryColumn.create "pr" "pull request"
              QueryColumn.create "title" "title"
              QueryColumn.create "state" "state"
              QueryColumn.create "checks" "checks"
              QueryColumn.create "watcher" "watched by"
              QueryColumn.create "status" "status" ] }

/// Register the watched pull requests as a query. A GETTER for the `mcp_servers` reason:
/// the query surface is composed before the Host is started, and the Host is what builds
/// the poller.
let query (current: unit -> PrWatchers) : Queries.QueryRegistration =
    { Def = queryDef
      Read =
        fun () ->
            async {
                return
                    Ok (RowsOf (
                        (current ()).Rows ()
                        |> List.map (fun row ->
                            [ "pr", CellText (PrRef.render row.Pr)
                              "title",
                              (match row.Snapshot with
                               | Some s when s.Title <> "" -> CellText s.Title
                               | _ -> CellAbsent)
                              // Toned, because a table of watches is SCANNED rather than
                              // read: the one a person is looking for is the red suite, and
                              // it should not take reading six rows to find it. The word is
                              // still the word — the tone only says how loudly.
                              "state",
                              (match row.Snapshot with
                               | Some s ->
                                   CellStatus (
                                       PrState.describe s.State,
                                       match s.State with
                                       // Merged is the outcome somebody was waiting for.
                                       // Open is the ordinary state and earns no colour —
                                       // colouring every row would be colouring none.
                                       | PrMerged -> ToneOk
                                       | PrClosed -> ToneMuted
                                       | PrOpen -> ToneMuted)
                               // Watched, but not yet looked at — which is a different
                               // thing from a state, and says so rather than guessing one.
                               | None -> CellAbsent)
                              "checks",
                              (match row.Snapshot with
                               | Some s ->
                                   CellStatus (
                                       ChecksRollup.describe s.Checks,
                                       match s.Checks with
                                       | ChecksGreen -> ToneOk
                                       | ChecksRed -> ToneBad
                                       | ChecksPending -> ToneBusy
                                       // No checks is not a verdict about anything.
                                       | ChecksNone -> ToneMuted)
                               | None -> CellAbsent)
                              "watcher", CellText (ActorRef.token row.Watcher)
                              "status",
                              (match row.Health, row.Pushed with
                               // A watch that has stopped moving, and why. The one cell in
                               // the row that is a problem rather than a state.
                               | Some health, _ -> CellStatus (health, ToneBad)
                               // The difference between a hook that is wired up and one
                               // that is not, which is otherwise only visible as latency.
                               | None, true -> CellStatus ("ok (push)", ToneOk)
                               | None, false -> CellStatus ("ok", ToneMuted)) ])))
            } }
