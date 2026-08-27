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
// deployment GitHub can reach — which the loopback default is not. Two conditional GETs
// per watch per minute cost nothing (a 304 does not count against the rate limit) and
// work in every deployment shape there is. `FetchPr` is where a future push transport
// plugs in without anything downstream noticing.

open System
open Fable.Core
open Yession.Domain
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

/// THE SEAM: one look at one pull request, with whatever credential the caller resolved.
/// The poller, the watch verb and every test hold this signature, so replacing polling
/// with a pushed stream later replaces an implementation rather than a design.
type FetchPr = string option -> PrRef -> PrEtags -> Async<PrFetchOutcome>

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
    fun token pr etags ->
        async {
            let bearer = defaultArg token ""
            let repo = RepoRef.value pr.Repo
            let prUrl = sprintf "%s/repos/%s/pulls/%d" (apiBase.TrimEnd '/') repo pr.Number
            let! prReply = getConditional prUrl bearer etags.Pr |> Interop.awaitPromise
            if prReply.reachable && prReply.status = 304 then
                // The pull request itself has not moved, so neither has its head sha —
                // and the checks URL is keyed by that sha. Asking again would spend a
                // request to be told the same thing.
                return PrUnchanged
            elif not (prReply.reachable && prReply.status >= 200 && prReply.status < 300) then
                return PrFetchFailed (failureOf prReply)
            else
                match Decode.fromString prDecoder prReply.body with
                | Error e -> return PrFetchFailed (PrUnreachable (sprintf "unrecognised pull request reply: %s" e))
                | Ok (state, title, headSha, mergeable) ->
                    let checksUrl =
                        sprintf "%s/repos/%s/commits/%s/check-runs?per_page=100" (apiBase.TrimEnd '/') repo headSha
                    let! checksReply = getConditional checksUrl bearer etags.Checks |> Interop.awaitPromise
                    // A checks endpoint that fails does not fail the whole look: the pull
                    // request's own state is the more important half and is already in
                    // hand. `ChecksNone` would be a lie, so an unreadable rollup keeps
                    // whatever the last one said by reporting pending — the next poll
                    // settles it either way.
                    let checks =
                        if checksReply.reachable && checksReply.status >= 200 && checksReply.status < 300 then
                            match Decode.fromString checkRunsDecoder checksReply.body with
                            | Ok runs -> rollupOf runs
                            | Error _ -> ChecksPending
                        elif checksReply.reachable && checksReply.status = 304 then ChecksPending
                        else ChecksPending
                    let snapshot =
                        { State = state; Title = title; HeadSha = headSha; Checks = checks; Mergeable = mergeable }
                    return PrChanged (snapshot, { Pr = prReply.etag; Checks = checksReply.etag })
        }

// --- the poller --------------------------------------------------------------------------

/// One watched pull request as the `github_prs` query reports it.
type PrWatchRow =
    { Pr : PrRef
      Watcher : ActorRef
      Snapshot : PrSnapshot option
      /// `None` while the last look worked; the reason otherwise, so a query reader
      /// learns what is wrong rather than seeing a row that silently stopped moving.
      Health : string option }

/// How often a session re-asks GitHub about the pull requests it watches.
///
/// Sixty seconds, chosen against what a watcher is waiting for: CI finishing, or a merge
/// landing. Nobody acts on either inside a minute, and the steady state is nearly free —
/// GitHub does not count a 304 against the primary rate limit, so an idle watch costs two
/// conditional requests a minute out of five thousand an hour.
///
/// No jitter, and one tick's watches are polled in sequence rather than at once. A single
/// session watching a handful of pull requests is not a thundering herd, and a slow
/// request delaying the next watch is the backpressure worth having — the same argument
/// `McpClient.PollIntervalMs` makes.
let PollIntervalMs = 60000

type private WatchEntry =
    { Pr : PrRef
      Watcher : ActorRef
      mutable Known : PrKnown
      mutable Snapshot : PrSnapshot option
      mutable Etags : PrEtags
      mutable Health : string option
      /// Set when GitHub said to come back later; the epoch second it named.
      mutable SkipUntilEpoch : int option }

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
      Rows : unit -> PrWatchRow list }

module PrWatchers =

    /// A session watching nothing, and the composition default. Not an error state: a
    /// session with no watches is the ordinary session.
    let none : PrWatchers =
        { Apply = fun _ -> ()
          Poll = fun () -> async { return false }
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
                      SkipUntilEpoch = None })

    let pollEntry (entry: WatchEntry) : Async<bool> =
        async {
            let nowEpoch = (now ()).ToUnixTimeSeconds ()
            match entry.SkipUntilEpoch with
            | Some until when int64 until > nowEpoch -> return false
            | _ ->
                entry.SkipUntilEpoch <- None
                let! token = resolveToken entry.Watcher
                let! outcome = fetch token entry.Pr entry.Etags
                match outcome with
                | PrUnchanged -> return false
                | PrChanged (snapshot, etags) ->
                    let transitions = PrTransitions.detect entry.Known snapshot
                    if not (List.isEmpty transitions) then
                        do! record entry.Watcher entry.Pr snapshot transitions
                        entry.Known <- transitions |> List.fold PrTransitions.advance entry.Known
                    let moved = entry.Snapshot <> Some snapshot || entry.Health <> None
                    entry.Snapshot <- Some snapshot
                    entry.Etags <- etags
                    entry.Health <- None
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
                    let! entryMoved = pollEntry entry
                    moved <- moved || entryMoved
                return moved
            }
      Rows =
        fun () ->
            entries
            |> List.map (fun e ->
                { Pr = e.Pr; Watcher = e.Watcher; Snapshot = e.Snapshot; Health = e.Health }) }

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
                    let! outcome = fetch token pr PrEtags.none
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
                              "state",
                              (match row.Snapshot with
                               | Some s -> CellText (PrState.describe s.State)
                               // Watched, but not yet looked at — which is a different
                               // thing from a state, and says so rather than guessing one.
                               | None -> CellAbsent)
                              "checks",
                              (match row.Snapshot with
                               | Some s -> CellText (ChecksRollup.describe s.Checks)
                               | None -> CellAbsent)
                              "watcher", CellText (ActorRef.token row.Watcher)
                              "status", CellText (defaultArg row.Health "ok") ])))
            } }
