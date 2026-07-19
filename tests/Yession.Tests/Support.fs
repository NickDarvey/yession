module Yession.Tests.Support

// Shared test infrastructure: a headless Elmish runner with event-driven model waiters,
// and a full-client connector (WebRTC channel + Yjs doc + withYlmish program + drivers)
// parameterized by host, so every E2E suite composes the same way. No sleeps, no polling.

open Elmish
open Fable.Core
open Fable.Pyxpecto
open Yjs
open Yession.Domain
open Yession.App
open Yession.Host

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

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
/// otherwise -> a reported skip. These cases also sit under `Tag.needs [Docker]`, so the cheap
/// tier never reaches them.
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

/// Drive the OIDC authorization flow over plain HTTP, the way a browser would: a cookie
/// jar plus MANUAL redirect following. Manual matters twice — an auto-following fetch
/// drops intermediate `Set-Cookie` headers, and the hops cross ports (session → manager
/// → session), which must share this jar, not the runtime's.
module OidcHttp =

    type Jar = { mutable Cookies : Map<string, string> }

    let newJar () : Jar = { Cookies = Map.empty }

    let private cookieHeader (jar: Jar) : string =
        jar.Cookies |> Map.toList |> List.map (fun (k, v) -> sprintf "%s=%s" k v) |> String.concat "; "

    type private ManualReply =
        abstract status : int
        abstract location : string
        abstract setCookies : string []
        abstract cacheControl : string
        abstract body : string

    [<Fable.Core.Emit("""fetch($0, { redirect: 'manual', headers: { cookie: $1 } }).then(async r => ({
      status: r.status,
      location: r.headers.get('location') || '',
      setCookies: r.headers.getSetCookie(),
      cacheControl: r.headers.get('cache-control') || '',
      body: await r.text() }))""")>]
    let private fetchManual (url: string) (cookie: string) : Fable.Core.JS.Promise<ManualReply> = Fable.Core.Util.jsNative

    let private store (jar: Jar) (setCookies: string []) =
        for header in setCookies do
            match header.Split ';' |> Array.tryHead with
            | Some pair ->
                match pair.IndexOf '=' with
                | i when i > 0 -> jar.Cookies <- Map.add (pair.Substring (0, i)) (pair.Substring (i + 1)) jar.Cookies
                | _ -> ()
            | None -> ()

    /// GET with the jar, storing any cookies; no redirect following.
    let getWithJar (jar: Jar) (url: string) : Async<{| Status: int; Location: string; CacheControl: string; Body: string |}> =
        async {
            let! reply = fetchManual url (cookieHeader jar) |> Async.AwaitPromise
            store jar reply.setCookies
            return {| Status = reply.status; Location = reply.location; CacheControl = reply.cacheControl; Body = reply.body |}
        }

    /// Follow a redirect chain (capped) with the jar, returning the final non-3xx reply.
    let followWithJar (jar: Jar) (startUrl: string) : Async<{| Status: int; Location: string; CacheControl: string; Body: string |}> =
        let resolve (baseUrl: string) (location: string) : string =
            if location.StartsWith "http" then location
            else
                // Relative redirect (the callback's `/`): resolve against the base.
                let origin = System.Uri baseUrl
                sprintf "%s://%s:%d%s" origin.Scheme origin.Host origin.Port location
        let rec go (url: string) (hops: int) =
            async {
                let! reply = getWithJar jar url
                if reply.Status >= 300 && reply.Status < 400 && hops < 10 then
                    return! go (resolve url reply.Location) (hops + 1)
                else
                    return reply
            }
        go startUrl 0

    /// Log in to a session the way the browser client does — start at `/login`, ride the
    /// hops to the manager and back — and return the jar plus a minted peer token from
    /// `/me`. Fails loudly on any non-success step.
    let openSession (sessionBaseUrl: string) : Async<{| Jar: Jar; PeerToken: string |}> =
        async {
            let jar = newJar ()
            let! landed = followWithJar jar (sessionBaseUrl + "/login")
            if landed.Status <> 200 then
                failwithf "OIDC login chain ended with %d: %s" landed.Status landed.Body
            let! me = getWithJar jar (sessionBaseUrl + "/me")
            if me.Status <> 200 then failwithf "/me after login answered %d: %s" me.Status me.Body
            let token =
                match Decode.fromString (Decode.field "peerToken" Decode.string) me.Body with
                | Ok token -> token
                | Error e -> failwithf "malformed /me body: %s" e
            return {| Jar = jar; PeerToken = token |}
        }

