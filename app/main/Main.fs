module Yession.Host.Main

// Entry point for running the Session Process locally (`mise run start`). Configuration
// comes from the environment so the repo interface stays declarative.

open Yession.Domain
open Yession.Host

let private expect =
    function
    | Ok v -> v
    | Error e -> failwith e

// Default 0 = a random OS-assigned port, so multiple instances coexist.
let private port = Interop.envOr "YESSION_PORT" "0" |> int
let private token = Interop.envOr "YESSION_TOKEN" "local-dev-token"
let private sessionId = SessionId.create (Interop.envOr "YESSION_SESSION" "local-session") |> expect

// The real agent runs only when the process has credentials; without them the session
// still works as a human-only collaborative session.
let private runAgent =
    if Interop.envOr "ANTHROPIC_API_KEY" (Interop.envOr "CLAUDE_CODE_OAUTH_TOKEN" "") <> "" then Some Agent.run else None

// The product topology (Phase 2): a Session Manager launches the default session's
// Process with scoped environment capabilities over the local-process backend.
// Everything durable survives restarts: each session's event log AND its collaborative
// document (unsent queue entries, drafts) are append-only JSONL files under the data
// directory (Step 19).
let private dataDir = Interop.envOr "YESSION_DATA_DIR" ".yession/data"

let private makeLog (id: SessionId) =
    EventStore.openLog (sprintf "%s/%s.events.jsonl" dataDir (SessionId.value id)) id (fun () -> System.DateTimeOffset.UtcNow)

let private makeDocStore (id: SessionId) =
    DocStore.openStore (sprintf "%s/%s.doc.jsonl" dataDir (SessionId.value id))

Async.StartImmediate(
    async {
        let manager =
            Manager.createFull runAgent (Some (Backends.LocalProcessBackend.create ())) (Some makeLog) (Some makeDocStore) port
        let! launched = manager.StartSession { SessionId = sessionId; SessionToken = token }
        printfn
            "Yession Session Manager: session %s launched at %s  (process=%s, agent=%s)"
            (SessionId.value launched.SessionId)
            launched.LocalBootstrapUri
            launched.ProcessId
            (if Option.isSome runAgent then "on" else "off")
    })
