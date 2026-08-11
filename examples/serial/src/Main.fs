module SerialProvider.Main

// `serial-provider` — an MCP server that lends the host's serial devices to an agent.
//
// A SEPARATE PROCESS rather than something inside an agent runtime, and the reason is the
// resource: a serial device is a host resource the OS hands out one file descriptor for, so
// something has to arbitrate. Put that inside the runtime and it holds a device claim it has
// no business holding; put it inside each agent session and two of them race for the same fd
// and get `EBUSY` instead of "session A has it".
//
// It knows nothing about the client that will call it. Point any MCP client at the control
// url below and it works — which is the whole point of an out-of-process provider, and the
// reason this is an example rather than a built-in.

open SerialProvider

// No options of its own; configured by the environment. `--version` and `--help` answer
// before anything is read: a running provider should never be anonymous, and this is the one
// thing it will do without a port.
let private version = "0.1.0"

match Interop.args () |> Array.toList with
| [ "--version" ] | [ "-v" ] ->
    printfn "%s" version
    Interop.exit 0
| [ "--help" ] | [ "-h" ] ->
    printfn "serial-provider %s" version
    printfn ""
    printfn "An MCP server offering the host's serial devices as four tools."
    printfn ""
    printfn "Configured entirely by the environment:"
    printfn "  YESSION_SERIAL_PORT    port to bind        (default 7333; 0 = OS-assigned)"
    printfn "  YESSION_SERIAL_HOST    interface to bind   (default 127.0.0.1)"
    printfn "  YESSION_SERIAL_ORIGIN  ws:// origin clients should attach to (default: the bound address)"
    Interop.exit 0
| _ -> ()

let private port = Interop.envOr "YESSION_SERIAL_PORT" "7333" |> int
let private host = Interop.envOr "YESSION_SERIAL_HOST" "127.0.0.1"

/// The address a CLIENT uses to reach the data leg. Configurable because the provider may sit
/// somewhere a client cannot name by the interface it bound — and defaulted to loopback,
/// which is the only deployment this example is honest about: the control leg is
/// unauthenticated, so binding it to anything but loopback would hand the host's serial ports
/// to the network.
///
/// Read from the BOUND port, not the requested one, which is why the provider takes a thunk:
/// `YESSION_SERIAL_PORT=0` is a legitimate ask (every test does it), and an origin computed
/// before `listen` would hand every client `ws://127.0.0.1:0`.
let private configuredOrigin = Interop.envOr "YESSION_SERIAL_ORIGIN" ""
let mutable private origin = configuredOrigin

let private provider = Provider.create Ports.real (fun () -> origin) version

let private server =
    Interop.createServer (fun req res ->
        if provider.TryHandle req res then ()
        else
            res.writeHead (404, Interop.createObj [ "content-type", box "text/plain" ]) |> ignore
            res.``end`` "not found")

provider.Serve server

server.listen (
    port,
    host,
    fun () ->
        if configuredOrigin = "" then
            origin <- sprintf "ws://%s:%d" host (Interop.serverPort server)
        // One line, and it names both legs, because an operator declaring this to a client
        // needs the control url and nothing else.
        printfn
            "serial-provider %s: MCP at http://%s:%d/mcp, streams at %s/attach/<token>"
            version
            host
            (Interop.serverPort server)
            origin)
|> ignore
