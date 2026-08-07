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

// --- The sandbox seam's deterministic test double ----------------------------------------

/// Counts sandbox lifecycle calls so tests can assert an operation happened (or was
/// prevented) at the seam.
type SandboxRecorder () =
    member val Created = 0 with get, set
    member val Disposed = 0 with get, set
    member val Spawned = 0 with get, set

/// A policy with nothing in it — for scripted sandboxes that ignore it.
let emptyPolicy : SandboxPolicy =
    { ReadPaths = []
      WritePaths = []
      AllowedDomains = None
      Env = Map.empty
      WorkingDirectory = None }

let preparedEmptyPolicy : unit -> Async<Result<SandboxPolicy, string>> =
    fun () -> async { return Ok emptyPolicy }

/// A deterministic in-memory sandbox: creations/disposals are counted, spawns are
/// delegated to an injected script. The seam analogue of the old InMemoryBackend.
let scriptedSandbox
    (recorder: SandboxRecorder)
    (script: SandboxExec -> (OutputStream * string -> unit) -> Async<SandboxRun>)
    : CreateSandbox =
    fun _policy ->
        async {
            recorder.Created <- recorder.Created + 1
            let ref = sprintf "scripted-%d" recorder.Created
            return
                Ok
                    { Ref = ref
                      Spawn =
                        fun exec onChunk ->
                            async {
                                recorder.Spawned <- recorder.Spawned + 1
                                return
                                    Ok
                                        { WriteStdin = ignore
                                          CloseStdin = ignore
                                          Kill = ignore
                                          Exited = script exec onChunk }
                            }
                      SpawnPty = None
                      Dispose = fun () -> async { recorder.Disposed <- recorder.Disposed + 1 } }
        }

/// The standard scripted spawn: one stdout chunk, exit 0.
let echoSandboxScript : SandboxExec -> (OutputStream * string -> unit) -> Async<SandboxRun> =
    fun _ onChunk ->
        async {
            onChunk (Stdout, "ok")
            return SandboxExited 0
        }

/// Run one command in a sandbox and wait for its end, accumulating stdout/stderr —
/// the execute path without an event log, for backend-level tests.
let runInSandbox
    (sandbox: Sandbox)
    (executable: string)
    (args: string list)
    (env: Map<string, string>)
    (workingDirectory: string option)
    : Async<SandboxRun * string * string> =
    async {
        let out = System.Text.StringBuilder ()
        let err = System.Text.StringBuilder ()
        let! spawned =
            sandbox.Spawn
                { Executable = executable
                  Arguments = args
                  Env = env
                  WorkingDirectory = workingDirectory }
                (fun (stream, text) ->
                    (match stream with
                     | Stdout -> out
                     | Stderr -> err)
                        .Append text
                    |> ignore)
        match spawned with
        | Error reason -> return SandboxRunFailed reason, out.ToString (), err.ToString ()
        | Ok handle ->
            let! run = handle.Exited
            return run, out.ToString (), err.ToString ()
    }

