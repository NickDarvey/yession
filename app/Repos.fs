module Yession.Host.Repos

// The session's repo manager (Plan 14): ONE function with two interfaces — the agent's
// MCP verbs and the settings panel both land here, every mutation appends the same
// events, and the conversation timeline is their shared record.
//
// Git itself runs through the sandbox seam under the AGENT backend (`host` is the
// explicitly lax choice; `srt` confines each spawn to the repos directory and the
// allowlisted egress). On top of the confinement, repo-controlled EXECUTION is disabled
// per invocation — hooks, fsmonitor, ext transport — because the WorkSandbox can write
// the repos directory by design, so a poisoned `.git/config` is assumed and made inert
// rather than trusted-by-placement.
//
// The credential (the acting human's GitHub token, Plan 08 precedence) reaches exactly
// one place: the env of the single confined git invocation that needs it. It is never
// in the sandbox policy env, so nothing that outlives the invocation can read it.

open System
open Fable.Core
open Yession.Domain
open Yession.SessionProcess

// --- pure pieces (cheap-tier tested) -----------------------------------------------------

/// A branch name the switch verb will accept: a conservative subset of what
/// `git check-ref-format` allows, enough for every real branch and none of the
/// option-injection shapes (`-`-leading) or traversal shapes (`..`).
let validBranchName (raw: string) : Result<string, string> =
    let name = (defaultArg (Option.ofObj raw) "").Trim ()
    let charOk c = Char.IsLetterOrDigit c || c = '-' || c = '_' || c = '.' || c = '/'
    if name = "" then Error "branch name cannot be empty"
    elif name.StartsWith "-" || name.StartsWith "." || name.StartsWith "/" then
        Error (sprintf "'%s' is not a valid branch name" name)
    elif name.EndsWith "/" || name.EndsWith ".lock" then
        Error (sprintf "'%s' is not a valid branch name" name)
    elif name.Contains ".." || name.Contains "//" || name.Contains "@{" then
        Error (sprintf "'%s' is not a valid branch name" name)
    elif name |> Seq.forall charOk then Ok name
    else Error (sprintf "'%s' is not a valid branch name" name)

/// The environment one git invocation runs with. Everything here is the point:
/// no global/system config (a repo's own `.git/config` is already untrusted — the
/// WorkSandbox can write it), no prompts, a pinned protocol allowlist, and the
/// config-driven execution vectors (`hooksPath`, `fsmonitor`, `protocol.ext`) forced
/// off via `GIT_CONFIG_*` — which apply with the highest precedence git knows, so a
/// planted repo config cannot override them.
let hardenedEnv (allowProtocol: string) (token: string option) : (string * string) list =
    let configs =
        [ "core.hooksPath", "/dev/null"
          "core.fsmonitor", "false"
          "protocol.ext.allow", "never" ]
        @ (match token with
           | Some token ->
               let basic =
                   Convert.ToBase64String (Text.Encoding.UTF8.GetBytes ("x-access-token:" + token))
               [ "http.https://github.com/.extraheader", "AUTHORIZATION: basic " + basic ]
           | None -> [])
    [ "GIT_CONFIG_GLOBAL", "/dev/null"
      "GIT_CONFIG_SYSTEM", "/dev/null"
      "GIT_TERMINAL_PROMPT", "0"
      "GIT_ALLOW_PROTOCOL", allowProtocol
      "GIT_CONFIG_COUNT", string (List.length configs) ]
    @ (configs
       |> List.mapi (fun i (key, value) ->
           [ sprintf "GIT_CONFIG_KEY_%d" i, key
             sprintf "GIT_CONFIG_VALUE_%d" i, value ])
       |> List.concat)

/// Cap rendered git output. The head is kept — a status/log/diff front-loads its
/// signal — and the elision is stated, never silent.
let capText (limit: int) (text: string) : string =
    if text.Length <= limit then text
    else sprintf "%s\n[%d more characters omitted]" (text.Substring (0, limit)) (text.Length - limit)

// --- the service -------------------------------------------------------------------------

type private GitRun =
    { Code : int
      Stdout : string
      Stderr : string }

