module Yession.Host.Interop

// Minimal Fable bindings for the Node APIs the Session Process host needs:
// `node-datachannel` (WebRTC) and `node:http` (bootstrap + signalling). Only the surface
// actually used is bound; everything is event-callback based, matching libdatachannel.

open Fable.Core
open Yession.Domain.Link
open Fable.Core.JsInterop

// --- Awaiting a promise ------------------------------------------------------
//
// Fable's async trampoline hijacks a workflow onto a `setTimeout` every 2000 steps, and
// `Async.AwaitPromise` attaches its rejection handler only once the workflow reaches the
// await. A promise that rejects inside that window has no handler when Node checks at the
// end of the turn, so Node kills the process — and a `try/with` around the await cannot
// help, because its handler is not attached yet. That is how a routine "no such
// container" 404, caught and ignored on every other run, killed the whole suite (the
// unhandled rejection of verify run 30725449198).
//
// `awaitPromise` settles the promise in JS, at creation, so both outcomes are a value the
// workflow may take as long as it likes to read. Every await in the Node host goes
// through it: one answer to the hazard rather than one per call site. (The browser keeps
// `Async.AwaitPromise` — an unhandled rejection there is a console warning, not a dead
// process, and this module is Node-only.)

[<Emit("$0.then(value => [value, null], error => [null, error ?? new Error('promise rejected')])")>]
let private settled (promise: JS.Promise<'a>) : JS.Promise<'a * exn> = jsNative

/// Await a promise; a rejection surfaces as an ordinary exception inside the workflow.
let awaitPromise (promise: JS.Promise<'a>) : Async<'a> =
    // Settle HERE, not inside the workflow below: this call runs in the same tick that
    // created the promise, which is the only tick in which attaching a handler is
    // guaranteed to beat Node's check. Deferring it into the `async` would reproduce the
    // very gap this exists to close.
    let outcome = settled promise
    async {
        // `outcome` never rejects, so this await is safe whenever the workflow reaches it.
        let! value, error = outcome |> Async.AwaitPromise
        if isNull (box error) then return value else return raise error
    }

// --- node-datachannel --------------------------------------------------------

type [<AllowNullLiteral>] LocalDescription =
    abstract ``type`` : string
    abstract sdp : string

type [<AllowNullLiteral>] DataChannel =
    abstract sendMessage : string -> bool
    abstract close : unit -> unit
    abstract isOpen : unit -> bool
    abstract getLabel : unit -> string
    abstract onOpen : (unit -> unit) -> unit
    abstract onClosed : (unit -> unit) -> unit
    abstract onError : (string -> unit) -> unit
    abstract onMessage : (string -> unit) -> unit

type [<AllowNullLiteral>] PeerConnection =
    abstract close : unit -> unit
    abstract setLocalDescription : unit -> unit
    abstract setRemoteDescription : string * string -> unit
    abstract localDescription : unit -> LocalDescription
    abstract createDataChannel : string -> DataChannel
    abstract state : unit -> string
    abstract gatheringState : unit -> string
    abstract onLocalDescription : (string -> string -> unit) -> unit
    abstract onStateChange : (string -> unit) -> unit
    abstract onGatheringStateChange : (string -> unit) -> unit
    abstract onDataChannel : (DataChannel -> unit) -> unit

// `node-datachannel` is a native addon. A static top-level `import` loads its `.node`
// binary at module-eval — which would force the CHEAP test tier (pure/model/protocol tests
// that never open a WebRTC connection) to build and ship that binary just to LOAD the test
// bundle. Resolve it lazily via `createRequire` instead: the addon loads only on the first
// real connection (the verify tier and production), so the cheap tier runs without it. This
// mirrors the dynamic-`import()` pattern already used for the agent SDK and Docker backend.
[<Import("createRequire", "node:module")>]
let private createRequire (url: string) : obj = jsNative

[<Emit("import.meta.url")>]
let private moduleUrl : string = jsNative

[<Emit("$0($1)")>]
let private callRequire (require: obj) (id: string) : obj = jsNative

let mutable private nodeDataChannel : obj = null
/// The lazily-required `node-datachannel` module (cached after first use).
let private ndc () : obj =
    if isNull nodeDataChannel then
        nodeDataChannel <- callRequire (createRequire moduleUrl) "node-datachannel"
    nodeDataChannel

[<Emit("new ($0.PeerConnection)($1, $2)")>]
let private newPeerConnection (module': obj) (name: string) (config: obj) : PeerConnection = jsNative

[<Emit("$0.cleanup()")>]
let private ndcCleanup (module': obj) : unit = jsNative

/// libdatachannel's global teardown; lazy like the constructor (loads the addon on demand).
let cleanup () : unit = ndcCleanup (ndc ())

/// Create a peer connection. Empty `iceServers` means no STUN and no TURN: gathering stops at
/// host candidates. Those are gathered on EVERY interface, not just loopback — so a session on
/// an overlay network puts a routable address (a tailnet `100.x`, say) in its non-trickle SDP
/// and a remote browser connects to it directly. That is the whole of remote data-channel
/// access, and why it needs a network whose addresses route directly; narrowing this to
/// loopback would silently take remote sessions with it.
let createPeerConnection (name: string) : PeerConnection =
    let config = createObj [ "iceServers" ==> ([||]: obj[]) ]
    newPeerConnection (ndc ()) name config

// --- node:http ---------------------------------------------------------------

type [<AllowNullLiteral>] IncomingMessage =
    abstract url : string
    abstract ``method`` : string
    abstract on : string * (obj -> unit) -> IncomingMessage

type [<AllowNullLiteral>] ServerResponse =
    abstract writeHead : int * obj -> ServerResponse
    abstract write : string -> bool
    abstract ``end`` : string -> unit

type [<AllowNullLiteral>] HttpServer =
    abstract listen : int * string * (unit -> unit) -> HttpServer
    abstract close : (obj -> unit) -> unit

/// The actual bound port (differs from the requested one when listening on 0).
[<Emit("$0.address().port")>]
let serverPort (server: HttpServer) : int = jsNative

[<Import("createServer", "node:http")>]
let private createServerRaw : System.Func<IncomingMessage, ServerResponse, unit> -> HttpServer = jsNative

/// Create an HTTP server. The handler is passed as an uncurried delegate so Node receives
/// a plain `(req, res) => ...` two-argument callback.
let createServer (handler: IncomingMessage -> ServerResponse -> unit) : HttpServer =
    createServerRaw (System.Func<_, _, _>(handler))

/// Decode a Node Buffer (or string) chunk to a UTF-8 string.
[<Emit("(function (chunk) { return typeof chunk === 'string' ? chunk : chunk.toString('utf8') })($0)")>]
let bufferToString (chunk: obj) : string = jsNative

/// Read a request header (Node lowercases header names); None when absent.
[<Emit("($0.headers[$1] ?? null)")>]
let headerOf (req: IncomingMessage) (name: string) : string option = jsNative

/// A cryptographically random identifier (per-launch control secrets).
[<Emit("crypto.randomUUID()")>]
let randomSecret () : string = jsNative

/// Uniform `[0, 1)` — what a jittered retry schedule spreads its delays with. Not
/// cryptographic and not meant to be: the only thing it decides is which millisecond inside
/// a backoff window a retry lands on.
[<Emit("Math.random()")>]
let random () : float = jsNative

/// A repeating timer, for the beats a long-lived process keeps (the MCP poll, the activity
/// report). Returns the handle `clearInterval` wants.
[<Emit("setInterval($1, $0)")>]
let setInterval (ms: int) (callback: unit -> unit) : obj = jsNative

[<ImportAll("node:crypto")>]
let private nodeCrypto : obj = jsNative

/// SHA-256 of the UTF-8 input, base64url-encoded — the PKCE S256 operation the provider
/// applies to a `code_verifier` (RFC 7636 §4.2).
[<Emit("$0.createHash('sha256').update($1, 'utf8').digest('base64url')")>]
let private sha256B64u (cryptoModule: obj) (input: string) : string = jsNative

let sha256Base64Url (input: string) : string = sha256B64u nodeCrypto input

/// HMAC-SHA256 of the UTF-8 input under a secret, digested in `encoding` (`hex`,
/// `base64`, `base64url`). Beside the hash above because it is the same kind of thing and
/// the same imported module; the hook relay verifies signed deliveries with it, over the
/// bytes exactly as they arrived.
[<Emit("$0.createHmac('sha256', $1).update($2, 'utf8').digest($3)")>]
let private hmacSha256In (cryptoModule: obj) (secret: string) (input: string) (encoding: string) : string = jsNative

let hmacSha256 (secret: string) (input: string) (encoding: string) : string =
    hmacSha256In nodeCrypto secret input encoding

/// A short content address: enough of the SHA-256 that a different build is a different
/// string, which is what lets bytes be served under an immutable cache policy — and short
/// enough to read in a network panel. 72 bits; a collision needs two builds whose hashes agree
/// there, which no real edit produces.
///
/// `None` has nothing to address, and renders as the empty string.
///
/// Used for the shell's `ETag` — "are these the bytes you already have?", asked of a document.
/// The static files are addressed by `Assets`, which asks the same question of a whole
/// directory at once so that a stylesheet and the faces it names can never answer differently.
let contentDigest (content: string option) : string =
    match content with
    | Some text -> (sha256Base64Url text).Substring (0, 12)
    | None -> ""

/// Constant-time string equality (client secrets); length mismatch short-circuits,
/// which leaks only the length.
[<Emit("(function (cryptoModule, a, b) { const left = Buffer.from(a, 'utf8'), right = Buffer.from(b, 'utf8'); return left.length === right.length && cryptoModule.timingSafeEqual(left, right) })($0, $1, $2)")>]
let private timingSafeEq (cryptoModule: obj) (a: string) (b: string) : bool = jsNative

let timingSafeEqualStr (a: string) (b: string) : bool = timingSafeEq nodeCrypto a b

/// The TCP peer address of a request (`socket.remoteAddress`); None once disconnected.
[<Emit("($0.socket?.remoteAddress ?? null)")>]
let remoteAddressOf (req: IncomingMessage) : string option = jsNative

/// A query parameter of a request URL; None when absent.
[<Emit("new URL($0, 'http://local').searchParams.get($1)")>]
let queryParamOf (url: string) (name: string) : string option = jsNative

/// POST a JSON body and resolve with the response text. Uses Node 24's global `fetch`.
[<Emit("fetch($0, { method: 'POST', headers: { 'content-type': 'application/json' }, body: $1 }).then(r => r.text())")>]
let postText (url: string) (body: string) : JS.Promise<string> = jsNative

/// GET a URL and resolve with the response text.
[<Emit("fetch($0).then(r => r.text())")>]
let getText (url: string) : JS.Promise<string> = jsNative

/// Extract the `sdp` field from a `{ type, sdp }` JSON message.
[<Emit("JSON.parse($0).sdp")>]
let sdpField (json: string) : string = jsNative

/// Read an environment variable, falling back to `fallback` when unset or empty.
[<Emit("process.env[$0] || $1")>]
let envOr (name: string) (fallback: string) : string = jsNative

[<Emit("process.env[$0] = $1")>]
let setEnv (name: string) (value: string) : unit = jsNative

/// How this deployment is reached from outside: the two operator
/// variables, parsed into the one value that decides both the Manager's public origin
/// and where sessions live. Error = a combination that cannot be deployed; every caller
/// fails its boot loudly rather than starting a half-reachable process.
///
/// Read once per process at boot. The Manager parses it to render open links and to be
/// its own OIDC issuer; a session parses the same variables, inherited by plain env, to
/// build its OAuth redirect URI and to know the path it is mounted under.
let publicAccess () : Result<Yession.Domain.Link.PublicAccess, string> =
    Yession.Domain.Link.PublicAccess.create (envOr "YESSION_MANAGER_URL" "") (envOr "YESSION_SESSION_URL" "")

/// Terminate the Node process with an exit code.
[<Emit("process.exit($0)")>]
let exit (code: int) : unit = jsNative

// Command-line reading lives in `Cli`, over Node's own `parseArgs`. There used to be a
// `versionFlag` and an `argValue` here that scanned `process.argv` by hand; they could not
// tell an unknown option from an absent one, so a typo ran the bin with the option missing.
