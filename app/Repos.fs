module Yession.Host.Repos

// The session's repo manager (Plan 14, resurfaced by Plan 15): the agent's MCP verbs
// land here, every mutation appends an attributed event, and the conversation timeline
// is the record everyone reads.
//
// Plan 14 gave this a second, symmetric interface — a panel that could drive every verb.
// Plan 15 retired that half: a human who wants a repo added asks the agent, so there is
// one authorization path, one set of inputs to validate, and one place the act is
// recorded. What the panel is left with is the LISTING, which is now the `repos` query
// at the foot of this file. The cost was weighed and taken: with no working agent, nobody
// can add a repo at all. That is acceptable because it is the same session in which nothing
// else works either — and a panel button kept "just in case" would restore the second
// authorization path, the second set of inputs to validate, and the second place a
// mutation's record can go missing.
//
// Git itself runs through the sandbox seam under the AGENT backend (`host` is the
// explicitly lax choice; `srt` confines each spawn to the repos directory and the
// allowlisted egress). CLONE is the one exception, and it is a whole sandbox rather
// than a flag on a spawn: it writes a work tree whose names are the repo's, and srt
// refuses some of those names anywhere on the disk. See `unconfined` below.
// On top of the confinement, repo-controlled EXECUTION is disabled
// per invocation — hooks, fsmonitor, ext transport — because the WorkSandbox can write
// the repos directory by design, so a poisoned `.git/config` is assumed and made inert
// rather than trusted-by-placement.
//
// The credential (the acting human's GitHub token, Plan 08 precedence) reaches exactly
// one place: the env of the single confined git invocation that needs it. It is never
// in the sandbox policy env, so nothing that outlives the invocation can read it.

open System
open System.Collections.Generic
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
/// WorkSandbox can write it), no prompts, a pinned protocol allowlist, no credential
/// helpers, and the config-driven execution vectors (`hooksPath`, `fsmonitor`,
/// `protocol.ext`) forced off via `GIT_CONFIG_*` — which apply with the highest
/// precedence git knows, so a planted repo config cannot override them.
///
/// `credential.helper` is cleared explicitly because nulling the config FILES does not
/// reach it: Apple's git reads its own extra config (CommandLineTools
/// `share/git-core/gitconfig`, which sets `credential.helper=osxkeychain`) regardless of
/// `GIT_CONFIG_GLOBAL`/`GIT_CONFIG_SYSTEM`, and `GIT_TERMINAL_PROMPT=0` does not cover
/// helpers. A helper here is never useful — the token rides in as a header — and under
/// launchd the keychain helper blocks forever on a prompt nobody can answer, so a 401
/// became a clone hung for days. Cleared, the same fault fails in under a second.
let hardenedEnv (allowProtocol: string) (token: string option) : (string * string) list =
    let configs =
        [ "core.hooksPath", "/dev/null"
          "core.fsmonitor", "false"
          "protocol.ext.allow", "never"
          "credential.helper", "" ]
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

/// The git a verb runs, NAMED rather than looked up on PATH. Every other binary a
/// confined spawn execs is named for this reason — srt's bwrap, socat and ripgrep, the
/// agent's claude — and git was the exception until the exception cost a session.
///
/// On macOS `git` on PATH is `/usr/bin/git`, which is not git: it is a shim that resolves
/// a developer directory first, through `xcode-select` reading the `/var/select` symlink.
/// A sandbox that scopes its reads denies that symlink (srt's macOS escape hatch allows
/// metadata on DIRECTORIES, and a symlink is not one), so the shim fails before git runs
/// and reports it as a broken Xcode install — which is not what happened and not what
/// fixes it. The same shim is why `credential.helper` has to be cleared above.
///
/// Empty or unset keeps PATH, so nothing off-Nix regresses; the installable names one.
let gitExecutable (ambient: Map<string, string>) : string =
    ambient
    |> Map.tryFind "YESSION_BIN_GIT"
    |> Option.map (fun path -> path.Trim ())
    |> Option.filter (fun path -> path <> "")
    |> Option.defaultValue "git"

