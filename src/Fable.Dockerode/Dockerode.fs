module Fable.Dockerode

// Fable bindings to the `dockerode` npm package — the de-facto Node client for the Docker
// Engine API (Docker ships no official Node SDK; only Go and Python are official). This is
// the binding layer only, mirroring how `Fable.Yjs` wraps `yjs`: it declares the slice of
// dockerode's surface the Docker backend uses and nothing more. The engine lives behind
// the `ContainerBackend` seam, so nothing above this file knows dockerode exists.
//
// Fable-only: `dotnet build` type-checks it; Fable emits the JS that runs on Node. Methods
// that return promises surface as `JS.Promise<_>` for `Async.AwaitPromise` at the call site.

open Fable.Core
open Fable.Core.JsInterop

/// The slice of a Node stream we consume: event subscription only.
type [<AllowNullLiteral>] Stream =
    abstract on: event: string * handler: (obj -> unit) -> Stream

/// A `node:stream` PassThrough — a writable sink `demuxStream` pushes one output stream
/// into, and a readable we drain via its `'data'`/`'end'` events.
type [<AllowNullLiteral>] PassThrough =
    inherit Stream

/// A running `docker exec` handle.
type [<AllowNullLiteral>] Exec =
    /// Start the exec; resolves to the (multiplexed) output stream.
    abstract start: options: obj -> JS.Promise<Stream>
    /// Inspect after completion; the result carries `ExitCode` (null while running).
    abstract inspect: unit -> JS.Promise<obj>

/// A container handle (created, or looked up by name/id).
type [<AllowNullLiteral>] Container =
    /// The full container id assigned by the daemon.
    abstract id: string
    abstract start: unit -> JS.Promise<obj>
    abstract remove: options: obj -> JS.Promise<obj>
    abstract exec: options: obj -> JS.Promise<Exec>
    abstract inspect: unit -> JS.Promise<obj>

/// A named-volume handle.
type [<AllowNullLiteral>] Volume =
    abstract remove: options: obj -> JS.Promise<obj>

/// An image handle — used to test local presence before pulling.
type [<AllowNullLiteral>] Image =
    /// Resolves if the image exists locally; rejects otherwise.
    abstract inspect: unit -> JS.Promise<obj>

/// docker-modem: the low-level plumbing dockerode exposes for stream handling.
type [<AllowNullLiteral>] Modem =
    /// Split Docker's multiplexed exec stream into stdout/stderr sinks.
    abstract demuxStream: source: Stream * stdout: PassThrough * stderr: PassThrough -> unit
    /// Drain a build/pull progress stream, calling back once when it finishes: `(err, output)`.
    abstract followProgress: source: Stream * onFinished: (obj -> obj -> unit) -> unit

/// The dockerode client, bound to the local daemon socket.
type [<AllowNullLiteral>] Docker =
    /// Resolves when the daemon answers; rejects when it is unreachable.
    abstract ping: unit -> JS.Promise<obj>
    abstract createContainer: options: obj -> JS.Promise<Container>
    abstract getContainer: id: string -> Container
    abstract createVolume: options: obj -> JS.Promise<obj>
    abstract getVolume: name: string -> Volume
    abstract getImage: name: string -> Image
    /// Pull an image; resolves to the progress stream to drain before use.
    abstract pull: tag: string -> JS.Promise<Stream>
    /// Build an image from a tar/`{ context; src }` and options `{ t; dockerfile }`.
    abstract buildImage: context: obj * options: obj -> JS.Promise<Stream>
    abstract listContainers: options: obj -> JS.Promise<obj array>
    abstract modem: Modem

[<Emit("new $0($1)")>]
let private newWith (ctor: obj) (opts: obj) : 'a = jsNative

let private dockerCtor: obj = importDefault "dockerode"
let private passThroughCtor: obj = import "PassThrough" "node:stream"

/// A client on the default local socket (`/var/run/docker.sock`, or the platform default).
let create () : Docker = newWith dockerCtor (createObj [])

/// A fresh PassThrough sink for demuxing exec output.
let createPassThrough () : PassThrough = newWith passThroughCtor (createObj [])
