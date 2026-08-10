module Yession.Host.SerialMain

// `yession-serial` — the serial device provider as its own process (Plan 16, part E).
//
// A third bin rather than something inside the Manager or a session, and the reasons are the
// ones Plan 16 gives: a device is a HOST resource, the OS hands out one file descriptor for
// it, and something has to arbitrate. Putting that in the Manager would give the Manager a
// native dependency and a device claim it must not hold; putting it in a session would make
// two sessions race on a file descriptor and get `EBUSY` instead of "session A has it".
//
// It knows nothing about Yession. Declare it to a session with the management UI's MCP form
// and the session's own MCP client does the rest — the same way any third party's server is
// declared, which is the whole point of the split.

open Yession.Domain
open Yession.Host

// `--version` before anything is read, like the other two bins: a running Yession is never
// anonymous, and this is the one thing it will do without a port.
if Interop.versionFlag () then
    printfn "%s" Version.current
    Interop.exit 0

let private port = Interop.envOr "YESSION_SERIAL_PORT" "7333" |> int
let private host = Interop.envOr "YESSION_SERIAL_HOST" "127.0.0.1"

/// The address a CLIENT uses to reach the data leg. Configurable because the provider may sit
/// somewhere a session cannot name by the interface it bound — and defaulted to loopback,
/// which is the only deployment this round is honest about: the control leg is
/// unauthenticated, so binding it to anything but loopback would hand the host's serial ports
/// to the network.
///
/// Read from the BOUND port, not the requested one, which is why the provider takes a thunk:
/// `YESSION_SERIAL_PORT=0` is a legitimate ask (every test does it), and an origin computed
/// before `listen` would hand every client `ws://127.0.0.1:0`.
let private configuredOrigin = Interop.envOr "YESSION_SERIAL_ORIGIN" ""
let mutable private origin = configuredOrigin

let private provider = SerialProvider.create SerialPorts.real (fun () -> origin)

let private server =
    Interop.createServer (fun req res ->
        if provider.TryHandle req res then ()
        else
            res.writeHead (404, Fable.Core.JsInterop.createObj [ "content-type", box "text/plain" ]) |> ignore
            res.``end`` "not found")

provider.Serve server

server.listen (
    port,
    host,
    fun () ->
        if configuredOrigin = "" then
            origin <- sprintf "ws://%s:%d" host (Interop.serverPort server)
        // One line, and it names both legs, because an operator declaring this in the
        // management UI needs the control url and nothing else.
        printfn
            "yession-serial %s: MCP at http://%s:%d/mcp, streams at %s/attach/<token>"
            Version.current
            host
            (Interop.serverPort server)
            origin)
|> ignore
