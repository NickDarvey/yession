module Yession.Host.WorkSandboxes

// The session's WorkSandboxes, by name (Plan 15, stage 2).
//
// A session used to have exactly one environment, so it needed no name. Now the agent can
// ask for a `test` sandbox beside the `default` one — and asking twice returns the SAME
// one. That idempotence is the whole point: it is what lets the declarative form
// (`yession.yaml`, the follow-up plan) be nothing more than a fold of the file into these
// commands at boot, run on every boot, converging rather than accumulating.
//
// The rule when an ask does NOT match what is running is the interesting half. Same name,
// same configuration: hand back the running one and record nothing, because nothing
// happened. Same name, DIFFERENT configuration: refuse, and say what differs. Never
// recreate silently — a sandbox has processes in it, and "converging" by killing
// somebody's build is not convergence.
//
// Credential forwarding is named, never a flag. `forward = ["github"]` resolves that
// credential for the TURN HUMAN (Plan 08 precedence, applied by the composition) into the
// sandbox's policy env at spawn. The event records WHICH names and WHOSE — the value
// reaches the sandbox env and nothing else, and the event type cannot carry one.

open System
open Yession.Domain
open Yession.SessionProcess

/// A credential this session knows how to forward, by name. The registry holds the name
/// and where the value lands; resolving it is the composition's business, because who a
/// credential belongs to is a Plan 08 question this module has no opinion on.
type CredentialSource =
    { Name : string
      /// The environment variable the value takes inside the sandbox.
      EnvVar : string
      /// Resolve for one actor. `None` = that actor has no such credential, which is a
      /// legible refusal rather than a silent start without it: a sandbox that was asked
      /// to forward `github` and did not is a sandbox whose `git push` fails much later,
      /// somewhere less informative.
      Resolve : ActorRef -> Async<string option> }

/// Who is asking. The two halves differ for the agent exactly as they do for the repo
/// verbs: the AGENT is the acting party the event records, the CREDENTIAL owner is the
/// turn human, because the agent has no scope of its own.
[<RequireQualifiedAccess>]
type SandboxCaller =
    { Actor : ActorRef
      Credential : ActorRef }

/// One sandbox the session has. Present in the registry does NOT mean started — the
/// environment underneath is lazy, and `default` exists from boot without a sandbox
/// existing anywhere.
type RunningSandbox =
    { Name : SandboxName
      Backend : string
      /// The credential names forwarded into it, normalised (trimmed, deduped, sorted) so
      /// that "is this the same configuration" is a comparison rather than a judgement.
      Forwarded : string list
      /// Who asked for it. `None` for `default`, which nobody asked for.
      StartedBy : ActorRef option
      StartedAt : DateTimeOffset option
      Environment : SessionEnvironment.SessionEnvironment }

type WorkSandboxesConfig =
    { /// How the backend describes itself, for the event and the query.
      Backend : string
      Credentials : CredentialSource list
      /// Build the environment for a name, with the resolved credentials as extra policy
      /// env. Synchronous and fallible: whether this backend can host another sandbox is
      /// known without starting one.
      Create : SandboxName -> Map<string, string> -> Result<SessionEnvironment.SessionEnvironment, string>
      Log : EventLog<SessionEvent>
      Clock : unit -> DateTimeOffset }

type WorkSandboxes =
    { /// Get-or-create by name. Idempotent when the configuration matches; a legible
      /// error when it does not.
      Ensure : SandboxCaller -> SandboxName -> string list -> Async<Result<RunningSandbox, string>>
      Stop : SandboxCaller -> SandboxName -> Async<Result<unit, string>>
      /// The environment a terminal runs in. Total, because a terminal has to be told no
      /// in the same shape it is told anything else — an unknown name resolves to an
      /// environment that refuses every spawn with the reason.
      EnvironmentFor : SandboxName -> SessionEnvironment.SessionEnvironment
      /// Every sandbox the session has, `default` first.
      Listed : unit -> RunningSandbox list
      /// Stop all of them — session shutdown. Sandbox lifetime is session lifetime.
      StopAll : unit -> Async<unit> }

