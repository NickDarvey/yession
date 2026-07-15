module Yession.Host.Interop

// Minimal Fable bindings for the Node APIs the Session Process host needs:
// `node-datachannel` (WebRTC) and `node:http` (bootstrap + signalling). Only the surface
// actually used is bound; everything is event-callback based, matching libdatachannel.

open Fable.Core
open Fable.Core.JsInterop

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

[<Import("PeerConnection", "node-datachannel")>]
let private peerConnectionCtor : obj = jsNative

[<Emit("new $0($1, $2)")>]
let private newPeerConnection (ctor: obj) (name: string) (config: obj) : PeerConnection = jsNative

[<Import("cleanup", "node-datachannel")>]
let cleanup : unit -> unit = jsNative

/// Create a peer connection. Empty `iceServers` keeps gathering to local host candidates,
/// which is all that is needed for a local-first, same-machine session.
let createPeerConnection (name: string) : PeerConnection =
    let config = createObj [ "iceServers" ==> ([||]: obj[]) ]
    newPeerConnection peerConnectionCtor name config

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
[<Emit("typeof $0 === 'string' ? $0 : $0.toString('utf8')")>]
let bufferToString (chunk: obj) : string = jsNative

/// Read a request header (Node lowercases header names); None when absent.
[<Emit("($0.headers[$1] ?? null)")>]
let headerOf (req: IncomingMessage) (name: string) : string option = jsNative

/// A cryptographically random identifier (per-launch control secrets).
[<Emit("crypto.randomUUID()")>]
let randomSecret () : string = jsNative

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

/// Read a bundled asset: from the npm package's `assets/` directory (next to the
/// bundled entry — the packaged case), else from the dev filesystem fallback path.
/// None when neither exists. `import.meta.url` resolves to the running module, which is
/// the package-root bundle once esbuild has flattened everything into one file.
[<Emit("""(() => {
  try { return $2.readFileSync(new URL('./assets/' + $0, import.meta.url), 'utf8') } catch {}
  try { return $2.readFileSync($1, 'utf8') } catch { return null }
})()""")>]
let readAsset (assetName: string) (fallbackPath: string) (fs: obj) : string option = jsNative

/// Terminate the Node process with an exit code.
[<Emit("process.exit($0)")>]
let exit (code: int) : unit = jsNative