/// One full connected client against a host. `Registry` is the client's `BodyRegistry` (over
/// its doc), so the body seam below binds the same top-level fragment roots the app does.
type Client =
    { Runner : Harness.Runner<ClientModel, Ylmish.Program.Message<ClientModel, ClientMsg>>
      Connection : App.Connection
      Registry : BodyRegistry
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
        let registry = BodyRegistry doc
        let runner = Harness.run (App.makeProgram doc (ClientModel.init local))
        let hello = { PeerId = local.PeerId; DisplayName = name; Token = token }
        let connection = App.connect options doc registry hello (user >> runner.Dispatch) channel
        Async.StartImmediate connection.Run
        do! runner.WaitFor (fun m -> m.Connection = Connected)
        return { Runner = runner; Connection = connection; Registry = registry; Channel = channel; Doc = doc; Hello = hello }
    }

/// `connectClientWith` under the default options (frame-based event reads).
let connectClient (signalUrl: string) (token: string) (id: string) (name: string) : Async<Client> =
    connectClientWith App.ConnectOptions.defaults signalUrl token id name

/// Connect one full client to a host over an IN-MEMORY channel pair — the same drivers as
/// the WebRTC path (`App.makeProgram` + `App.connect` on one end, the Host's real per-peer
/// pump on the other via `host.Connect`), but with no WebRTC, HTTP, or native addon, so it
/// runs in the cheap tier. Events come over frames (`FetchEvents = None`). The peer token
/// is minted from the host — what `/me` would serve an authorized browser. Resolves once
/// the model reaches `Connected`.
let connectInMemoryClient (host: Host.SessionHost) (id: string) (name: string) : Async<Client> =
    async {
        let clientEnd, serverEnd = Yession.SessionProcess.InMemoryChannel.createPair<string> ()
        // The Host drives the server end exactly as it would a WebRTC connection.
        host.Connect serverEnd
        let doc = Y.Doc.Create ()
        let local = peer id name
        let registry = BodyRegistry doc
        let runner = Harness.run (App.makeProgram doc (ClientModel.init local))
        let hello = { PeerId = local.PeerId; DisplayName = name; Token = host.MintPeerToken () }
        let connection = App.connect App.ConnectOptions.defaults doc registry hello (user >> runner.Dispatch) clientEnd
        Async.StartImmediate connection.Run
        do! runner.WaitFor (fun m -> m.Connection = Connected)
        return { Runner = runner; Connection = connection; Registry = registry; Channel = clientEnd; Doc = doc; Hello = hello }
    }

/// Reconnect an existing client on a fresh channel, resuming event consumption from its
/// model's processed offset (E2E-4's catch-up path). Small pages force multi-page reads.
let reconnectClient (signalUrl: string) (client: Client) : Async<Client> =
    async {
        let! channel = WebRtc.connect signalUrl
        let options =
            { App.ConnectOptions.defaults with
                ResumeAfter = (client.Runner.Model ()).EventConsumer.LastProcessedOffset
                PageSize = 2 }
        let connection = App.connect options client.Doc client.Registry client.Hello (user >> client.Runner.Dispatch) channel
        Async.StartImmediate connection.Run
        do! client.Runner.WaitFor (fun m -> m.Connection = Connected)
        return { client with Connection = connection; Channel = channel }
    }