/// The one exec any git invocation here is built from — the probe and every verb alike.
///
/// A function rather than a record literal per call site, because `hardenedEnv` is not
/// hardening if a call site can be written without it, and one was: the probe spawned
/// `git --version` with an EMPTY env, which is an env no verb ever runs with. So it
/// answered a question about a git nothing here executes, and it answered it wrong.
///
/// git resolves its global config path before it does anything at all, `--version`
/// included. It tolerates an EACCES there — unreadable reads as absent — and treats every
/// other errno as fatal. A sandbox that denies the operator's home (Plan 24's read scope)
/// answers EPERM, not EACCES, so the probe died with `fatal: unable to access
/// '~/.config/git/config'` (exit 128) on a host where every verb, which nulls that path,
/// ran fine. The sandbox was then refused for its whole lifetime in words naming
/// `YESSION_BIN_GIT` and `YESSION_SESSION_READ`: a working binary, and a read scope
/// whose only fault was doing its job. Following that advice would have widened the scope
/// to hand back the home it exists to deny.
let gitExec
    (git: string)
    (workingDirectory: string)
    (allowProtocol: string)
    (token: string option)
    (args: string list)
    : SandboxExec =
    { Executable = git
      Arguments = args
      Env = hardenedEnv allowProtocol token |> Map.ofList
      WorkingDirectory = Some workingDirectory }

/// What a sandbox that cannot run git says. It is a whole sentence with the two knobs in
/// it because the alternative is what shipped: the host binary's own parting words —
/// `xcode-select: error: unable to read data link ...` — which name neither the sandbox
/// nor anything an operator can set.
///
/// Both knobs are honest only because the probe now runs the verbs' env: what it rules out
/// before it speaks is every config file git would otherwise have gone looking for, so
/// what is left really is the binary or the runtime it reads.
let unusableGit (git: string) (reason: string) : string =
    sprintf
        "git ('%s') cannot run inside the sandbox: %s. Name a working git with YESSION_BIN_GIT; if it needs files the sandbox's read scope denies, name those with YESSION_SESSION_READ."
        git
        reason

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
      /// The same directory as a TERMINAL sees it (`Sandboxes.reposVisibleAt`): the host
      /// path itself under the host-family backends, the mount target under docker. Every
      /// listing carries it, because the only reason to clone a repo is to work in it and
      /// the work happens in a terminal.
      VisibleAt : string
      /// Paths beyond `ReposDir` the git sandbox may READ. Empty in production; the
      /// test harness names its local bare-repo fixtures here. None of them may be an
      /// ANCESTOR of `ReposDir`: when both sit under a read-denied region (a HOME, which
      /// is where a session's data dir usually lives) srt re-binds each read path after
      /// the write binds, so an ancestor lands on top of the repos dir read-only and
      /// every clone fails. A sibling cannot cover it.
      ExtraReadPaths : string list
      /// The git binary every verb spawns (`gitExecutable`). A path, or `git` for the
      /// PATH lookup that is only safe where PATH's git is really git.
      Git : string
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
      /// A network verb failed while spending the credential resolved for this actor.
      ///
      /// Beside `ResolveToken` deliberately, because they are two halves of one story: a
      /// caller that can hand a verb a credential but cannot be told the verb failed with it
      /// is exactly the caller that leaves a dead credential reading as healthy. Nothing here
      /// decides what the failure MEANS — git's stderr cannot tell "your token expired" from
      /// "that repo does not exist" (`Repository not found` is what github.com says for
      /// both), so the composition asks the provider, which is the only place that knows.
      OnNetworkFailure : ActorRef -> string -> Async<unit>
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
      /// Delete a checkout, and answer with the path it was at AS A TERMINAL SAW IT (Plan
      /// 26). That path rather than `unit`, because it is the one fact only this service has
      /// and the only one a shell profile could have been holding — the git sandbox's own
      /// path is visible to no terminal in this session.
      ///
      /// `force` is required to delete a checkout with uncommitted changes. An UNREADABLE
      /// checkout needs none: clearing one is what this verb was advertised for long before
      /// it existed, and refusing there would leave no way out of an interrupted clone.
      RemoveRepo : RepoCaller -> RepoRef -> bool -> Async<Result<string, string>> }

[<ImportAll("node:fs")>]
let private fs : obj = jsNative

