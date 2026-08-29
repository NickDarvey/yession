module Yession.Host.GitHubPrs

// Everything GitHub-specific about WATCHING a pull request lives here, in the session —
// the `GitHubConnection.fs` precedent, for the same reason: the Manager brokers the
// credential and never learns which service it brokered, so a REST endpoint has no
// business above this file. The Domain's vocabulary (`PrFacts.fs`, `PrWatches.fs`) is
// provider-lean; what this module adds is the two GitHub endpoints, their JSON, and the
// poll that folds one into the other.
//
// Polling, not webhooks, and that is a decision rather than a stopgap: a repo webhook
// needs admin on every repo somebody wants watched, and inbound delivery needs a
// deployment GitHub can reach — which the loopback default is not. A settled watch costs
// two conditional GETs that both answer 304, which is free (GitHub does not count one
// against the rate limit), and it works in every deployment shape there is. `FetchPr` is
// where a future push transport plugs in without anything downstream noticing.

open System
open Fable.Core
open Yession.Domain
open Yession.Domain.Hooks
open Yession.Domain.Prs
open Yession.Domain.Tools

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

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
    /// Gone, or a credential that cannot see it — GitHub answers 404 for both, which is
    /// why this is one case and the message says so.
    | PrNotFound
    /// Rate limited, with the epoch second GitHub says the window resets at when it said.
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

// --- the provider's JSON, decoded ------------------------------------------------------

/// What `GET /repos/{o}/{r}/pulls/{n}` says, reduced to what a snapshot carries.
///
/// `merged` rather than `state` decides a merge: GitHub reports a merged pull request as
/// `state: "closed"` with `merged: true`, so reading state alone would file every merge
/// as a close — which is the one distinction the whole feature exists to draw.
let prDecoder : Decoder<PrState * string * string * bool option> =
    Decode.object (fun get ->
        let merged = get.Optional.Field "merged" Decode.bool |> Option.defaultValue false
        let state = get.Required.Field "state" Decode.string
        let resolved =
            if merged then PrMerged
            elif state = "closed" then PrClosed
            else PrOpen
        resolved,
        get.Optional.Field "title" Decode.string |> Option.defaultValue "",
        get.Required.At [ "head"; "sha" ] Decode.string,
        // Null until GitHub has computed it, which it does lazily. Carried for display
        // and never for a transition — see `PrSnapshot.Mergeable`.
        get.Optional.Field "mergeable" (Decode.option Decode.bool) |> Option.flatten)

/// `GET /repos/{o}/{r}/commits/{sha}/check-runs` — each run's status and conclusion.
let checkRunsDecoder : Decoder<(string * string option) list> =
    Decode.field
        "check_runs"
        (Decode.list (
            Decode.object (fun get ->
                get.Required.Field "status" Decode.string,
                get.Optional.Field "conclusion" (Decode.option Decode.string) |> Option.flatten)))

/// Fold every check run on a commit into the one word a watcher acts on.
///
/// Pending wins over red, deliberately: a suite still running may yet turn the answer
/// around, and announcing red while jobs are in flight is how a watcher learns to
/// distrust the announcement. `skipped` and `neutral` count as green — they are how a
/// conditional job reports "not my turn", and a PR whose docs job skipped is not a PR
/// with a problem.
let rollupOf (runs: (string * string option) list) : ChecksRollup =
    let failed =
        [ "failure"; "timed_out"; "cancelled"; "action_required"; "startup_failure"; "stale" ]
    if List.isEmpty runs then ChecksNone
    elif runs |> List.exists (fun (status, _) -> status <> "completed") then ChecksPending
    elif runs |> List.exists (fun (_, conclusion) -> conclusion |> Option.exists (fun c -> List.contains c failed)) then
        ChecksRed
    else ChecksGreen

// --- the two conditional GETs -----------------------------------------------------------

type private FetchReply =
    abstract reachable : bool
    abstract status : int
    abstract etag : string
    abstract reset : string
    abstract body : string