type ReposConfig =
    { Backend : SandboxBackend
      ReposDir : string
      /// Paths beyond `ReposDir` the git sandbox may READ. Empty in production; the
      /// test harness names its local bare-repo fixtures here. None of them may be an
      /// ANCESTOR of `ReposDir`: when both sit under a read-denied region (a HOME, which
      /// is where a session's data dir usually lives) srt re-binds each read path after
      /// the write binds, so an ancestor lands on top of the repos dir read-only and
      /// every clone fails. A sibling cannot cover it.
      ExtraReadPaths : string list
      /// Egress for the git sandbox. Production: github.com.
      AllowedDomains : string list
      /// `GIT_ALLOW_PROTOCOL`. Production pins `https`; the test harness allows `file`.
      AllowProtocol : string
      /// Repo -> clone URL. Production is `RepoRef.cloneUrl` (constructed, github.com
      /// only); the test harness points at local bare fixtures.
      CloneUrl : RepoRef -> string
      /// The GitHub token for the network verbs, resolved for the CREDENTIAL actor a
      /// caller names (Plan 08 precedence, applied by the composition). None =
      /// anonymous — public repos still clone; a private one fails with git's own words.
      ResolveToken : ActorRef -> Async<string option>
      Log : EventLog<SessionEvent> }

/// Who is calling a mutating/network verb. The two halves genuinely differ for the
/// agent: the AGENT is the acting party the event records, while the CREDENTIAL is the
/// turn human's (Plan 08 — no borrowing across actors, and an agent has no scope of
/// its own). At the panel the two are the same person.
type RepoCaller =
    { Actor : ActorRef
      Credential : ActorRef }

/// The Process-side repo manager. Caller-taking members append the acting party onto
/// the event; the read-only inspectors take none because they record nothing.
type ReposService =
    { AddRepo : RepoCaller -> RepoRef -> Async<Result<RepoListing, string>>
      ListRepos : unit -> Async<Result<RepoListing list, string>>
      SwitchBranch : RepoCaller -> RepoRef -> string -> bool -> Async<Result<RepoListing, string>>
      FetchRepo : RepoCaller -> RepoRef -> Async<Result<string, string>>
      RepoStatus : RepoRef -> Async<Result<string, string>>
      RepoLog : RepoRef -> Async<Result<string, string>>
      RepoDiff : RepoRef -> Async<Result<string, string>>
      RemoveRepo : RepoCaller -> RepoRef -> Async<Result<unit, string>> }

[<ImportAll("node:fs")>]
let private fs : obj = jsNative

[<Emit("(() => { try { return $0.readdirSync($1) } catch { return [] } })()")>]
let private readdirSafe (fs: obj) (dir: string) : string array = jsNative

[<Emit("$0.rmSync($1, { recursive: true, force: true })")>]
let private rmRecursive (fs: obj) (path: string) : unit = jsNative

let private outputLimit = 20000

