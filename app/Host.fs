module Yession.Host.Host

// The Session Process host: owns the event log and accepts WebRTC peer connections,
// running the token-gated peer-session handshake for each. This is the composition root
// for the running process (design.md §1 "Composition at the top", §2.1).

open System
open Yession.Domain
open Yession.SessionProcess

type SessionHost =
    { SessionId : SessionId
      Token : string
      Port : int
      Log : EventLog<SessionEvent>
      /// Resolves when the next peer session ends. Register (call) it *before* triggering
      /// the disconnect you want to observe, then await it — this avoids any reliance on
      /// timing to see the resulting `PeerLeft`.
      WaitForNextSessionEnd : unit -> Async<unit>
      Stop : unit -> Async<unit> }

/// Start a Session Process: create the event log, start HTTP bootstrap + signalling, and
/// run a peer session for every connection. Resolves once the server is listening.
let start (sessionId: SessionId) (token: string) (port: int) : Async<SessionHost> =
    async {
        let log = InMemoryEventLog.create sessionId (fun () -> DateTimeOffset.UtcNow)

        let mutable endWaiters : (unit -> unit) list = []
        let signalSessionEnded () =
            let waiters = endWaiters
            endWaiters <- []
            waiters |> List.iter (fun w -> w ())

        let onConnection (channel: FrameChannel<string>) =
            Async.StartImmediate(
                async {
                    do! PeerSession.run sessionId token log channel
                    signalSessionEnded ()
                })

        let! server = Signalling.start onConnection port

        let waitForNextSessionEnd () : Async<unit> =
            // Register eagerly at call time so a session that ends before the await still
            // resolves the returned computation.
            let mutable ended = false
            let mutable waiter : (unit -> unit) option = None
            endWaiters <-
                (fun () ->
                    ended <- true
                    match waiter with
                    | Some w -> w ()
                    | None -> ())
                :: endWaiters
            async {
                return!
                    Async.FromContinuations(fun (cont, _, _) ->
                        if ended then cont () else waiter <- Some cont)
            }

        return
            { SessionId = sessionId
              Token = token
              Port = port
              Log = log
              WaitForNextSessionEnd = waitForNextSessionEnd
              Stop = fun () -> async { server.close ignore } }
    }