/// The body-agnostic seam. These helpers are the ONLY test code that touches a body
/// fragment; every suite drives drafts/queues through them, so no test outside the seam
/// knows the body is a `Y.XmlFragment`. Bodies are markdown strings at this boundary.
///
/// Body fragments are top-level doc roots (`BodyKey`), created idempotently by
/// `BodyRegistry.Fragment` (`doc.getXmlFragment`), so they are always available — no waiting to
/// anchor. Reading a peer's fragment before its content has synced yields the empty string
/// until the owner's update arrives (an empty root merges with the incoming one by name).
module Body =

    type Runner = Harness.Runner<ClientModel, Ylmish.Program.Message<ClientModel, ClientMsg>>

    /// Author a peer's draft body on a bare runner: ensure the slot, then write the markdown
    /// into its top-level body fragment.
    let author (registry: BodyRegistry) (runner: Runner) (peer: PeerId) (markdown: string) : unit =
        runner.Dispatch (user (EnsureDraftMsg peer))
        Markdown.intoFragment markdown (registry.Fragment (BodyKey.draft peer))

    /// Read a peer's draft body as markdown (the empty string before any content exists).
    let draft (registry: BodyRegistry) (peer: PeerId) : string option =
        Some (Markdown.ofFragment (registry.Fragment (BodyKey.draft peer)))

    /// The bare-runner analogue of `Connection.SendDraft`: capture the draft body, dispatch
    /// the enqueue, and seed the new queue fragment (the draft->queue content copy that shared
    /// Y types cannot do by re-parenting).
    let send (registry: BodyRegistry) (runner: Runner) (peer: PeerId) (queueId: QueueId) : unit =
        let md = draft registry peer |> Option.defaultValue ""
        // Seed the queue body BEFORE the entry (mirrors `Connection.SendDraft`): over an ordered
        // transport the body update reaches a draining Session Process before the entry, so the
        // drain never snapshots an entry whose body has not yet landed.
        if md <> "" then Markdown.intoFragment md (registry.Fragment (BodyKey.queued queueId))
        runner.Dispatch (user (SendDraftMsg (peer, queueId)))
        // The composer empties after send (the body root is durable, not removed with the slot).
        Markdown.intoFragment "" (registry.Fragment (BodyKey.draft peer))

    /// One queue entry's markdown, read straight from the doc (exactly the drain's read).
    let queued (doc: Y.Doc) (queueId: QueueId) : string =
        SyncedStateSync.queuedBodyMarkdown doc queueId

/// Author a draft body on a full Client: ensure the peer's slot, then write the markdown into
/// its body fragment. The write flows through the fragment CRDT and syncs like any edit.
/// Replaces the old `editBody`/`setDraft`.
let compose (client: Client) (peer: PeerId) (markdown: string) : Async<unit> =
    async {
        client.Runner.Dispatch (user (EnsureDraftMsg peer))
        do! client.Runner.WaitFor (fun m -> Map.containsKey peer m.Synced.Drafts)
        Markdown.intoFragment markdown (client.Registry.Fragment (BodyKey.draft peer))
    }

/// Read a peer's draft body as markdown (the empty string until content has synced). Replaces
/// the old `bodyOf`.
let draftBody (client: Client) (peer: PeerId) : string option =
    Body.draft client.Registry peer

/// Read one queued entry's body as markdown, straight from the doc (the same read the
/// drain uses). Replaces the old `queueBodyOf`.
let queueBody (client: Client) (queueId: QueueId) : string =
    Body.queued client.Doc queueId

/// Every queued entry as `(queueId, markdown)`, in consumption order. Replaces `queueView`.
let queueBodies (client: Client) : (string * string) list =
    (client.Runner.Model ()).Synced.Queue
    |> QueueOrder.sorted
    |> List.map (fun entry -> QueueId.value entry.QueueId, queueBody client entry.QueueId)