[<Emit("(() => { try { return $0.readdirSync($1) } catch { return [] } })()")>]
let private readdirSafe (fs: obj) (dir: string) : string array = jsNative

[<Emit("$0.rmSync($1, { recursive: true, force: true })")>]
let private rmRecursive (fs: obj) (path: string) : unit = jsNative

let private outputLimit = 20000

/// Where a clone is BUILT, under the repos directory, before it is anything.
///
/// `git clone` creates its target's `.git` within milliseconds and leaves it without a
/// HEAD for the rest of the clone, so a checkout at its visible path was never evidence
/// that there IS a checkout. Every reader in that window — a second `add_repo` taking the
/// already-here branch, the `repos` query, `repo_status` — met a repository git then
/// refused to describe: `fatal: ambiguous argument 'HEAD'`, which reads as "this repo is
/// empty" and is not. Building here and renaming into place makes the visible path binary:
/// absent, or whole.
///
/// The `~` is load-bearing. `RepoRef.create` admits only letters, digits, `-`, `_` and `.`
/// in an owner segment, so this name can never parse as one — the listing scan cannot
/// mistake a clone in progress for a repo BY CONSTRUCTION, rather than by every future
/// reader of that scan remembering to skip it. `Repos` pins that in the cheap tier.
let stagingDirName = "staging~"

