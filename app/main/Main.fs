module Yession.Host.Main

// The Manager entry point (`mise run start`; the `yession` binary). The Manager is a
// process supervisor + management surface: sessions run as CHILD OS PROCESSES
// (`yession-session`; in development, node over the Fable output), so a crashing
// session never takes the Manager down. Configuration comes from the environment so
// the repo interface stays declarative.
//
// For product continuity a default session is ensured and launched at boot; creating,
// launching, resuming, and stopping further sessions arrives with the management UI
// (Step 25).

open Yession.Domain
open Yession.Host

let private expect =
    function
    | Ok v -> v
    | Error e -> failwith e

// Default 0 = a random OS-assigned port for the default session, so multiple
// instances coexist.
let private port = Interop.envOr "YESSION_PORT" "0" |> int
let private token = Interop.envOr "YESSION_TOKEN" "local-dev-token"
let private sessionKey = Interop.envOr "YESSION_SESSION" "local-session"
let private dataDir = Interop.envOr "YESSION_DATA_DIR" ".yession"

[<Fable.Core.Emit("process.execPath")>]
let private nodePath : string = Fable.Core.Util.jsNative

// The session process command: the sibling binary in the product (Step 26); in
// development, this Node running the Fable-compiled session entry.
let private sessionCommand, sessionArgs =
    match Interop.envOr "YESSION_SESSION_BIN" "" with
    | "" -> nodePath, [ Interop.envOr "YESSION_SESSION_MAIN" "app/SessionMain.js" ]
    | binary -> binary, []

Async.StartImmediate(
    async {
        let manager =
            ProcessManager.create
                { ProcessManager.Options.defaults dataDir sessionCommand sessionArgs with
                    SessionPort = (if port = 0 then None else Some port) }

        // Ensure the default session exists (an existing registration is resume).
        let sessionId = SessionId.create sessionKey |> expect
        match manager.TryFind sessionId with
        | Some _ -> ()
        | None -> manager.CreateSession sessionKey sessionKey token |> expect |> ignore

        match! manager.Launch sessionId with
        | Error reason -> failwithf "default session failed to launch: %s" reason
        | Ok sessionPort ->
            printfn
                "Yession Manager: session %s launched at http://127.0.0.1:%d/  (child process, data=%s)"
                sessionKey
                sessionPort
                dataDir
    })
