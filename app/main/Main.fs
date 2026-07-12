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

Async.StartImmediate(
    async {
        let! host = Host.startWith runAgent sessionId token port
        printfn
            "Yession Session Process listening on http://127.0.0.1:%d  (session=%s, agent=%s)"
            host.Port
            (SessionId.value host.SessionId)
            (if Option.isSome runAgent then "on" else "off")
    })
