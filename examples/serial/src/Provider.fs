module SerialProvider.Provider

// The serial device provider (Plan 16, part E): an ordinary MCP server that happens to own
// some ttys.
//
// Nothing about it is Yession-shaped, which is the whole point — it is separately shippable
// and useful to any MCP client. It exists because `/dev/ttyACM0` cannot speak MCP: something
// has to own the OS-level port claim, enumerate what is attached, and hand out an address a
// byte stream can be opened on. A device that speaks MCP natively needs none of this and is
// declared to a session directly.
//
// Two legs, as Plan 16 split them and Plan 17 wired up:
//
//   control  MCP over Streamable HTTP at /mcp   — list, acquire, configure, release
//   data     a WebSocket at /attach/<token>     — the bytes, both ways
//
// joined ONE WAY by the attach ticket: `acquire_device` answers with a url, and the client
// opens it. Nothing flows back the other way, so a terminal closing does not disturb the
// control leg and the control leg dropping does not close a terminal already streaming.

open System
open System.Collections.Generic
open SerialProvider
open SerialProvider.Interop

/// A device that has been claimed, and the port behind it once somebody attaches.
type private Claimed =
    { Device : SerialDevice
      /// The MCP session that took it. A claim dies with the session that took it, which is
      /// what stops a crashed client holding hardware forever.
      Holder : string
      mutable Settings : SerialSettings
      /// The single-use token in the attach url. Single-use because it IS the authority to
      /// reach the device: a token that could be replayed would let a second client take a
      /// stream the claim says is exclusive.
      mutable Token : string
      /// The live port, once the data leg opened. `None` while the claim is held but nobody
      /// has attached — an acquire that is never followed by an attach holds the DEVICE, not
      /// a file descriptor.
      mutable Port : Ports.OpenPort option }

/// The provider, as its host process sees it.
type SerialProvider =
    { /// Handle one control-leg request. `true` when it claimed the path.
      TryHandle : IncomingMessage -> ServerResponse -> bool
      /// Attach WebSocket upgrades to an HTTP server. Separate from `TryHandle` because an
      /// upgrade is not a request — it is an event on the server object.
      Serve : HttpServer -> unit
      /// What is claimed right now (observability, and what the tests read).
      Claims : unit -> SerialClaim list }

// --- the tools, declared ------------------------------------------------------------------
//
// One list, read by BOTH `tools/list` and `tools/call`, so the two cannot disagree about what
// this server accepts. The descriptions are written for the MODEL, not for a person reading
// the source: they say what the tool is for, what it costs, and what to do next, because a
// tool description is the only documentation the caller will ever see.

let Namespace = "serial"

let private deviceArg = Mcp.Schema.required "device_id" "string" "the device_id list_devices returned"

let tools : Mcp.Tool list =
    [ { Name = "list_devices"
        Title = None
        Description =
            "Every serial device attached to this host that this provider recognises, with a stable device_id, its make and model, and whether it is free. Unrecognised ports are deliberately not listed — a serial port that is not a known device is usually the machine's own console. Call this again after somebody plugs something in; the list is read from the OS each time."
        InputSchema = Mcp.Schema.ofFields [] }
      { Name = "acquire_device"
        Title = Some "Acquire a serial device"
        Description =
            "Claim a device for exclusive use and get back a URL to open its byte stream on. One holder at a time, because the OS gives out one handle: if somebody else has it, this says who. Release it when you are done."
        InputSchema = Mcp.Schema.ofFields [ deviceArg ] }
      { Name = "configure_device"
        Title = None
        Description =
            "Set the line parameters of a device you hold. Takes effect on the NEXT attach, so configure before you open the stream. Defaults are 115200 8N1, which is what most boards boot at."
        InputSchema =
            Mcp.Schema.ofFields
                [ deviceArg
                  Mcp.Schema.required "baud_rate" "integer" "e.g. 9600, 115200"
                  Mcp.Schema.optional "data_bits" "integer" "5, 6, 7 or 8 (default 8)"
                  Mcp.Schema.optional "stop_bits" "integer" "1 or 2 (default 1)"
                  Mcp.Schema.optional "parity" "string" "none, even or odd (default none)" ] }
      { Name = "release_device"
        Title = None
        Description =
            "Give a device back, closing its stream if one is open. Safe to call on a device you do not hold."
        InputSchema = Mcp.Schema.ofFields [ deviceArg ] } ]

