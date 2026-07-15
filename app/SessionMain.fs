module Yession.Host.SessionMain

// The Session Process entry (Phase 4, Step 23): runs exactly ONE session, configured
// from the environment — the Manager's spawn contract — over the session's own data
// directory. Once listening it prints exactly one JSON readiness line to stdout;
// everything else it writes is logging. Interim until Step 24: no environment
// capabilities cross the process boundary, so the agent runs conversational-only and
// records environment needs as unavailable.

open Yession.Domain
open Yession.Host

let private expect =
    function
    | Ok v -> v
    | Error e -> failwith e

let private sessionId = SessionId.create (Interop.envOr "YESSION_SESSION" "local-session") |> expect
let private token = Interop.envOr "YESSION_TOKEN" "local-dev-token"
let private port = Interop.envOr "YESSION_PORT" "0" |> int
let private dataDir =
    Interop.envOr "YESSION_SESSION_DATA" (sprintf ".yession/sessions/%s" (SessionId.value sessionId))

// The real agent runs only when the process has credentials; without them the session
// still works as a human-only collaborative session.
let private runAgent =
    if Interop.envOr "ANTHROPIC_API_KEY" (Interop.envOr "CLAUDE_CODE_OAUTH_TOKEN" "") <> "" then Some Agent.run else None

[<Fable.Core.Emit("(process.stdin.on('close', $0), process.stdin.on('end', $0), process.stdin.resume())")>]
let private onStdinClosed (handler: unit -> unit) : unit = Fable.Core.Util.jsNative

Async.StartImmediate (
    async {
        let log =
            EventStore.openLog (sprintf "%s/events.jsonl" dataDir) sessionId (fun () -> System.DateTimeOffset.UtcNow)
        let docStore = DocStore.openStore (sprintf "%s/doc.jsonl" dataDir)
        let! host = Host.startFull runAgent None (Some log) (Some docStore) sessionId token port
        // Sessions never outlive their Manager: spawned under the guard, the Manager's
        // death closes our stdin (the kernel does this even on SIGKILL) and we exit.
        if Interop.envOr "YESSION_PARENT_GUARD" "" = "1" then
            onStdinClosed (fun () -> Interop.exit 0)
        // The one readiness line of the spawn contract — last, so the Manager can
        // treat everything before it as logs and everything after as a live session.
        printfn """{"yession":"ready","port":%d}""" host.Port
    })
