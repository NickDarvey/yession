module Yession.Host.Manager

// The Session Manager (Step 10): the authority that launches Session Processes. The
// Manager owns launch — a Session Process never self-starts with host authority — and
// keeps the registry of launched Processes. In Phase 2 the Manager and its Session
// Processes share one local Node runtime (each Process is its own composition root
// listening on its own port); the OS-process split is an adapter concern for later
// phases, and nothing in the authority contract depends on it. See docs/design.md §3.

open System
open Yession.Domain
open Yession.Domain.Link
open Yession.SessionProcess

/// A Session Process the Manager has launched and registered.
type ManagedSession =
    { SessionId : SessionId
      ProcessId : string
      Port : int
      BootstrapUri : string
      Host : Host.SessionHost }

type SessionManager =
    { /// Launch a Session Process for a new session and return its bootstrap URI once
      /// the Process has registered (i.e. is listening). Rejects duplicate sessions.
      StartSession : StartSession
      /// The Manager's registry of launched Processes.
      Registered : unit -> ManagedSession list
      /// Look up one registration.
      TryFind : SessionId -> ManagedSession option
      /// Stop every launched Process.
      Stop : unit -> Async<unit> }

/// Create a Session Manager. Ports are allocated from `basePort` upward; `runAgent`
/// is passed through to each launched Process. When `makeSandbox` is given, each
/// launched Process owns a lazily-created WorkSandbox over the sandbox seam (the
/// environment lifecycle of Steps 12–13); the sandbox is created by the SESSION, not
/// the Manager. `makeLog`/`makeDocStore` give each session its durable event log and
/// doc store (Step 19).
let createFull
    (runAgent: RunAgent option)
    (makeSandbox: (SessionId -> CreateSandbox) option)
    (makeLog: (SessionId -> Yession.SessionProcess.EventLog<SessionEvent>) option)
    (makeDocStore: (SessionId -> DocStore.DocStore) option)
    (basePort: int)
    : SessionManager =
    let mutable nextPort = basePort
    let mutable nextProcessNumber = 0
    let mutable registry : Map<string, ManagedSession> = Map.empty

    let startSession (request: SessionLaunchRequest) : Async<SessionLaunchResult> =
        async {
            let key = SessionId.value request.SessionId
            if Map.containsKey key registry then
                return failwithf "session %s is already launched" key
            else
                // basePort 0 = an OS-assigned free port per session (the default);
                // a fixed basePort allocates sequentially for deterministic tests.
                let port = if basePort = 0 then 0 else nextPort
                if basePort <> 0 then nextPort <- nextPort + 1
                let processId = sprintf "session-process-%d" nextProcessNumber
                nextProcessNumber <- nextProcessNumber + 1
                // Launch. The host's listening resolution is the Process's registration
                // back to the Manager. The WorkSandbox composition is per session: no
                // secret references in this in-process topology, so the policy is the
                // spec's plain values under the host baseline.
                // Named sandboxes (Plan 15) in this topology are all the same shape: the
                // in-process Manager has no per-name workspace policy to vary, so a name
                // only changes the environment id. `default` is what everything reaches.
                let makeSandboxes =
                    makeSandbox
                    |> Option.map (fun make ->
                        let createSandbox = make request.SessionId
                        fun log ->
                            let create (name: SandboxRef) (spec: EnvironmentSpec) (extraEnv: Map<string, string>) =
                                let prepare =
                                    Sandboxes.preparePolicy
                                        HostBackend
                                        (fun secret -> async { return Error (sprintf "no secrets channel for '%s'" (SecretName.value secret)) })
                                        None
                                        None
                                        spec
                                Ok (
                                    SessionEnvironment.create
                                        log
                                        createSandbox
                                        (fun () ->
                                            async {
                                                match! prepare () with
                                                | Error e -> return Error e
                                                | Ok policy -> return Ok { policy with Env = Sandboxes.mergeEnv policy.Env extraEnv }
                                            })
                                        (Sandboxes.summaryFor HostBackend spec)
                                        (sprintf "env-%s-%s" (SessionId.value request.SessionId) (SandboxRef.render name)))
                            match WorkSandboxes.create
                                    { Backend = SandboxBackend.describe HostBackend
                                      Credentials = []
                                      Create = create
                                      Log = log
                                      Clock = fun () -> DateTimeOffset.UtcNow } with
                            | Ok sandboxes -> sandboxes
                            | Error e -> failwithf "work sandboxes: %s" e)
                let baseLog = makeLog |> Option.map (fun make -> make request.SessionId)
                let docStore = makeDocStore |> Option.map (fun make -> make request.SessionId)
                // In-process sessions run without an HTTP auth gate (there is no OIDC
                // provider in this composition — it belongs to the ProcessManager);
                // peer-token gating still applies, minted via `Host.MintPeerToken`.
                let! host =
                    // The in-process Manager path has no control RPC, so no notification
                    // channel — the reverse leg exists only across the OS-process boundary.
                    // No mount: an in-process session is served at its own origin root.
                    Host.startFull (fun () -> runAgent) makeSandboxes None baseLog docStore None None None (fun _ _ -> ()) None McpClient.McpConnections.none None request.SessionId None "" None false port
                let bootstrapUri = sprintf "http://127.0.0.1:%d/" host.Port
                let managed =
                    { SessionId = request.SessionId
                      ProcessId = processId
                      Port = host.Port
                      BootstrapUri = bootstrapUri
                      Host = host }
                registry <- Map.add key managed registry
                return
                    { SessionId = request.SessionId
                      ProcessId = processId
                      LocalBootstrapUri = bootstrapUri }
        }

    { StartSession = startSession
      Registered = fun () -> registry |> Map.toList |> List.map snd
      TryFind = fun sessionId -> Map.tryFind (SessionId.value sessionId) registry
      Stop =
        fun () ->
            async {
                for _, managed in Map.toList registry do
                    do! managed.Host.Stop ()
                registry <- Map.empty
            } }

/// `createFull` without doc persistence.
let createWith
    (runAgent: RunAgent option)
    (makeSandbox: (SessionId -> CreateSandbox) option)
    (makeLog: (SessionId -> Yession.SessionProcess.EventLog<SessionEvent>) option)
    (basePort: int)
    : SessionManager =
    createFull runAgent makeSandbox makeLog None basePort

/// `createWith` without durable storage — the deterministic in-memory default.
let create (runAgent: RunAgent option) (makeSandbox: (SessionId -> CreateSandbox) option) (basePort: int) : SessionManager =
    createWith runAgent makeSandbox None basePort