/// Normalise a forwarding list so two asks that mean the same thing compare equal.
/// Without this, `["github"]` and `["GitHub", "github"]` would be a configuration
/// CHANGE, and the second ask would be refused for no reason a caller could see.
let normaliseForward (names: string list) : string list =
    names
    |> List.map (fun name -> (defaultArg (Option.ofObj name) "").Trim().ToLowerInvariant ())
    |> List.filter (fun name -> name <> "")
    |> List.distinct
    |> List.sort

/// An environment for a name the session does not have. Refuses in the same shape a real
/// one does, naming what is wrong rather than the generic "no environment".
let private missing (name: SandboxName) : SessionEnvironment.SessionEnvironment =
    let reason =
        sprintf
            "there is no sandbox named '%s' in this session — start_work_sandbox creates one"
            (SandboxName.value name)
    { Ensure = fun _ _ -> async { return EnvironmentUnavailable reason }
      Spawn = fun _ _ -> async { return Error reason }
      SpawnPty = fun _ _ _ _ -> async { return Error reason }
      Stop = fun () -> async { return () }
      CurrentRef = fun () -> None }

let create (config: WorkSandboxesConfig) : Result<WorkSandboxes, string> =
    // `default` exists from boot, because it is the sandbox every session has always had:
    // a terminal opened without naming one lands here, and nothing about a pre-Plan-15
    // session changes. It is created eagerly and started lazily, exactly as before.
    config.Create SandboxName.defaultName Map.empty
    |> Result.map (fun defaultEnvironment ->

    let mutable entries : (string * RunningSandbox) list =
        [ SandboxName.value SandboxName.defaultName,
          { Name = SandboxName.defaultName
            Backend = config.Backend
            Forwarded = []
            StartedBy = None
            StartedAt = None
            Environment = defaultEnvironment } ]

    let find (name: SandboxName) =
        entries |> List.tryFind (fun (key, _) -> key = SandboxName.value name) |> Option.map snd

    let mintMessageId () : MessageId =
        match MessageId.create (string (Guid.NewGuid ())) with
        | Ok id -> id
        | Error e -> failwithf "message id invariant violated: %s" e

    let append (actor: ActorRef) (event: SessionEvent) : Async<unit> =
        async {
            let! _ = config.Log.Append actor event
            return ()
        }

    /// Resolve every named credential for one actor, or say which one could not be.
    let resolveForward (owner: ActorRef) (names: string list) : Async<Result<Map<string, string>, string>> =
        async {
            let mutable resolved = Map.empty
            let mutable failure = None
            for name in names do
                match failure with
                | Some _ -> ()
                | None ->
                    match config.Credentials |> List.tryFind (fun source -> source.Name = name) with
                    | None ->
                        let known =
                            match config.Credentials |> List.map (fun s -> s.Name) with
                            | [] -> "this session forwards none"
                            | available -> "this session knows: " + String.concat ", " available
                        failure <- Some (sprintf "there is no credential called '%s' (%s)" name known)
                    | Some source ->
                        match! source.Resolve owner with
                        | None ->
                            failure <-
                                Some (
                                    sprintf
                                        "%s has no '%s' credential to forward — sign in on the settings panel first"
                                        (ActorRef.token owner)
                                        name)
                        | Some value -> resolved <- Map.add source.EnvVar value resolved
            match failure with
            | Some e -> return Error e
            | None -> return Ok resolved
        }

    let ensure (caller: SandboxCaller) (name: SandboxName) (forward: string list) : Async<Result<RunningSandbox, string>> =
        async {
            let wanted = normaliseForward forward
            match find name with
            | Some existing when existing.Forwarded = wanted ->
                // The idempotent case. Make sure it is actually up (the environment is
                // lazy, and a stopped one recreates here), and record NOTHING — a second
                // ask changed nothing, so the timeline should not claim it did.
                match! existing.Environment.Ensure None (sprintf "sandbox '%s' was asked for" (SandboxName.value name)) with
                | EnvironmentUnavailable reason -> return Error reason
                | EnvironmentAvailable -> return Ok existing
            | Some existing ->
                let render names = match names with [] -> "nothing" | some -> String.concat ", " some
                return
                    Error (
                        sprintf
                            "sandbox '%s' is already running and forwards %s, not %s — stop_work_sandbox it first if you want to change that (anything running in it dies with it)"
                            (SandboxName.value name)
                            (render existing.Forwarded)
                            (render wanted))
            | None ->
                match! resolveForward caller.Credential wanted with
                | Error e -> return Error e
                | Ok credentialEnv ->
                    match config.Create name credentialEnv with
                    | Error e -> return Error e
                    | Ok environment ->
                        match! environment.Ensure None (sprintf "sandbox '%s' was started" (SandboxName.value name)) with
                        | EnvironmentUnavailable reason -> return Error reason
                        | EnvironmentAvailable ->
                            let startedAt = config.Clock ()
                            let entry =
                                { Name = name
                                  Backend = config.Backend
                                  Forwarded = wanted
                                  StartedBy = Some caller.Actor
                                  StartedAt = Some startedAt
                                  Environment = environment }
                            entries <- entries @ [ SandboxName.value name, entry ]
                            do!
                                append
                                    caller.Actor
                                    (SessionEvent.WorkSandboxStarted
                                        { MessageId = mintMessageId ()
                                          Sandbox = name
                                          Backend = config.Backend
                                          Forwarded = wanted
                                          CredentialOwner = (if List.isEmpty wanted then None else Some caller.Credential)
                                          Actor = caller.Actor })
                            return Ok entry
        }

    let stop (caller: SandboxCaller) (name: SandboxName) : Async<Result<unit, string>> =
        async {
            match find name with
            | None ->
                return Error (sprintf "there is no sandbox named '%s' in this session" (SandboxName.value name))
            | Some entry ->
                do! entry.Environment.Stop ()
                // `default` keeps its ENTRY — it is the sandbox every session has, and a
                // terminal that names nothing must still find it — but loses its
                // configuration, so a later start may forward something different. Any
                // other name leaves entirely, which is what makes "stop it first, then
                // start it with different forwarding" work.
                if name = SandboxName.defaultName then
                    entries <-
                        entries
                        |> List.map (fun (key, existing) ->
                            if key = SandboxName.value name then
                                key, { existing with Forwarded = []; StartedBy = None; StartedAt = None }
                            else key, existing)
                else
                    entries <- entries |> List.filter (fun (key, _) -> key <> SandboxName.value name)
                do!
                    append
                        caller.Actor
                        (SessionEvent.WorkSandboxStopped
                            { MessageId = mintMessageId (); Sandbox = name; Actor = caller.Actor })
                return Ok ()
        }

    let environmentFor (name: SandboxName) : SessionEnvironment.SessionEnvironment =
        match find name with
        | Some entry -> entry.Environment
        | None -> missing name

    let stopAll () : Async<unit> =
        async {
            for _, entry in entries do
                do! entry.Environment.Stop ()
        }

    { Ensure = ensure
      Stop = stop
      EnvironmentFor = environmentFor
      Listed = fun () -> entries |> List.map snd
      StopAll = stopAll })