let create (config: ReposConfig) : Result<ReposService, string> =
    Sandboxes.forBackend config.Backend "git" EnvironmentSpec.defaults
    |> Result.map (fun createSandbox ->

        /// The repos directory as an ABSOLUTE path, resolved once here and used for every
        /// path below — never `config.ReposDir` again.
        ///
        /// Every verb but the clone runs `git -C (pathOf repo)` in a sandbox whose working
        /// directory IS this directory, so a relative one is resolved twice: git looks for
        /// `<reposDir>/<reposDir>/owner/repo` and exits 128 with `cannot change to ...: No
        /// such file or directory`. The clone is the one verb that survives it, because it
        /// passes a target relative to that same cwd on purpose — so the checkout lands and
        /// then every verb, `add_repo`'s own listing included, reports a failure about it.
        /// A session whose data directory is relative (`Launch.unlaunched`'s
        /// default is) had exactly that: a repo on disk that no verb would admit to.
        ///
        /// Resolved HERE rather than only at the composition root because this is where a
        /// relative path stops being a path and starts being a wrong answer, and the next
        /// caller to build one of these has not read the root.
        let reposDir = Fs.absolute config.ReposDir

        let policy : SandboxPolicy =
            { ReadPaths = reposDir :: config.ExtraReadPaths
              WritePaths = [ reposDir ]
              AllowedDomains = Some config.AllowedDomains
              Env = Sandboxes.hostBaseline (Sandboxes.ambientEnv ())
              WorkingDirectory = Some reposDir
              Filesystem = Confined }

        /// Every git this service spawns, built in the one place that carries the hardened
        /// env (`gitExec`) — so the probe below cannot drift from the verbs it speaks for.
        let execFor (token: string option) (args: string list) : SandboxExec =
            gitExec config.Git reposDir config.AllowProtocol token args

        /// `git --version` inside the sandbox, before any verb runs one. A sandbox that
        /// cannot run git is not a git sandbox, and this is where it says so: the
        /// alternative is every verb reporting whatever the host's binary printed on its
        /// way out, once per verb, in words that name neither the sandbox nor a knob.
        ///
        /// It runs a VERB's environment (no token: `--version` reaches no remote), because
        /// a probe that runs anything else proves nothing about the invocations it gates —
        /// which is exactly how it once refused a git that worked.
        ///
        /// It costs one spawn per sandbox lifetime — two per session, milliseconds under
        /// srt — and it is the guard that catches the NEXT unreadable runtime rather than
        /// only the one that prompted it.
        let usable (sandbox: Sandbox) : Async<Result<Sandbox, string>> =
            async {
                let mutable output = ""
                match! sandbox.Spawn (execFor None [ "--version" ]) (fun (_, chunk) -> output <- output + chunk) with
                | Error reason -> return Error (unusableGit config.Git reason)
                | Ok handle ->
                    match! handle.Exited with
                    | SandboxExited 0 -> return Ok sandbox
                    | SandboxExited code ->
                        return
                            Error (unusableGit config.Git (sprintf "exit %d: %s" code (capText 400 (output.Trim ()))))
                    | SandboxRunFailed reason -> return Error (unusableGit config.Git reason)
            }

        // A sandbox per policy, created and PROVED on first use — under srt that is an
        // argv-rewriting wrapper, not a container, so per-verb spawns stay cheap. The
        // answer is remembered either way: a sandbox that failed its probe fails every
        // verb with the same sentence rather than re-probing per call.
        let lazySandbox (policy: SandboxPolicy) : unit -> Async<Result<Sandbox, string>> =
            let mutable ready : Result<Sandbox, string> option = None
            fun () ->
                async {
                    match ready with
                    | Some answer -> return answer
                    | None ->
                        let! answer =
                            async {
                                match! createSandbox policy with
                                | Error e -> return Error (sprintf "git sandbox: %s" e)
                                | Ok created -> return! usable created
                            }
                        ready <- Some answer
                        return answer
                }

        let confined = lazySandbox policy

        /// The clone's sandbox, and nothing else's: a checkout can contain a `.vscode`,
        /// a `.mcp.json`, a `.gitmodules` — names srt refuses to write anywhere, over any
        /// allow-path — so the one verb that materializes a work tree runs with the
        /// filesystem unenforced. Egress stays pinned to `AllowedDomains`, the env stays
        /// the hardened one, and every other verb keeps the policy above: none of them
        /// writes a path srt objects to. Undo the moment srt can exempt a subtree instead
        /// of a whole spawn — `clone` goes back to `confined` and this sandbox goes away.
        let unconfined = lazySandbox { policy with Filesystem = Unconfined }

        let runGit
            (sandboxFor: unit -> Async<Result<Sandbox, string>>)
            (token: string option)
            (args: string list)
            : Async<Result<GitRun, string>> =
            async {
                match! sandboxFor () with
                | Error e -> return Error e
                | Ok sandbox ->
                    let mutable stdout = ""
                    let mutable stderr = ""
                    match! sandbox.Spawn (execFor token args) (fun (stream, chunk) ->
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
        let runOk
            (sandboxFor: unit -> Async<Result<Sandbox, string>>)
            (token: string option)
            (args: string list)
            : Async<Result<GitRun, string>> =
            async {
                match! runGit sandboxFor token args with
                | Error e -> return Error e
                | Ok run when run.Code <> 0 ->
                    let said = if run.Stderr.Trim () <> "" then run.Stderr else run.Stdout
                    return Error (sprintf "git %s failed (exit %d): %s" (List.tryHead args |> Option.defaultValue "") run.Code (capText 2000 (said.Trim ())))
                | Ok run -> return Ok run
            }

        let pathOf (repo: RepoRef) = sprintf "%s/%s" reposDir (RepoRef.relativePath repo)
        /// The same checkout as a TERMINAL in this session reaches it. What the listing
        /// reports, and the only path anything outside the git sandbox can act on.
        let visiblePathOf (repo: RepoRef) = sprintf "%s/%s" config.VisibleAt (RepoRef.relativePath repo)
        let present (repo: RepoRef) = Fs.exists (sprintf "%s/.git" (pathOf repo))

        let listingOf (repo: RepoRef) : Async<Result<RepoListing, string>> =
            async {
                match! runOk confined None [ "-C"; pathOf repo; "rev-parse"; "--abbrev-ref"; "HEAD" ] with
                | Error e -> return Error e
                | Ok branch ->
                    match! runOk confined None [ "-C"; pathOf repo; "status"; "--porcelain" ] with
                    | Error e -> return Error e
                    | Ok status ->
                        return
                            Ok
                                { Repo = repo
                                  Branch = branch.Stdout.Trim ()
                                  Dirty = status.Stdout.Trim () <> ""
                                  Path = visiblePathOf repo }
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

        /// Clone into the staging area and MOVE the finished thing into place. The rename
        /// is the moment the repo exists; before it, nothing at the visible path suggests
        /// one is coming, and a clone that dies leaves the wreckage somewhere nobody reads.
        let cloneIntoPlace (caller: RepoCaller) (repo: RepoRef) : Async<Result<RepoListing, string>> =
            async {
                let! token = config.ResolveToken caller.Credential
                // Relative, because the sandbox's working directory is the repos dir; git
                // creates the leading directories itself.
                let relative = sprintf "%s/%s" stagingDirName (string (Guid.NewGuid ()))
                let staging = sprintf "%s/%s" reposDir relative
                // `--template=` is empty deliberately: git's default templates are a
                // set of `.git/hooks/*.sample` files, and srt's macOS profile denies
                // every write under `**/.git/hooks/**` unconditionally — so the copy
                // that populates them is what fails, and a clone that asks for no
                // templates never attempts it. Nothing here wants a hooks directory
                // anyway: repo-controlled execution is off by construction.
                match! runOk unconfined token [ "clone"; "--no-recurse-submodules"; "--template="; config.CloneUrl repo; relative ] with
                | Error e ->
                    // git removes a target it created itself, but not one it was killed
                    // out of. Either way the staging area is ours to leave clean.
                    rmRecursive fs staging
                    // Only when a credential was actually spent: an anonymous clone that
                    // failed says nothing about anybody's sign-in.
                    if token.IsSome then do! config.OnNetworkFailure caller.Credential e
                    return Error e
                | Ok _ ->
                    // The owner directory is the rename's destination PARENT, and git made
                    // it inside the staging area rather than here.
                    Fs.ensureDir (sprintf "%s/%s" reposDir (RepoRef.owner repo))
                    Fs.rename staging (pathOf repo)
                    match! listingOf repo with
                    | Error e -> return Error e
                    | Ok listing ->
                        do! append caller.Actor (SessionEvent.RepoAdded { MessageId = mintMessageId (); Repo = repo; Branch = listing.Branch; Actor = caller.Actor })
                        return Ok listing
            }

        /// Clones in flight, keyed by repo. A clone is not instant, and the same repo asked
        /// for twice while the first is still running used to start a SECOND clone into the
        /// same directory. Joining the first is both correct and the answer the caller
        /// wanted — `Broker`'s token refresh is this shape for the same reason.
        let cloning = Dictionary<string, JS.Promise<Result<RepoListing, string>>> ()

        let addRepo (caller: RepoCaller) (repo: RepoRef) : Async<Result<RepoListing, string>> =
            async {
                if present repo then
                    // Already here: answer with the current state and record nothing —
                    // a repeated add is a question, not an act. `present` can be trusted to
                    // mean WHOLE now, which is what the staging rename above buys.
                    match! listingOf repo with
                    | Ok listing -> return Ok listing
                    | Error e ->
                        // The one bad state the rename cannot prevent, because it predates
                        // it: a checkout an interrupted clone left at the visible path. git
                        // describes it as `ambiguous argument 'HEAD'`, which reads as "this
                        // repo is empty" and sent a whole session looking for the wrong
                        // fault. Say what it is and how to clear it instead.
                        return
                            Error (
                                sprintf
                                    "%s is already checked out here, but git cannot read that checkout: %s. That is what an interrupted clone leaves behind — remove_repo it, then add it again."
                                    (RepoRef.value repo)
                                    e)
                else
                    let key = RepoRef.value repo
                    let pending =
                        match cloning.TryGetValue key with
                        | true, running -> running
                        | _ ->
                            let running = cloneIntoPlace caller repo |> Async.StartAsPromise
                            cloning.[key] <- running
                            // Cleared on settle, so a clone that failed can be retried. The
                            // workflow answers with `Error` rather than rejecting, so this
                            // runs on both outcomes.
                            running.``then`` (fun outcome -> cloning.Remove key |> ignore; outcome) |> ignore
                            running
                    return! Interop.awaitPromise pending
            }

        let listRepos () : Async<Result<RepoListing list, string>> =
            async {
                let refs =
                    readdirSafe fs reposDir
                    |> Array.collect (fun owner ->
                        readdirSafe fs (sprintf "%s/%s" reposDir owner)
                        |> Array.choose (fun repo ->
                            match RepoRef.create (sprintf "%s/%s" owner repo) with
                            | Ok ref when Fs.exists (sprintf "%s/%s/%s/.git" reposDir owner repo) -> Some ref
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
                        match! runOk confined None args with
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
                    match! runOk confined token [ "-C"; pathOf repo; "fetch"; "--prune"; "--no-recurse-submodules"; "origin" ] with
                    | Error e ->
                        if token.IsSome then do! config.OnNetworkFailure caller.Credential e
                        return Error e
                    | Ok run ->
                        // Fetch narrates on stderr; an up-to-date fetch says nothing.
                        let said = (run.Stderr + run.Stdout).Trim ()
                        return Ok (if said = "" then "already up to date" else capText outputLimit said)
                })

        let inspect (args: string -> string list) (repo: RepoRef) : Async<Result<string, string>> =
            requirePresent repo (fun () ->
                async {
                    match! runOk confined None (args (pathOf repo)) with
                    | Error e -> return Error e
                    | Ok run ->
                        let said = run.Stdout.Trim ()
                        return Ok (if said = "" then "(clean — nothing to show)" else capText outputLimit said)
                })

        let removeRepo (caller: RepoCaller) (repo: RepoRef) (force: bool) : Async<Result<string, string>> =
            requirePresent repo (fun () ->
                async {
                    let remove () =
                        async {
                            rmRecursive fs (pathOf repo)
                            do!
                                append
                                    caller.Actor
                                    (SessionEvent.RepoRemoved
                                        { MessageId = mintMessageId (); Repo = repo; Actor = caller.Actor })
                            return Ok (visiblePathOf repo)
                        }
                    // Uncommitted work is the one thing removal cannot undo: `add_repo` brings
                    // back the commits and nothing else. So it is refused, and `force` is a
                    // second decision — taken here, with the checkout, because a caller who
                    // could delete without asking is the caller this exists to stop.
                    match! listingOf repo with
                    | Ok listing when listing.Dirty && not force ->
                        return
                            Error (
                                sprintf
                                    "%s has uncommitted changes, and removing it deletes them — adding the repo again brings back the commits and nothing else. Commit or push them first, or pass force to delete them."
                                    (RepoRef.value repo))
                    | Ok _ -> return! remove ()
                    // git cannot read the checkout: exactly what an interrupted clone leaves
                    // behind, and exactly what `add_repo` points at this verb to clear. There
                    // is no dirtiness to protect, because there is nothing git can tell us
                    // about — refusing here would leave that state with no way out.
                    | Error _ -> return! remove ()
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

// --- the `repos` query (Plan 15) ----------------------------------------------------------
// What was the Repos PANEL is now a registered query, and the panel's three write actions
// are gone: a human who wants a repo added asks the agent, and the mutation lands in the
// timeline attributed. What survives is exactly the half worth keeping — the listing,
// which is the FILESYSTEM's answer, so it can never disagree with `git status`.

let queryName : QueryName =
    match QueryName.create "repos" with
    | Ok name -> name
    | Error e -> failwithf "repos query name: %s" e

let private queryDef : QueryDef =
    { Name = queryName
      Title = "repos"
      Description =
        "The repos checked out in this session, each with the branch it is on, whether it \
         has uncommitted changes, and the path a terminal here reaches it at. Read from the \
         checkouts themselves, so it always agrees with git."
      Shape =
        Rows
            [ QueryColumn.create "repo" "repo"
              QueryColumn.create "branch" "branch"
              QueryColumn.create "dirty" "uncommitted changes"
              QueryColumn.create "path" "path" ] }

/// Register the listing as a query. The service keeps its typed `ListRepos` — the query
/// is a projection of it, not a replacement — so the shape lives beside the thing it
/// describes rather than in the composition root.
let query (service: ReposService) : Queries.QueryRegistration =
    { Def = queryDef
      Read =
        fun () ->
            async {
                match! service.ListRepos () with
                | Error e -> return Error e
                | Ok listings ->
                    return
                        Ok (RowsOf (
                            listings
                            |> List.map (fun listing ->
                                [ "repo", CellText (RepoRef.value listing.Repo)
                                  "branch", CellText listing.Branch
                                  "dirty", CellFlag listing.Dirty
                                  "path", CellText listing.Path ])))
            } }
