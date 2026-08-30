module Yession.Host.GitHubPrs

// Everything GitHub-specific about watching a pull request, and nothing else — the
// `GitHubConnection.fs` precedent, for the same reason: the Manager brokers the credential
// and never learns which service it brokered, so a REST endpoint has no business above
// this file. What is left here is the two endpoints and their JSON (`fetchOver`, the whole
// of the `FetchPr` seam this side owns) and the one field path a delivery names its repo
// at. The cadence, the ETag bookkeeping, the verbs and the query are provider-neutral and
// live in `PrWatches.fs`; a second forge is a second copy of this file, not a second poller.

open System
open Fable.Core
open Yession.Domain
open Yession.Domain.Hooks
open Yession.Domain.Prs
open Yession.Host.PrWatches

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// What the neutral watcher calls this provider when it has to say so in a sentence a
/// person reads — "github rejected this credential". Lower case, because it appears
/// mid-sentence far more often than it starts one.
let provider = "github"

// --- the provider's JSON, decoded ------------------------------------------------------

/// Everything the pull request resource itself contributes to a snapshot — which is all
/// of it but the checks rollup, whose endpoint is the other half of a look.
type PrFields =
    { State : PrState
      Title : string
      HeadSha : string
      Queued : bool
      Mergeable : bool option }

/// What `GET /repos/{o}/{r}/pulls/{n}` says, reduced to what a snapshot carries.
///
/// `merged` rather than `state` decides a merge: GitHub reports a merged pull request as
/// `state: "closed"` with `merged: true`, so reading state alone would file every merge
/// as a close — which is the one distinction the whole feature exists to draw.
let prDecoder : Decoder<PrFields> =
    Decode.object (fun get ->
        let merged = get.Optional.Field "merged" Decode.bool |> Option.defaultValue false
        let state = get.Required.Field "state" Decode.string
        { State =
            if merged then PrMerged
            elif state = "closed" then PrClosed
            else PrOpen
          Title = get.Optional.Field "title" Decode.string |> Option.defaultValue ""
          HeadSha = get.Required.At [ "head"; "sha" ] Decode.string
          // `auto_merge` is an OBJECT when auto merge is armed and null when it is not, so
          // its presence is the whole fact and none of its contents are read. Decoded as
          // a raw value for exactly that reason: what is inside it (who armed it, which
          // method, what commit message) would date this decoder against a shape nobody
          // here depends on.
          Queued = get.Optional.Field "auto_merge" Decode.value |> Option.exists (fun v -> not (Decode.Helpers.isNullValue v))
          // Null until GitHub has computed it, which it does lazily. Carried for display
          // and never for a transition — see `PrSnapshot.Mergeable`.
          Mergeable = get.Optional.Field "mergeable" (Decode.option Decode.bool) |> Option.flatten })

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
                    Ok (
                        last
                        |> Option.map (fun s ->
                            { State = s.State
                              Title = s.Title
                              HeadSha = s.HeadSha
                              Queued = s.Queued
                              Mergeable = s.Mergeable }))
                elif succeeded prReply then
                    Decode.fromString prDecoder prReply.body
                    |> Result.map Some
                    |> Result.mapError (sprintf "unrecognised pull request reply: %s")
                else Ok None
            match fields with
            | Error e -> return PrFetchFailed (PrUnreachable e)
            | Ok None when not (notModified prReply) -> return PrFetchFailed (failureOf prReply)
            | Ok None -> return PrUnchanged
            | Ok (Some fields) ->
                let checksUrl =
                    sprintf
                        "%s/repos/%s/commits/%s/check-runs?per_page=100"
                        (apiBase.TrimEnd '/')
                        repo
                        fields.HeadSha
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
                        | Some s when s.HeadSha = fields.HeadSha -> s.Checks
                        | _ -> ChecksPending
                    let checks =
                        if succeeded checksReply then
                            match Decode.fromString checkRunsDecoder checksReply.body with
                            | Ok runs -> rollupOf runs
                            | Error _ -> unread
                        else unread
                    let snapshot =
                        { State = fields.State
                          Title = fields.Title
                          HeadSha = fields.HeadSha
                          Checks = checks
                          Queued = fields.Queued
                          Mergeable = fields.Mergeable }
                    // An ETag is replaced only by a half that actually answered with one.
                    // A 304 carries back the ETag we sent, so keeping the old one says the
                    // same thing without depending on the provider echoing it.
                    let nextEtags =
                        { Pr = (if succeeded prReply then prReply.etag else etags.Pr)
                          Checks = (if succeeded checksReply then checksReply.etag else etags.Checks) }
                    return PrChanged (snapshot, nextEtags)
        }

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