/// A registry over ONE already-built environment, under the `default` name: the shape
/// every session had before Plan 15. Starting or stopping a second one is refused, because
/// there is no factory here to build it with — a composition that wants named sandboxes
/// hands `create` a `Create`.
let singleton (backend: string) (environment: SessionEnvironment.SessionEnvironment) : WorkSandboxes =
    let entry =
        { Name = SandboxName.defaultName
          Backend = backend
          Forwarded = []
          StartedBy = None
          StartedAt = None
          Environment = environment }
    { Ensure =
        fun _ name _ ->
            async {
                if name <> SandboxName.defaultName then
                    return Error "this session has only its default sandbox"
                else
                    match! environment.Ensure None "the sandbox was asked for" with
                    | EnvironmentUnavailable reason -> return Error reason
                    | EnvironmentAvailable -> return Ok entry
            }
      Stop =
        fun _ name ->
            async {
                if name <> SandboxName.defaultName then
                    return Error "this session has only its default sandbox"
                else
                    do! environment.Stop ()
                    return Ok ()
            }
      EnvironmentFor = fun _ -> environment
      Listed = fun () -> [ entry ]
      StopAll = environment.Stop }

/// A session composed without any sandbox at all. It still HAS a `default` — every
/// session does — but that default refuses everything, which is what a Host started
/// without the composition has always done. Not an empty registry: an empty one would
/// make `default` an unknown NAME, and "there is no sandbox named default" is a confusing
/// way to say "this session has no environment".
let unavailable : WorkSandboxes =
    let entry =
        { Name = SandboxName.defaultName
          Backend = "none"
          Forwarded = []
          StartedBy = None
          StartedAt = None
          Environment = SessionEnvironment.unavailable }
    { Ensure = fun _ _ _ -> async { return Error "this session has no environment" }
      Stop = fun _ _ -> async { return Error "this session has no environment" }
      EnvironmentFor = fun _ -> SessionEnvironment.unavailable
      Listed = fun () -> [ entry ]
      StopAll = fun () -> async { return () } }

