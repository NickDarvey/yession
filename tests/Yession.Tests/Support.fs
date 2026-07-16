module Yession.Tests.Support

// Shared test infrastructure: a headless Elmish runner with event-driven model waiters,
// and a full-client connector (WebRTC channel + Yjs doc + withYlmish program + drivers)
// parameterized by host, so every E2E suite composes the same way. No sleeps, no polling.

open Elmish
open Fable.Pyxpecto
open Yjs
open Yession.Domain
open Yession.App
open Yession.Host

let expect =
    function
    | Ok v -> v
    | Error e -> failwith e

let user msg = Ylmish.Program.Message.User msg

/// Run an Elmish program headlessly, exposing the latest model, dispatch, and an
/// event-driven `WaitFor` that resolves the first time the model satisfies a predicate.
module Harness =

    [<Fable.Core.Emit("queueMicrotask($0)")>]
    let private defer (f: unit -> unit) : unit = Fable.Core.Util.jsNative

    type Runner<'model, 'msg> =
        { Model : unit -> 'model
          Dispatch : 'msg -> unit
          WaitFor : ('model -> bool) -> Async<unit> }

    let run (program: Program<unit, 'model, 'msg, unit>) : Runner<'model, 'msg> =
        let mutable model = Unchecked.defaultof<'model>
        let mutable dispatch : 'msg -> unit = ignore
        let mutable waiters : (('model -> bool) * (unit -> unit)) list = []
        let setState m d =
            model <- m
            dispatch <- d
            let fire, keep = waiters |> List.partition (fun (predicate, _) -> predicate m)
            waiters <- keep
            // Resume on a microtask: setState runs INSIDE the Elmish dispatch loop, and
            // a continuation that dispatches synchronously from here would only enqueue
            // (ring buffer) — its Model() reads would then see stale state. Deferring
            // lets the loop drain first, so awaited WaitFor + Dispatch compose safely.
            fire |> List.iter (fun (_, resume) -> defer resume)
        Program.withSetState setState program |> Program.run
        { Model = fun () -> model
          Dispatch = fun msg -> dispatch msg
          WaitFor =
            fun predicate ->
                Async.FromContinuations (fun (cont, _, _) ->
                    if predicate model then cont ()
                    else waiters <- (predicate, fun () -> cont ()) :: waiters) }

/// Render the client view to an HTML string for markup assertions — through the very
/// renderer the served bootstrap uses (`Ssr`), so tests exercise the shipped SSR path.
/// The view's `ViewActions` are no-ops (handlers fire on live browser events only).
let render (model: ClientModel) : string = Ssr.renderModel model

/// The Docker daemon gate for the integration suite. A real daemon exists on the CI
/// `verify` runner but not in the dev container, so the gate makes availability explicit
/// instead of the old silent no-op: present -> run the body; absent + `YESSION_REQUIRE_DOCKER`
/// (set on the release gate) -> FAIL, so a daemon-less gate can never pass green; absent
/// otherwise -> a reported skip. These cases also sit under `Tag.verify`, so the cheap tier
/// never reaches them.
module Docker =

    [<Fable.Core.Emit("process.env[$0] || ''")>]
    let private env (name: string) : string = Fable.Core.Util.jsNative

    let private required = env "YESSION_REQUIRE_DOCKER" <> ""

    let gate (name: string) (body: unit -> Async<unit>) =
        testCaseAsync name <| async {
            match! Backends.DockerBackend.daemonAvailable () with
            | true -> do! body ()
            | false when required ->
                failwith "verify gate requires a Docker daemon; none reachable (YESSION_REQUIRE_DOCKER is set)"
            | false ->
                printfn "skipped '%s': no Docker daemon (set YESSION_REQUIRE_DOCKER to require it)" name
        }

let peer (id: string) (name: string) : PeerState =
    { PeerId = PeerId.create id |> expect; DisplayName = name }

let bodyOf (draftId: DraftId) (m: ClientModel) : string option =
    m.Synced.Drafts |> Map.tryFind draftId |> Option.map (fun d -> Ylmish.Text.toString d.Body)

let editBody (draftId: DraftId) (edit: Ylmish.Text -> Ylmish.Text) (m: ClientModel) : ClientMsg =
    match Map.tryFind draftId m.Synced.Drafts with
    | Some draft -> EditDraftBodyMsg (draftId, edit draft.Body)
    | None ->
        failwithf
            "editBody: draft %s not in the model (drafts: %A)"
            (DraftId.value draftId)
            (m.Synced.Drafts |> Map.toList |> List.map (fst >> DraftId.value))

let queueBodyOf (queueId: QueueId) (m: ClientModel) : string option =
    m.Synced.Queue |> Map.tryFind queueId |> Option.map (fun e -> Ylmish.Text.toString e.Body)

let editQueued (queueId: QueueId) (edit: Ylmish.Text -> Ylmish.Text) (m: ClientModel) : ClientMsg =
    match Map.tryFind queueId m.Synced.Queue with
    | Some entry -> EditQueuedBodyMsg (queueId, edit entry.Body)
    | None ->
        failwithf
            "editQueued: entry %s not in the model (queue: %A)"
            (QueueId.value queueId)
            (m.Synced.Queue |> Map.toList |> List.map (fst >> QueueId.value))

/// The queue as (id, body) in consumption order — the shape most assertions want.
let queueView (m: ClientModel) : (string * string) list =
    QueueOrder.sorted m.Synced.Queue
    |> List.map (fun e -> QueueId.value e.QueueId, Ylmish.Text.toString e.Body)

/// One full connected client against a host.
type Client =
    { Runner : Harness.Runner<ClientModel, Ylmish.Program.Message<ClientModel, ClientMsg>>
      Connection : App.Connection
      Channel : FrameChannel<string>
      Doc : Y.Doc
      Hello : PeerHelloPayload }

/// Connect one full client with explicit options: WebRTC channel, its own Yjs doc, the
/// withYlmish program, and the connection driver. Resolves once the model reaches
/// `Connected`.
let connectClientWith (options: App.ConnectOptions) (signalUrl: string) (token: string) (id: string) (name: string) : Async<Client> =
    async {
        let! channel = WebRtc.connect signalUrl
        let doc = Y.Doc.Create ()
        let local = peer id name
        let runner = Harness.run (App.makeProgram doc (ClientModel.init local))
        let hello = { PeerId = local.PeerId; DisplayName = name; Token = token }
        let connection = App.connect options doc hello (user >> runner.Dispatch) channel
        Async.StartImmediate connection.Run
        do! runner.WaitFor (fun m -> m.Connection = Connected)
        return { Runner = runner; Connection = connection; Channel = channel; Doc = doc; Hello = hello }
    }

/// `connectClientWith` under the default options (frame-based event reads).
let connectClient (signalUrl: string) (token: string) (id: string) (name: string) : Async<Client> =
    connectClientWith App.ConnectOptions.defaults signalUrl token id name

/// Reconnect an existing client on a fresh channel, resuming event consumption from its
/// model's processed offset (E2E-4's catch-up path). Small pages force multi-page reads.
let reconnectClient (signalUrl: string) (client: Client) : Async<Client> =
    async {
        let! channel = WebRtc.connect signalUrl
        let options =
            { App.ConnectOptions.defaults with
                ResumeAfter = (client.Runner.Model ()).EventConsumer.LastProcessedOffset
                PageSize = 2 }
        let connection = App.connect options client.Doc client.Hello (user >> client.Runner.Dispatch) channel
        Async.StartImmediate connection.Run
        do! client.Runner.WaitFor (fun m -> m.Connection = Connected)
        return { client with Connection = connection; Channel = channel }
    }