/// Run an Elmish program headlessly, exposing the latest model, dispatch, and an
/// event-driven `WaitFor` that resolves the first time the model satisfies a predicate.
module Harness =

    [<Fable.Core.Emit("queueMicrotask($0)")>]
    let private defer (f: unit -> unit) : unit = Fable.Core.Util.jsNative

    [<Fable.Core.Emit("setTimeout($0, $1)")>]
    let private setTimer (f: unit -> unit) (ms: int) : float = Fable.Core.Util.jsNative

    [<Fable.Core.Emit("clearTimeout($0)")>]
    let private clearTimer (handle: float) : unit = Fable.Core.Util.jsNative

    /// How long a single `WaitFor` may wait before it is a FAILURE rather than a wait.
    ///
    /// A condition that never arrives used to hang for ever, and the run's own budget
    /// (`tasks.fsx`, 240s for the whole Node suite) was the only thing that stopped it — so
    /// one stuck predicate killed every suite after it and reported "tests timed out" with no
    /// name attached. Finding which test it was meant reading a process table. A deadline here
    /// costs nothing when things work and turns that into one named failing test.
    ///
    /// 30s is deliberately far above anything real: the slowest whole test in a healthy
    /// `check Ports Native Srt` run — a packaged manager launching real child processes over
    /// real WebRTC, with several waits inside it — is under 9s, and a single wait is
    /// milliseconds. It is a hang detector, not a performance budget.
    let waitForTimeoutMs = 30000

    type Runner<'model, 'msg> =
        { Model : unit -> 'model
          Dispatch : 'msg -> unit
          /// Resolve the first time the model satisfies the predicate, or FAIL after
          /// `waitForTimeoutMs`. Never waits for ever.
          WaitFor : ('model -> bool) -> Async<unit> }

    /// The runner, with the wait deadline as a parameter — so the deadline itself can be
    /// tested (a 30s one cannot be, in a cheap tier measured in milliseconds) without any
    /// suite having to reach for a different mechanism. `run` is this at the real deadline.
    let runWith (timeoutMs: int) (program: Program<unit, 'model, 'msg, unit>) : Runner<'model, 'msg> =
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
                Async.FromContinuations (fun (cont, econt, _) ->
                    if predicate model then cont ()
                    else
                        // Settled exactly once, by whichever comes first — the model or the
                        // clock. `settled` is what makes that true: the timer cannot resume a
                        // continuation the model already resumed, and a model update cannot
                        // resume one the timer already failed.
                        let settled = ref false
                        let timer = ref 0.0
                        let resume () =
                            if not settled.Value then
                                settled.Value <- true
                                clearTimer timer.Value
                                cont ()
                        waiters <- (predicate, resume) :: waiters
                        timer.Value <-
                            setTimer
                                (fun () ->
                                    if not settled.Value then
                                        settled.Value <- true
                                        // Drop the waiter before failing: a predicate left in
                                        // the list would be re-evaluated on every later
                                        // setState, for a test that is already over.
                                        waiters <-
                                            waiters
                                            |> List.filter (fun (_, r) ->
                                                not (System.Object.ReferenceEquals (r, resume)))
                                        econt (
                                            exn (
                                                sprintf
                                                    "WaitFor timed out after %dms: the model never satisfied the predicate"
                                                    timeoutMs)))
                                timeoutMs) }

    let run (program: Program<unit, 'model, 'msg, unit>) : Runner<'model, 'msg> =
        runWith waitForTimeoutMs program

/// Render the client view to an HTML string for markup assertions — through the very
/// renderer the served bootstrap uses (`Ssr`), so tests exercise the shipped SSR path.
/// The view's `ViewActions` are no-ops (handlers fire on live browser events only).
let render (model: ClientModel) : string = Ssr.renderModel model

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

    /// Resolve a (possibly relative) URL against a base, exactly as a browser resolves a
    /// `Location` header against the request URI.
    [<Fable.Core.Emit("new URL($0, $1).href")>]
    let private resolveUrl (location: string) (baseUrl: string) : string = Fable.Core.Util.jsNative

    [<Fable.Core.Emit("""fetch($0, { redirect: 'manual', headers: { ...Object.fromEntries($2), cookie: $1 } }).then(async r => ({
      status: r.status,
      location: r.headers.get('location') || '',
      setCookies: r.headers.getSetCookie(),
      cacheControl: r.headers.get('cache-control') || '',
      body: await r.text() }))""")>]
    let private fetchManualWith (url: string) (cookie: string) (headers: (string * string) []) : Fable.Core.JS.Promise<ManualReply> = Fable.Core.Util.jsNative

    let private fetchManual (url: string) (cookie: string) : Fable.Core.JS.Promise<ManualReply> =
        fetchManualWith url cookie [||]

    let private store (jar: Jar) (setCookies: string []) =
        for header in setCookies do
            match header.Split ';' |> Array.tryHead with
            | Some pair ->
                match pair.IndexOf '=' with
                | i when i > 0 -> jar.Cookies <- Map.add (pair.Substring (0, i)) (pair.Substring (i + 1)) jar.Cookies
                | _ -> ()
            | None -> ()

    /// GET with the jar and extra request headers (what an authenticating proxy would
    /// assert on every hop — Plan 07), storing any cookies; no redirect following.
    let getWithJarAs (headers: (string * string) list) (jar: Jar) (url: string) : Async<{| Status: int; Location: string; CacheControl: string; Body: string |}> =
        async {
            let! reply = fetchManualWith url (cookieHeader jar) (Array.ofList headers) |> Async.AwaitPromise
            store jar reply.setCookies
            return {| Status = reply.status; Location = reply.location; CacheControl = reply.cacheControl; Body = reply.body |}
        }

    /// GET with the jar, storing any cookies; no redirect following.
    let getWithJar (jar: Jar) (url: string) : Async<{| Status: int; Location: string; CacheControl: string; Body: string |}> =
        getWithJarAs [] jar url

    /// Follow a redirect chain (capped) with the jar and extra headers on every hop,
    /// returning the final non-3xx reply.
    let followWithJarAs (headers: (string * string) list) (jar: Jar) (startUrl: string) : Async<{| Status: int; Location: string; CacheControl: string; Body: string |}> =
        // Resolve a `Location` against the request URI the way RFC 3986 (and every
        // browser) does. It used to graft the location onto `scheme://host:port`, which
        // only handles a location that is already absolute or root-anchored — so the
        // callback's `./`, and every redirect a session mounted under a path emits,
        // resolved to nonsense.
        let rec go (url: string) (hops: int) =
            async {
                let! reply = getWithJarAs headers jar url
                if reply.Status >= 300 && reply.Status < 400 && hops < 10 then
                    return! go (resolveUrl reply.Location url) (hops + 1)
                else
                    return reply
            }
        go startUrl 0

    /// Follow a redirect chain (capped) with the jar, returning the final non-3xx reply.
    let followWithJar (jar: Jar) (startUrl: string) : Async<{| Status: int; Location: string; CacheControl: string; Body: string |}> =
        followWithJarAs [] jar startUrl

    /// Log in to a session the way the browser client does — start at `loginPath`, ride
    /// the hops to the manager and back with `headers` on every hop (what an
    /// authenticating proxy would assert, Plan 07) — and return the jar plus a minted
    /// peer token from `/me`. Fails loudly on any non-success step.
    let openSessionVia (headers: (string * string) list) (loginPath: string) (sessionBaseUrl: string) : Async<{| Jar: Jar; PeerToken: string |}> =
        async {
            let jar = newJar ()
            let! landed = followWithJarAs headers jar (sessionBaseUrl + loginPath)
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

    /// `openSessionVia` with no extra headers from `/login` — the plain localhost bounce.
    let openSession (sessionBaseUrl: string) : Async<{| Jar: Jar; PeerToken: string |}> =
        openSessionVia [] "/login" sessionBaseUrl

/// Poll a predicate until it holds, failing loudly with `label` if it never does. For the few
/// signals that are not model changes (an SSE frame arriving, a hub publishing) — a model waiter
/// is `Runner.WaitFor` and needs no polling.
let waitUntil (label: string) (condition: unit -> bool) : Async<unit> =
    let rec go (remaining: int) =
        async {
            if condition () then return ()
            elif remaining <= 0 then return failwithf "timed out waiting for %s" label
            else
                do! Async.Sleep 50
                return! go (remaining - 1)
        }
    go 100

/// One full connected client against a host. `Registry` is the client's `BodyRegistry` (over
/// its doc), so the body seam below binds the same top-level fragment roots the app does.
type Client =
    { Runner : Harness.Runner<ClientModel, Ylmish.Program.Message<ClientMsg>>
      Connection : App.Connection
      Registry : BodyRegistry
      /// The plain-text roots the terminal composers live in (Plan 13), alongside the rich
      /// bodies. Held on the client for the same reason `Registry` is: a test drives the
      /// composer by writing the CRDT the browser's input writes.
      Texts : TextRegistry
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
        let texts = TextRegistry doc
        let runner = Harness.run (App.makeProgram doc (ClientModel.init local))
        // The composer's publication rule, wired exactly as the browser wires it: the client's
        // draft slot appears when its body has content and goes when the body empties.
        DraftSlot.follow doc registry local.PeerId (user >> runner.Dispatch) |> ignore
        let hello = { PeerId = local.PeerId; DisplayName = name; Token = token }
        // The model is what "how far have we consumed" means (see `ConnectOptions`).
        let options = { options with ReadPosition = Some (fun () -> (runner.Model ()).EventConsumer.LastProcessedOffset) }
        let connection = App.connect options doc registry texts hello (user >> runner.Dispatch) channel
        Async.StartImmediate connection.Run
        do! runner.WaitFor (fun m -> m.Connection = Connected)
        return { Runner = runner; Connection = connection; Registry = registry; Texts = texts; Channel = channel; Doc = doc; Hello = hello }
    }

/// `connectClientWith` under the default options (frame-based event reads).
let connectClient (signalUrl: string) (token: string) (id: string) (name: string) : Async<Client> =
    connectClientWith App.ConnectOptions.defaults signalUrl token id name

/// Connect one full client to a host over an IN-MEMORY channel pair — the same drivers as
/// the WebRTC path (`App.makeProgram` + `App.connect` on one end, the Host's real per-peer
/// pump on the other via `host.Connect`), but with no WebRTC, HTTP, or native addon, so it
/// runs in the cheap tier. The peer token is minted from the host — what `/me` would serve
/// an authorized browser. Resolves once the model reaches `Connected`.
///
/// The options are built FROM the client's dispatch, mirroring the browser's `dispatchRef`:
/// a transport composed against dispatch (the event feed's resilience policy reports its
/// interim health that way) is therefore wired before the first frame moves, with no window
/// in which reports are dropped.
let connectInMemoryClientVia
    (makeOptions: (ClientMsg -> unit) -> App.ConnectOptions)
    (host: Host.SessionHost)
    (id: string)
    (name: string)
    : Async<Client> =
    async {
        let clientEnd, serverEnd = Yession.SessionProcess.InMemoryChannel.createPair<string> ()
        // The Host drives the server end exactly as it would a WebRTC connection.
        host.Connect serverEnd
        let doc = Y.Doc.Create ()
        let local = peer id name
        let registry = BodyRegistry doc
        let texts = TextRegistry doc
        let runner = Harness.run (App.makeProgram doc (ClientModel.init local))
        // As the browser wires it (see `connectClientWith`).
        DraftSlot.follow doc registry local.PeerId (user >> runner.Dispatch) |> ignore
        let hello = { PeerId = local.PeerId; DisplayName = name; Token = host.MintPeerToken () }
        let dispatch = user >> runner.Dispatch
        let options =
            { makeOptions dispatch with
                ReadPosition = Some (fun () -> (runner.Model ()).EventConsumer.LastProcessedOffset) }
        let connection = App.connect options doc registry texts hello dispatch clientEnd
        Async.StartImmediate connection.Run
        do! runner.WaitFor (fun m -> m.Connection = Connected)
        return { Runner = runner; Connection = connection; Registry = registry; Texts = texts; Channel = clientEnd; Doc = doc; Hello = hello }
    }

/// `connectInMemoryClientVia` with options that do not depend on dispatch.
let connectInMemoryClientWith (options: App.ConnectOptions) : Host.SessionHost -> string -> string -> Async<Client> =
    connectInMemoryClientVia (fun _ -> options)

/// `connectInMemoryClientWith` under the default options (events over frames).
let connectInMemoryClient : Host.SessionHost -> string -> string -> Async<Client> =
    connectInMemoryClientWith App.ConnectOptions.defaults

/// Reconnect an existing client on a fresh channel, resuming event consumption from its
/// model's processed offset (E2E-4's catch-up path). Small pages force multi-page reads.
let reconnectClient (signalUrl: string) (client: Client) : Async<Client> =
    async {
        let! channel = WebRtc.connect signalUrl
        let options =
            { App.ConnectOptions.defaults with
                ResumeAfter = (client.Runner.Model ()).EventConsumer.LastProcessedOffset
                PageSize = 2
                ReadPosition = Some (fun () -> (client.Runner.Model ()).EventConsumer.LastProcessedOffset) }
        let connection = App.connect options client.Doc client.Registry client.Texts client.Hello (user >> client.Runner.Dispatch) channel
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

    type Runner = Harness.Runner<ClientModel, Ylmish.Program.Message<ClientMsg>>

    /// Author a peer's draft body on a bare runner under an EXPLICIT queue key: publish the slot
    /// carrying that key, then write the markdown into its top-level body fragment. A bare runner
    /// has no `DraftSlot.follow` on its doc (that is client composition, `connectClientWith`), so
    /// the slot is dispatched here — the same slot the rule would publish, with the key named so a
    /// test can assert the queue entry it becomes.
    let authorAs (queueId: QueueId) (registry: BodyRegistry) (runner: Runner) (peer: PeerId) (markdown: string) : unit =
        runner.Dispatch (user (EnsureDraftMsg (peer, queueId)))
        Markdown.intoFragment markdown (registry.Fragment (BodyKey.draft peer))

    /// `authorAs` under a minted key — for tests that never name the queue entry.
    let author (registry: BodyRegistry) (runner: Runner) (peer: PeerId) (markdown: string) : unit =
        authorAs (QueueId.create (string (System.Guid.NewGuid ())) |> expect) registry runner peer markdown

    /// The queue key a peer's published draft carries, as any co-editor's send would read it.
    let queueKeyOf (runner: Runner) (peer: PeerId) : QueueId option =
        (runner.Model ()).Synced.Drafts |> Map.tryFind peer |> Option.map (fun draft -> draft.QueueId)

    /// Write a peer's draft body and NOTHING else — what typing into the composer does. The slot
    /// is whatever the publication rule makes of the content (`DraftSlot.follow`), so this is how
    /// a test drives that rule; `author` is the bare-runner shortcut that dispatches the slot too.
    /// The empty string empties the composer.
    let write (registry: BodyRegistry) (peer: PeerId) (markdown: string) : unit =
        Markdown.intoFragment markdown (registry.Fragment (BodyKey.draft peer))

    /// Read a peer's draft body as markdown (the empty string before any content exists).
    let draft (registry: BodyRegistry) (peer: PeerId) : string option =
        Some (Markdown.ofFragment (registry.Fragment (BodyKey.draft peer)))

    /// The bare-runner analogue of `Connection.SendDraft`: capture the draft body, seed the queue
    /// fragment under the key the SLOT carries (the draft->queue content copy that shared Y types
    /// cannot do by re-parenting), then dispatch the enqueue. Returns the key it went in under, so
    /// a caller can assert the entry. A no-op returning `None` when nothing is published.
    let send (registry: BodyRegistry) (runner: Runner) (peer: PeerId) : QueueId option =
        match queueKeyOf runner peer with
        | None -> None
        | Some queueId ->
            let md = draft registry peer |> Option.defaultValue ""
            // Seed the queue body BEFORE the entry (mirrors `Connection.SendDraft`): over an
            // ordered transport the body update reaches a draining Session Process before the
            // entry, so the drain never snapshots an entry whose body has not yet landed.
            if md <> "" then Markdown.intoFragment md (registry.Fragment (BodyKey.queued queueId))
            runner.Dispatch (user (SendDraftMsg peer))
            // The composer empties after send (the body root is durable, not removed with the slot).
            Markdown.intoFragment "" (registry.Fragment (BodyKey.draft peer))
            Some queueId

    /// One queue entry's markdown, read straight from the doc (exactly the drain's read).
    let queued (doc: Y.Doc) (queueId: QueueId) : string =
        SyncedStateSync.queuedBodyMarkdown doc queueId

/// Author a draft body on a full Client: write the markdown into the peer's body fragment and
/// wait for the slot. No slot is dispatched — writing the body publishes it (`DraftSlot.follow`,
/// wired by the connectors above as the browser wires it), which is what typing does. The write
/// flows through the fragment CRDT and syncs like any edit. Replaces the old `editBody`/`setDraft`.
/// Co-editing another peer's slot goes through here too: their slot already exists (they typed).
let compose (client: Client) (peer: PeerId) (markdown: string) : Async<unit> =
    async {
        Body.write client.Registry peer markdown
        do! client.Runner.WaitFor (fun m -> Map.containsKey peer m.Synced.Drafts)
    }

/// Empty a peer's composer on a full Client (select-all-delete, or the ✕), and wait for the slot
/// to go: publication follows the body, so an empty composer has no draft slot.
let clearComposer (client: Client) (peer: PeerId) : Async<unit> =
    async {
        Body.write client.Registry peer ""
        do! client.Runner.WaitFor (fun m -> not (Map.containsKey peer m.Synced.Drafts))
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