// --- the answers ---------------------------------------------------------------------------

/// Build a provider over an engine. `origin` is the address a CLIENT should use to reach the
/// data leg — passed in rather than derived from a request, because the provider may sit
/// behind something and only its operator knows.
let create (engine: Ports.SerialEngine) (origin: unit -> string) (version: string) : SerialProvider =
    // device_id -> claim. A `Dictionary` rather than a map in a ref because a claim is
    // mutated by three different legs (configure, attach, the port closing) and threading a
    // whole map through each would be bookkeeping with no reader.
    let claimed = Dictionary<string, Claimed> ()
    // token -> device_id, so an attach resolves in one lookup and a spent token is removed
    // rather than searched for.
    let tokens = Dictionary<string, string> ()

    let devices () : Async<SerialDevice list> =
        async {
            match! engine.List () with
            | Error _ -> return []
            | Ok ports -> return Discovery.devices ports
        }

    let mintToken () = randomId ()

    let release (deviceId: string) : unit =
        match claimed.TryGetValue deviceId with
        | false, _ -> ()
        | true, claim ->
            claim.Port |> Option.iter (fun port -> port.Close ())
            claim.Port <- None
            tokens.Remove claim.Token |> ignore
            claimed.Remove deviceId |> ignore

    // --- tool bodies -------------------------------------------------------------------
    //
    // Each answers TEXT, because that is what a model reads, and each says which state it is
    // in rather than only whether it worked. `acquire_device` in particular has to name the
    // holder when it refuses: a bare "unavailable" reads as a hardware fault.

    let listDevices () : Async<string> =
        async {
            let! found = devices ()
            if List.isEmpty found then
                return
                    "no serial devices attached (or none this provider recognises — unrecognised ports are not listed)"
            else
                return
                    found
                    |> List.map (fun device ->
                        let state =
                            match claimed.TryGetValue device.DeviceId with
                            | true, claim -> sprintf "held by %s" claim.Holder
                            | _ -> "free"
                        sprintf
                            "%s — %s %s at %s (%s)"
                            device.DeviceId
                            device.Make
                            device.Model
                            device.Port.Path
                            state)
                    |> String.concat "\n"
        }

    let acquire (holder: string) (deviceId: string) : Async<string> =
        async {
            let! found = devices ()
            match found |> List.tryFind (fun d -> d.DeviceId = deviceId) with
            | None -> return sprintf "no device '%s' is attached — call list_devices" deviceId
            | Some device ->
                match claimed.TryGetValue deviceId with
                | true, claim when claim.Holder <> holder ->
                    // Naming the holder is the whole point of this branch.
                    return sprintf "%s is already held by %s — it is in use, not broken" deviceId claim.Holder
                | true, claim ->
                    return
                        sprintf
                            "you already hold %s — attach it at %s/attach/%s"
                            deviceId
                            (origin ())
                            claim.Token
                | _ ->
                    let token = mintToken ()
                    claimed.[deviceId] <-
                        { Device = device
                          Holder = holder
                          Settings = SerialSettings.defaults
                          Token = token
                          Port = None }
                    tokens.[token] <- deviceId
                    return
                        sprintf
                            "%s is yours. Attach its byte stream at %s/attach/%s — the stream is live-only, so it has no exit codes and nothing there reports whether a command succeeded."
                            deviceId
                            (origin ())
                            token
        }

    let configure (holder: string) (deviceId: string) (settings: SerialSettings) : Async<string> =
        async {
            match claimed.TryGetValue deviceId with
            | false, _ -> return sprintf "you do not hold %s — acquire it first" deviceId
            | true, claim when claim.Holder <> holder ->
                return sprintf "%s is held by %s" deviceId claim.Holder
            | true, claim ->
                match SerialSettings.validate settings with
                | Error e -> return sprintf "those line settings are not usable: %s" e
                | Ok settings ->
                    claim.Settings <- settings
                    // Stated rather than silently true: a caller who configures a device
                    // whose stream is already open and sees no change would reasonably
                    // conclude the call did nothing.
                    let already = if claim.Port.IsSome then " Re-attach for it to take effect." else ""
                    return
                        sprintf
                            "%s is set to %d %d%s%d.%s"
                            deviceId
                            settings.BaudRate
                            settings.DataBits
                            (match settings.Parity with
                             | "none" -> "N"
                             | "even" -> "E"
                             | "odd" -> "O"
                             | other -> (other.Substring (0, 1)).ToUpperInvariant ())
                            settings.StopBits
                            already
        }

    let releaseTool (holder: string) (deviceId: string) : Async<string> =
        async {
            match claimed.TryGetValue deviceId with
            | false, _ -> return sprintf "%s was not held" deviceId
            | true, claim when claim.Holder <> holder ->
                return sprintf "%s is held by %s, not by you" deviceId claim.Holder
            | true, _ ->
                release deviceId
                return sprintf "released %s" deviceId
        }

    let invoke (holder: string) (call: Mcp.ToolCall) : Async<Result<string, string>> =
        let ok (body: Async<string>) = async { let! text = body in return Ok text }
        let deviceOf (json: string) =
            match SerialWire.deviceId json with
            | Error e -> Error e
            | Ok id -> Ok id
        async {
            match call.Name with
            | "list_devices" -> return! ok (listDevices ())
            | "acquire_device" ->
                match deviceOf call.Arguments with
                | Error e -> return Error e
                | Ok id -> return! ok (acquire holder id)
            | "release_device" ->
                match deviceOf call.Arguments with
                | Error e -> return Error e
                | Ok id -> return! ok (releaseTool holder id)
            | "configure_device" ->
                match SerialWire.configure call.Arguments with
                | Error e -> return Error e
                | Ok (id, settings) -> return! ok (configure holder id settings)
            | other -> return Error (sprintf "no tool '%s'" other)
        }

    // --- the control leg ----------------------------------------------------------------

    let mcp = Mcp.create { Name = "serial"; Version = version; Tools = tools; Invoke = invoke }

    // --- the data leg -------------------------------------------------------------------

    let attach (token: string) (peer: Ws.WsPeer) : Ws.WsHandlers =
        match tokens.TryGetValue token with
        | false, _ ->
            peer.Control (SerialWire.exited 1)
            peer.Close ()
            { Data = ignore; Control = ignore; Closed = ignore }
        | true, deviceId ->
            let claim = claimed.[deviceId]
            // SPENT on use. The token is the authority to reach the device, and one that
            // could be replayed would hand a second client the stream the claim says is
            // exclusive.
            tokens.Remove token |> ignore
            let mutable port : Ports.OpenPort option = None
            let finish (why: string) =
                // The in-band termination frame, which is what tells a client the difference
                // between "the device went away" and "the network did". An abrupt close
                // carries nothing, which is exactly why this exists.
                peer.Control (SerialWire.exited 0)
                ignore why
                peer.Close ()
            Async.StartImmediate (
                async {
                    match! engine.Open claim.Device.Port.Path claim.Settings peer.Send finish with
                    | Error reason ->
                        peer.Send (sprintf "could not open %s: %s\r\n" claim.Device.Port.Path reason)
                        finish reason
                    | Ok opened ->
                        port <- Some opened
                        claim.Port <- Some opened
                })
            { Data = fun text -> port |> Option.iter (fun p -> p.Write text)
              // A serial line has no size and no signals: `resize` is nothing to do, and
              // `kill` closes the stream rather than killing anything. Saying so here beats
              // a client discovering it by silence.
              Control =
                fun text ->
                    match SerialWire.control text with
                    | SerialWire.Kill ->
                        port |> Option.iter (fun p -> p.Close ())
                        claim.Port <- None
                        finish "closed by the client"
                    | SerialWire.Resize
                    | SerialWire.Unknown -> ()
              Closed =
                fun () ->
                    port |> Option.iter (fun p -> p.Close ())
                    claim.Port <- None }

    { TryHandle = mcp.TryHandle
      Serve =
        fun server ->
            Ws.serve server (fun path ->
                if path.StartsWith "/attach/" then Some (attach (path.Substring "/attach/".Length))
                else None)
      Claims =
        fun () ->
            claimed
            |> Seq.map (fun pair ->
                { DeviceId = pair.Key
                  Holder = pair.Value.Holder
                  Settings = pair.Value.Settings
                  AttachToken = pair.Value.Token })
            |> Seq.sortBy (fun claim -> claim.DeviceId)
            |> List.ofSeq }