/// One conditional GET, as GitHub wants it asked: a bearer token when there is one, the
/// versioned accept header, a user agent (GitHub refuses requests without one), and the
/// caller's ETag so an unchanged resource costs a 304 rather than a body.
[<Emit("""(function (url, token, etag) {
  const headers = { 'accept': 'application/vnd.github+json', 'user-agent': 'yession',
                    'x-github-api-version': '2022-11-28' }
  if (token) headers['authorization'] = 'Bearer ' + token
  if (etag) headers['if-none-match'] = etag
  return fetch(url, { headers })
    .then(async r => ({ reachable: true, status: r.status, etag: r.headers.get('etag') || '',
                        reset: r.headers.get('x-ratelimit-reset') || '', body: await r.text() }))
    .catch(e => ({ reachable: false, status: 0, etag: '', reset: '',
                   body: String((e && e.message) || e) }))
})($0, $1, $2)""")>]
let private getConditional (url: string) (token: string) (etag: string) : JS.Promise<FetchReply> = jsNative

let private failureOf (reply: FetchReply) : PrFetchFailure =
    if not reply.reachable then PrUnreachable reply.body
    elif reply.status = 401 then PrUnauthorized
    elif reply.status = 404 then PrNotFound
    // 403 and 429 are both how GitHub says "too many"; a 403 for any other reason
    // (scopes, a blocked App) is also not something a retry sooner would fix, so the
    // wait it implies is the safe reading either way.
    elif reply.status = 403 || reply.status = 429 then
        PrRateLimited (match Int32.TryParse reply.reset with | true, epoch -> Some epoch | _ -> None)
    else PrUnreachable (sprintf "github answered %d" reply.status)

/// The fetch as it is composed against a real API base. The base is a PARAMETER for the
/// reason `GitHubConnection.refusedAt` takes one: a suite needs somewhere to point it
/// that is not the live provider.
let fetchOver (apiBase: string) : FetchPr =
    fun token pr etags last ->
        async {
            let bearer = defaultArg token ""
            let repo = RepoRef.value pr.Repo
            let succeeded (reply: FetchReply) = reply.reachable && reply.status >= 200 && reply.status < 300
            let notModified (reply: FetchReply) = reply.reachable && reply.status = 304
            let prUrl = sprintf "%s/repos/%s/pulls/%d" (apiBase.TrimEnd '/') repo pr.Number
            let! prReply = getConditional prUrl bearer etags.Pr |> Interop.awaitPromise
            // The pull request's own fields, decoded when it answered with a body and
            // carried over from the last look when it answered 304.
            //
            // BOTH halves are always asked, and that is the point. The check runs on a
            // commit go queued -> in_progress -> completed without the pull request
            // resource moving at all, so returning early on its 304 is how a watch sits
            // on `pending` for the whole life of a build that has already gone green.
            // Asking twice is free in the only currency that binds: GitHub does not count
            // a 304 against the primary rate limit.
            let fields =
                if notModified prReply then
                    // Nothing to carry means nothing to say. Unreachable in practice — an
                    // ETag only exists because a body came back once — but total here
                    // rather than a guess.
                    Ok (last |> Option.map (fun s -> s.State, s.Title, s.HeadSha, s.Mergeable))
                elif succeeded prReply then
                    Decode.fromString prDecoder prReply.body
                    |> Result.map Some
                    |> Result.mapError (sprintf "unrecognised pull request reply: %s")
                else Ok None
            match fields with
            | Error e -> return PrFetchFailed (PrUnreachable e)
            | Ok None when not (notModified prReply) -> return PrFetchFailed (failureOf prReply)
            | Ok None -> return PrUnchanged
            | Ok (Some (state, title, headSha, mergeable)) ->
                let checksUrl =
                    sprintf "%s/repos/%s/commits/%s/check-runs?per_page=100" (apiBase.TrimEnd '/') repo headSha
                let! checksReply = getConditional checksUrl bearer etags.Checks |> Interop.awaitPromise
                if notModified prReply && notModified checksReply then
                    // Both halves unchanged: there is nothing to fold and nothing to say.
                    return PrUnchanged
                else
                    // A checks endpoint that says nothing readable does not fail the whole
                    // look — the pull request's own state is the more important half and
                    // is already in hand. On the SAME head sha the last rollup still
                    // stands (a 304 can only mean that, because the checks URL is keyed by
                    // the sha); on a new one it does not, and pending is the honest answer
                    // for a commit whose runs have not been read. Never `ChecksNone`,
                    // which would claim there are none.
                    let unread =
                        match last with
                        | Some s when s.HeadSha = headSha -> s.Checks
                        | _ -> ChecksPending
                    let checks =
                        if succeeded checksReply then
                            match Decode.fromString checkRunsDecoder checksReply.body with
                            | Ok runs -> rollupOf runs
                            | Error _ -> unread
                        else unread
                    let snapshot =
                        { State = state; Title = title; HeadSha = headSha; Checks = checks; Mergeable = mergeable }
                    // An ETag is replaced only by a half that actually answered with one.
                    // A 304 carries back the ETag we sent, so keeping the old one says the
                    // same thing without depending on the provider echoing it.
                    let nextEtags =
                        { Pr = (if succeeded prReply then prReply.etag else etags.Pr)
                          Checks = (if succeeded checksReply then checksReply.etag else etags.Checks) }
                    return PrChanged (snapshot, nextEtags)
        }