// --- the `work_sandboxes` query (Plan 15) -------------------------------------------------

let queryName : QueryName =
    match QueryName.create "work_sandboxes" with
    | Ok name -> name
    | Error e -> failwithf "work sandboxes query name: %s" e

let private queryDef : QueryDef =
    { Name = queryName
      Title = "work sandboxes"
      Description =
        "The sandboxes commands can run in, each with the backend confining it, whether it \
         is up, and which credentials were forwarded into it and by whom. Read from the \
         processes themselves."
      Shape =
        Rows
            [ QueryColumn.create "name" "name"
              QueryColumn.create "backend" "backend"
              QueryColumn.create "state" "state"
              QueryColumn.create "forwarding" "forwarding"
              QueryColumn.create "started_by" "started by"
              QueryColumn.create "started_at" "started" ] }

/// Register the sandboxes as a query. `state` is read from the RUNNING sandbox rather
/// than from what the registry was told — the same rule the repos listing follows, for
/// the same reason: a panel that can disagree with reality teaches people to distrust it.
///
/// It takes a GETTER, not a registry: the query surface is composed before the Host is
/// started and the Host is what builds the registry, so the value does not exist yet when
/// this is registered. A read happens later, by which time it does.
let query (current: unit -> WorkSandboxes) : Queries.QueryRegistration =
    { Def = queryDef
      Read =
        fun () ->
            async {
                return
                    Ok (RowsOf (
                        (current ()).Listed ()
                        |> List.map (fun entry ->
                            [ "name", CellText (SandboxName.value entry.Name)
                              "backend", CellText entry.Backend
                              "state",
                              CellText (
                                  match entry.Environment.CurrentRef () with
                                  | Some ref -> "running (" + ref + ")"
                                  | None -> "not started")
                              "forwarding",
                              (match entry.Forwarded with
                               | [] -> CellText "nothing"
                               | names -> CellText (String.concat ", " names))
                              "started_by",
                              (match entry.StartedBy with
                               | Some actor -> CellText (ActorRef.token actor)
                               | None -> CellAbsent)
                              "started_at",
                              (match entry.StartedAt with
                               | Some at -> CellText (at.ToString "o")
                               | None -> CellAbsent) ])))
            } }
