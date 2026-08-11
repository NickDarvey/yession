module SerialProvider.Interop

// The Node surface this provider needs, and nothing else.
//
// Every binding here is one `Emit` over a Node API. There is no framework underneath, which
// is the point of the example: a provider is an ordinary HTTP server that happens to answer
// MCP, and the whole of its runtime dependency is `node:http` plus a hash function.
//
// It is deliberately a SEPARATE file from the protocol and the device. Fable compiles
// `Emit` verbatim, so this is the only place raw JavaScript appears — if you are porting
// this provider to another host (Deno, Bun, a browser extension talking to WebSerial), this
// is the file you rewrite and the other five you keep.

open Fable.Core

// --- promises -----------------------------------------------------------------------------

// Settled at CREATION, in the same tick the promise was made, rather than inside the
// workflow: that is the only tick in which attaching a handler is guaranteed to beat Node's
// unhandled-rejection check. Deferring it into the `async` reopens the gap this closes, and
// an unhandled rejection in Node is a dead process rather than a console warning.
[<Emit("$0.then(value => [value, null], error => [null, error ?? new Error('promise rejected')])")>]
let private settled (promise: JS.Promise<'a>) : JS.Promise<'a * exn> = jsNative

/// Await a promise; a rejection surfaces as an ordinary exception inside the workflow.
let awaitPromise (promise: JS.Promise<'a>) : Async<'a> =
    let outcome = settled promise
    async {
        let! value, error = outcome |> Async.AwaitPromise
        if isNull (box error) then return value else return raise error
    }

// --- node:http ----------------------------------------------------------------------------

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

[<Import("createServer", "node:http")>]
let private createServerRaw : System.Func<IncomingMessage, ServerResponse, unit> -> HttpServer = jsNative

/// Create an HTTP server. The handler is passed as an uncurried delegate so Node receives a
/// plain `(req, res) => ...` two-argument callback rather than F#'s curried chain.
let createServer (handler: IncomingMessage -> ServerResponse -> unit) : HttpServer =
    createServerRaw (System.Func<_, _, _>(handler))

/// The actual bound port, which differs from the requested one when listening on 0 — and
/// listening on 0 is what every test does, so the origin a client is handed has to be read
/// back from the socket rather than assumed from the request.
[<Emit("$0.address().port")>]
let serverPort (server: HttpServer) : int = jsNative

/// Decode a Node Buffer (or string) chunk to a UTF-8 string.
[<Emit("typeof $0 === 'string' ? $0 : $0.toString('utf8')")>]
let bufferToString (chunk: obj) : string = jsNative

/// Read a request header. Node lowercases header names; None when absent.
[<Emit("($0.headers[$1] ?? null)")>]
let headerOf (req: IncomingMessage) (name: string) : string option = jsNative

/// Build a plain JS object from pairs — response headers, mostly.
let createObj (pairs: (string * obj) list) : obj = JsInterop.createObj pairs

// --- process ------------------------------------------------------------------------------

/// A cryptographically random identifier: MCP session ids and single-use attach tokens.
[<Emit("crypto.randomUUID()")>]
let randomId () : string = jsNative

/// Read an environment variable, falling back when unset or empty.
[<Emit("process.env[$0] || $1")>]
let envOr (name: string) (fallback: string) : string = jsNative

/// The process's arguments, less the executable and script.
[<Emit("process.argv.slice(2)")>]
let args () : string array = jsNative

[<Emit("process.exit($0)")>]
let exit (code: int) : unit = jsNative