// --- the poller --------------------------------------------------------------------------

/// One watched pull request as the `github_prs` query reports it.
type PrWatchRow =
    { Pr : PrRef
      Watcher : ActorRef
      Snapshot : PrSnapshot option
      /// Has a delivery ever reached this watch?
      Pushed : bool
      /// `None` while the last look worked; the reason otherwise, so a query reader
      /// learns what is wrong rather than seeing a row that silently stopped moving.
      Health : string option }

/// How often a session re-asks GitHub about a pull request it watches — and it depends on
/// what the last look found, because the two waits are not the same wait.
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
      /// Set when GitHub said to come back later; the epoch second it named.
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
      /// delivery does. It never overrides the PROVIDER's hold: GitHub naming the moment it
      /// will answer again is not something a push knows better than.
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
            // window GitHub already named would spend a request to be refused.
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
                        | PrUnauthorized -> "github rejected this credential"
                        | PrNotFound -> "github cannot see this pull request — it may be gone, or the credential cannot reach it"
                        | PrRateLimited _ -> "rate limited by github — waiting for the window to reset"
                        | PrUnreachable reason -> reason
                    match failure with
                    | PrUnauthorized -> do! onUnauthorized entry.Watcher
                    | PrRateLimited reset ->
                        // GitHub names the moment it will answer again, which beats any
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
            "watching %s (%s, %s)"
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
                                "already watching %s (%s, %s)"
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
                                    "github cannot see %s — check the number, or whether the connected GitHub credential can reach that repo"
                                    (PrRef.render pr))
                    | PrFetchFailed PrUnauthorized ->
                        return Error "github rejected the credential — sign in again from the Connections panel"
                    | PrFetchFailed (PrRateLimited _) ->
                        return Error "rate limited by github — try again shortly"
                    | PrFetchFailed (PrUnreachable reason) -> return Error reason
                    // Unreachable in practice (nothing has an ETag yet), but total: a
                    // provider that answers 304 to a first look has told us nothing to
                    // start a baseline from.
                    | PrUnchanged -> return Error "github answered nothing about that pull request"
                    | PrChanged (snapshot, _) ->
                        match mintId () with
                        | Error e -> return Error e
                        | Ok messageId ->
                            do!
                                append
                                    actor
                                    (SessionEvent.PrWatchStarted
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
                    return Error (sprintf "not watching %s" (PrRef.render pr))
                else
                    match mintId () with
                    | Error e -> return Error e
                    | Ok messageId ->
                        do! append actor (SessionEvent.PrWatchStopped { MessageId = messageId; Pr = pr; Actor = actor })
                        let! watches = watchesNow ()
                        refold watches
                        return Ok (sprintf "stopped watching %s" (PrRef.render pr))
            } }

// --- the hook subscription -------------------------------------------------------------------
// Push, where the deployment can take it. A delivery does not tell this session anything —
// it tells it to LOOK, and the poll above is still what produces every fact. So this is an
// accelerator with no second code path behind it: where hooks are configured a transition
// lands in seconds, and where they are not the interval above is unchanged.

