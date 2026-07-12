module Yession.Host.Main

// Entry point for running the Session Process locally (`mise run start`). Configuration
// comes from the environment so the repo interface stays declarative.

open Yession.Domain
open Yession.Host

let private expect =
    function
    | Ok v -> v
    | Error e -> failwith e

let private port = Interop.envOr "YESSION_PORT" "8080" |> int
let private token = Interop.envOr "YESSION_TOKEN" "local-dev-token"
let private sessionId = SessionId.create (Interop.envOr "YESSION_SESSION" "local-session") |> expect

// The real agent runs only when the process has credentials; without them the session
// still works as a human-only collaborative session.
let private runAgent =
    if Interop.envOr "ANTHROPIC_API_KEY" (Interop.envOr "CLAUDE_CODE_OAUTH_TOKEN" "") <> "" then Some Agent.run else None

// The product topology (Phase 2): a Session Manager launches the default session's
// Process with scoped environment capabilities over the local-process backend.
// Durable facts survive restarts: each session's log is an append-only JSONL file
// under the data directory.
let private dataDir = Interop.envOr "YESSION_DATA_DIR" ".yession/data"

let private makeLog (id: SessionId) =
    EventStore.openLog (sprintf "%s/%s.events.jsonl" dataDir (SessionId.value id)) id (fun () -> System.DateTimeOffset.UtcNow)

Async.StartImmediate(
    async {
        let manager =
            Manager.createWith runAgent (Some (Backends.LocalProcessBackend.create ())) (Some makeLog) port
        let! launched = manager.StartSession { SessionId = sessionId; SessionToken = token }
        printfn
            "Yession Session Manager: session %s launched at %s  (process=%s, agent=%s)"
            (SessionId.value launched.SessionId)
            launched.LocalBootstrapUri
            launched.ProcessId
            (if Option.isSome runAgent then "on" else "off")
    })