let create (config: ReposConfig) : Result<ReposService, string> =
    Sandboxes.forBackend config.Backend "git" EnvironmentSpec.defaults
    |> Result.map (fun createSandbox ->

        let policy : SandboxPolicy =
            { ReadPaths = config.ReposDir :: config.ExtraReadPaths
              WritePaths = [ config.ReposDir ]
              AllowedDomains = Some config.AllowedDomains
              Env = Sandboxes.hostBaseline (Sandboxes.ambientEnv ())
              WorkingDirectory = Some config.ReposDir }

        // One sandbox for the service's life, created on first use — under srt that is
        // an argv-rewriting wrapper, not a container, so per-verb spawns stay cheap.
        let mutable sandbox : Sandbox option = None
        let ensureSandbox () : Async<Result<Sandbox, string>> =
            async {
                match sandbox with
                | Some ready -> return Ok ready
                | None ->
                    match! createSandbox policy with
                    | Error e -> return Error (sprintf "git sandbox: %s" e)
                    | Ok created ->
                        sandbox <- Some created
                        return Ok created
            }

        let runGit (token: string option) (args: string list) : Async<Result<GitRun, string>> =
            async {
                match! ensureSandbox () with
                | Error e -> return Error e
                | Ok sandbox ->
                    let mutable stdout = ""
                    let mutable stderr = ""
                    let exec : SandboxExec =
                        { Executable = "git"
                          Arguments = args
                          Env = hardenedEnv config.AllowProtocol token |> Map.ofList
                          WorkingDirectory = Some config.ReposDir }
                    match! sandbox.Spawn exec (fun (stream, chunk) ->
                              match stream with
                              | Stdout -> stdout <- stdout + chunk
                              | Stderr -> stderr <- stderr + chunk) with
                    | Error e -> return Error e
                    | Ok handle ->
                        match! handle.Exited with
                        | SandboxRunFailed reason -> return Error reason
                        | SandboxExited code -> return Ok { Code = code; Stdout = stdout; Stderr = stderr }
            }

        /// Run and demand exit 0; a failure surfaces git's own words, capped.
        let runOk (token: string option) (args: string list) : Async<Result<GitRun, string>> =
            async {
                match! runGit token args with
                | Error e -> return Error e
                | Ok run when run.Code <> 0 ->
                    let said = if run.Stderr.Trim () <> "" then run.Stderr else run.Stdout
                    return Error (sprintf "git %s failed (exit %d): %s" (List.tryHead args |> Option.defaultValue "") run.Code (capText 2000 (said.Trim ())))
                | Ok run -> return Ok run
            }

        let pathOf (repo: RepoRef) = sprintf "%s/%s" config.ReposDir (RepoRef.relativePath repo)
        let present (repo: RepoRef) = Fs.exists (sprintf "%s/.git" (pathOf repo))

        let listingOf (repo: RepoRef) : Async<Result<RepoListing, string>> =
            async {
                match! runOk None [ "-C"; pathOf repo; "rev-parse"; "--abbrev-ref"; "HEAD" ] with
                | Error e -> return Error e
                | Ok branch ->
                    match! runOk None [ "-C"; pathOf repo; "status"; "--porcelain" ] with
                    | Error e -> return Error e
                    | Ok status ->
                        return Ok { Repo = repo; Branch = branch.Stdout.Trim (); Dirty = status.Stdout.Trim () <> "" }
            }

        let mintMessageId () : MessageId =
            match MessageId.create (string (Guid.NewGuid ())) with
            | Ok id -> id
            | Error e -> failwithf "message id invariant violated: %s" e

        let append (actor: ActorRef) (event: SessionEvent) : Async<unit> =
            async {
                let! _ = config.Log.Append actor event
                return ()
            }

        let requirePresent (repo: RepoRef) (inner: unit -> Async<Result<'a, string>>) : Async<Result<'a, string>> =
            async {
                if not (present repo) then
                    return Error (sprintf "repo %s is not in this session — add_repo first" (RepoRef.value repo))
                else return! inner ()
            }

        let addRepo (caller: RepoCaller) (repo: RepoRef) : Async<Result<RepoListing, string>> =
            async {
                if present repo then
                    // Already here: answer with the current state and record nothing —
                    // a repeated add is a question, not an act.
                    return! listingOf repo
                else
                    let! token = config.ResolveToken caller.Credential
                    match! runOk token [ "clone"; "--no-recurse-submodules"; config.CloneUrl repo; RepoRef.relativePath repo ] with
                    | Error e -> return Error e
                    | Ok _ ->
                        match! listingOf repo with
                        | Error e -> return Error e
                        | Ok listing ->
                            do! append caller.Actor (SessionEvent.RepoAdded { MessageId = mintMessageId (); Repo = repo; Branch = listing.Branch; Actor = caller.Actor })
                            return Ok listing
            }

        let listRepos () : Async<Result<RepoListing list, string>> =
            async {
                let refs =
                    readdirSafe fs config.ReposDir
                    |> Array.collect (fun owner ->
                        readdirSafe fs (sprintf "%s/%s" config.ReposDir owner)
                        |> Array.choose (fun repo ->
                            match RepoRef.create (sprintf "%s/%s" owner repo) with
                            | Ok ref when Fs.exists (sprintf "%s/%s/%s/.git" config.ReposDir owner repo) -> Some ref
                            | _ -> None))
                    |> Array.toList
                let mutable listings = []
                let mutable failure = None
                for ref in refs do
                    match failure with
                    | Some _ -> ()
                    | None ->
                        match! listingOf ref with
                        | Error e -> failure <- Some e
                        | Ok listing -> listings <- listings @ [ listing ]
                match failure with
                | Some e -> return Error e
                | None -> return Ok listings
            }

        let switchBranch (caller: RepoCaller) (repo: RepoRef) (branch: string) (create: bool) : Async<Result<RepoListing, string>> =
            requirePresent repo (fun () ->
                async {
                    match validBranchName branch with
                    | Error e -> return Error e
                    | Ok branch ->
                        let args =
                            [ "-C"; pathOf repo; "switch" ] @ (if create then [ "-c" ] else []) @ [ branch ]
                        match! runOk None args with
                        | Error e -> return Error e
                        | Ok _ ->
                            match! listingOf repo with
                            | Error e -> return Error e
                            | Ok listing ->
                                do! append caller.Actor (SessionEvent.RepoBranchSwitched { MessageId = mintMessageId (); Repo = repo; Branch = listing.Branch; Created = create; Actor = caller.Actor })
                                return Ok listing
                })

        let fetchRepo (caller: RepoCaller) (repo: RepoRef) : Async<Result<string, string>> =
            requirePresent repo (fun () ->
                async {
                    let! token = config.ResolveToken caller.Credential
                    match! runOk token [ "-C"; pathOf repo; "fetch"; "--prune"; "--no-recurse-submodules"; "origin" ] with
                    | Error e -> return Error e
                    | Ok run ->
                        // Fetch narrates on stderr; an up-to-date fetch says nothing.
                        let said = (run.Stderr + run.Stdout).Trim ()
                        return Ok (if said = "" then "already up to date" else capText outputLimit said)
                })

        let inspect (args: string -> string list) (repo: RepoRef) : Async<Result<string, string>> =
            requirePresent repo (fun () ->
                async {
                    match! runOk None (args (pathOf repo)) with
                    | Error e -> return Error e
                    | Ok run ->
                        let said = run.Stdout.Trim ()
                        return Ok (if said = "" then "(clean — nothing to show)" else capText outputLimit said)
                })

        let removeRepo (caller: RepoCaller) (repo: RepoRef) : Async<Result<unit, string>> =
            requirePresent repo (fun () ->
                async {
                    rmRecursive fs (pathOf repo)
                    do! append caller.Actor (SessionEvent.RepoRemoved { MessageId = mintMessageId (); Repo = repo; Actor = caller.Actor })
                    return Ok ()
                })

        { AddRepo = addRepo
          ListRepos = listRepos
          SwitchBranch = switchBranch
          FetchRepo = fetchRepo
          RepoStatus = inspect (fun path -> [ "-C"; path; "status"; "--porcelain=v1"; "--branch" ])
          RepoLog = inspect (fun path -> [ "-C"; path; "log"; "--oneline"; "-30" ])
          RepoDiff = inspect (fun path -> [ "-C"; path; "diff" ])
          RemoveRepo = removeRepo })

/// The agent-facing capability set for one turn: events attribute the AGENT (it is
/// the acting party), the token is the TURN HUMAN's (Plan 08 — no borrowing across
/// actors, and the agent has no scope of its own).
let agentCaller (turnActor: ActorRef) : RepoCaller =
    { Actor = ActorRef.Agent; Credential = turnActor }

// --- the browser-facing /repos* routes (Plan 14) -----------------------------------------
// The human interface over the SAME service the agent's verbs drive — cookie-gated like
// the connection panels, composed into `Signalling.start` extra routes beside them. At
// the panel the acting party and the credential owner are the same person.

open Fable.Core.JsInterop
open Yession.App
open Yession.Host.Interop

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

type private RepoRequestBody =
    { Repo : string
      Branch : string option
      Create : bool
      PeerId : string option }

let private bodyDecoder : Decoder<RepoRequestBody> =
    Decode.object (fun get ->
        { Repo = get.Optional.Field "repo" Decode.string |> Option.defaultValue ""
          Branch = get.Optional.Field "branch" Decode.string
          Create = get.Optional.Field "create" Decode.bool |> Option.defaultValue false
          PeerId = get.Optional.Field "peerId" Decode.string })

let private readBody (req: IncomingMessage) (cont: string -> unit) =
    let mutable acc = ""
    req.on ("data", fun chunk -> acc <- acc + bufferToString chunk) |> ignore
    req.on ("end", fun _ -> cont acc) |> ignore

let private respondJson (res: ServerResponse) (status: int) (json: string) =
    res.writeHead (status, createObj [ "content-type", box "application/json"; "cache-control", box "no-store" ]) |> ignore
    res.``end`` json

let private respondText (res: ServerResponse) (status: int) (text: string) =
    res.writeHead (status, createObj [ "content-type", box "text/plain"; "cache-control", box "no-store" ]) |> ignore
    res.``end`` text

let private listingsJson (listings: RepoListing list) : string =
    listings
    |> List.map (fun l ->
        Encode.object
            [ "repo", Encode.string (RepoRef.value l.Repo)
              "branch", Encode.string l.Branch
              "dirty", Encode.bool l.Dirty ])
    |> Encode.list
    |> Encode.toString 0

/// The acting human behind a panel request: the cookie's Manager-verified user, or —
/// for unattributed access — the browser's self-asserted peer (the same trust rule as
/// the connection panels: the Manager's policy is the authority behind any credential
/// that self-assertion goes on to resolve).
let private actorOf (identity: CookieIdentity) (peerIdRaw: string option) : Result<ActorRef, string> =
    match identity.Attribution with
    | AttributedUser user -> Ok (UserRef user)
    | UnattributedAccess ->
        match peerIdRaw with
        | Some raw -> PeerId.create raw |> Result.map PeerRef
        | None -> Error "peer id required for an unattributed repo action"

/// Build the /repos* route handler over a started service.
let routes
    (auth: SessionAuth.Auth)
    (service: ReposService)
    (mount: string)
    : IncomingMessage -> ServerResponse -> bool =
    fun req res ->
        let routeOf () = SessionRoute.parseUnder mount req.``method`` (req.url.Split('?').[0])
        match routeOf () with
        | Some RepoList
        | Some (Repo _) ->
            match auth.IdentityOf req with
            | None -> respondText res 401 "unauthorized"
            | Some identity ->
                let handle (body: RepoRequestBody) : unit =
                    match actorOf identity body.PeerId with
                    | Error e -> respondText res 400 e
                    | Ok actor ->
                        let caller : RepoCaller = { Actor = actor; Credential = actor }
                        let respondListing (outcome: Result<RepoListing, string>) =
                            match outcome with
                            | Ok listing -> respondJson res 200 (listingsJson [ listing ])
                            | Error e -> respondText res 400 e
                        Async.StartImmediate (
                            async {
                                match routeOf () with
                                | Some RepoList ->
                                    match! service.ListRepos () with
                                    | Ok listings -> respondJson res 200 (listingsJson listings)
                                    | Error e -> respondText res 502 e
                                | Some (Repo action) ->
                                    match RepoRef.create body.Repo with
                                    | Error e -> respondText res 400 e
                                    | Ok repo ->
                                        match action with
                                        | RepoPanelAction.Add ->
                                            let! outcome = service.AddRepo caller repo
                                            respondListing outcome
                                        | RepoPanelAction.Remove ->
                                            match! service.RemoveRepo caller repo with
                                            | Ok () -> respondJson res 200 """{"ok":true}"""
                                            | Error e -> respondText res 400 e
                                        | RepoPanelAction.Switch ->
                                            match body.Branch with
                                            | None -> respondText res 400 "missing branch"
                                            | Some branch ->
                                                let! outcome = service.SwitchBranch caller repo branch body.Create
                                                respondListing outcome
                                // Unreachable: this handler only runs for the two cases above.
                                | Some _
                                | None -> respondText res 404 "not found"
                            })
                match req.``method`` with
                | "GET" ->
                    handle
                        { Repo = ""
                          Branch = None
                          Create = false
                          PeerId = Interop.queryParamOf req.url "peer_id" }
                | _ ->
                    readBody req (fun raw ->
                        match Decode.fromString bodyDecoder (if raw.Trim () = "" then "{}" else raw) with
                        | Ok body -> handle body
                        | Error e -> respondText res 400 (sprintf "malformed request: %s" e))
            true
        // Not this handler's path: the composing server falls through (to its 404).
        | Some _
        | None -> false