/// Where a delivery carries the repository it concerns.
///
/// THE one provider-shaped string this feature puts in front of the Manager — and it goes
/// there as DATA, inside a filter the Manager stores and compares without ever knowing what
/// it means. That is the whole trade: the Manager relays, this file knows.
let repoPath : FieldPath =
    match FieldPath.create "body.repository.full_name" with
    | Ok path -> path
    | Error e -> failwithf "github repo path: %s" e

/// What this session asks to be forwarded: deliveries naming this repo. Per REPO and not
/// per pull request, because that is what a delivery names — the poke is repo-wide and the
/// poller decides which of its watches moved.
let filterFor (repo: RepoRef) : DeliveryFilter = { Where = [ repoPath, RepoRef.value repo ] }

/// The hook subscriptions this session holds, one per watched repo.
type PrHooks =
    { /// Reconcile against the repos currently watched — at boot, and after every watch or
      /// unwatch. The same shape as `PrWatchers.Apply`, for the same reason: the log's
      /// watches are the one source of what is subscribed, so the two cannot drift.
      Apply : RepoRef list -> unit
      /// Which repo a delivery concerns, from the subscription it names.
      ///
      /// Read from THIS session's own records, never from the delivery. That is what makes
      /// "a delivery is a poke" true rather than aspirational: the body is never parsed, so
      /// there is nothing in it to be wrong about or to lie with.
      RepoOf : string -> RepoRef option }

module PrHooks =

    /// A session that subscribes to nothing — the composition default, and what a session
    /// with no control channel gets. Not an error state: polling is the mechanism.
    let none : PrHooks =
        { Apply = fun _ -> ()
          RepoOf = fun _ -> None }

/// Build the reconciler over the Manager's hook control leg.
///
/// Every failure here is logged and dropped, deliberately: a subscription that could not be
/// made costs latency and nothing else, because the poll still runs. Failing the watch over
/// it would make an optional accelerator a required dependency.
let hooks
    (subscribe: DeliveryFilter -> Async<Result<string, string>>)
    (unsubscribe: string -> Async<Result<bool, string>>)
    : PrHooks =

    // `None` is claimed-but-not-yet-acknowledged: the slot is taken synchronously so a
    // second reconcile arriving before the Manager answers cannot subscribe twice.
    let mutable held : (RepoRef * string option) list = []

    { Apply =
        fun repos ->
            let wanted = List.distinct repos
            for repo in wanted do
                if held |> List.exists (fun (r, _) -> r = repo) |> not then
                    held <- held @ [ repo, None ]
                    Async.StartImmediate (
                        async {
                            match! subscribe (filterFor repo) with
                            | Ok id ->
                                held <- held |> List.map (fun (r, current) -> if r = repo then r, Some id else r, current)
                            | Error e ->
                                eprintfn "hook subscription for %s failed, falling back to polling: %s" (RepoRef.value repo) e
                                held <- held |> List.filter (fun (r, _) -> r <> repo)
                        })
            for repo, id in held |> List.filter (fun (r, _) -> not (List.contains r wanted)) do
                held <- held |> List.filter (fun (r, _) -> r <> repo)
                match id with
                | Some subscriptionId ->
                    Async.StartImmediate (
                        async {
                            match! unsubscribe subscriptionId with
                            | Ok _ -> ()
                            | Error e -> eprintfn "dropping hook subscription for %s failed: %s" (RepoRef.value repo) e
                        })
                // Unwatched before the Manager answered: the id to drop does not exist yet,
                // so the subscription outlives the watch until the launch ends and the
                // Manager drops everything under its secret. Rare, and bounded by that.
                | None -> ()
      RepoOf =
        fun id ->
            held
            |> List.tryPick (fun (repo, current) -> if current = Some id then Some repo else None) }

// --- the query -----------------------------------------------------------------------------
// A QUERY, so registering it IS the UI change (the `mcp_servers` argument): the settings
// surface maps over whatever the session declared, and the registry generates the agent's
// read-only tool from the same declaration. No panel, no route.

let queryName : QueryName =
    match QueryName.create "github_prs" with
    | Ok name -> name
    | Error e -> failwithf "github prs query name: %s" e

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
