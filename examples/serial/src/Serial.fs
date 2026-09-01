namespace SerialProvider

// The serial device provider's vocabulary (Plan 16, part E).
//
// This is the DOMAIN half of a program that is deliberately not Yession-shaped. `yession-serial`
// is a separate bin, useful to any MCP client, and the only reason it lives in this repo is
// that we wanted one: nothing in Plan 17 knows what serial is, and a device that speaks MCP
// over HTTP natively is declared exactly the same way with no provider at all.
//
// What is here is what can be decided without a port: which physical ports are interesting,
// what a device is called, what the four tools take and answer. The engine — opening a tty,
// setting a baud rate, streaming bytes — is a seam the Host fills, so all of the above is
// testable with no hardware and no native addon.

open System

/// A USB vendor/product pair, as `serialport` reports it: four lowercase hex digits, or
/// absent for a port that is not on a USB bus (an on-board UART, a PTY).
type UsbId =
    { Vendor : string
      Product : string }

module UsbId =

    /// Normalised: lowercase, no `0x`, four digits. `serialport` is inconsistent across
    /// platforms about all three, and a table keyed on the raw string would match on Linux
    /// and miss on macOS.
    let create (vendor: string) (product: string) : UsbId option =
        let clean (raw: string) =
            match Option.ofObj raw with
            | None -> None
            | Some value ->
                let value = value.Trim().ToLowerInvariant()
                let value = if value.StartsWith "0x" then value.Substring 2 else value
                if value.Length = 0 || value.Length > 4 then None
                elif value |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')) then
                    Some (value.PadLeft (4, '0'))
                else None
        match clean vendor, clean product with
        | Some vendor, Some product -> Some { Vendor = vendor; Product = product }
        | _ -> None

    let describe (id: UsbId) = sprintf "%s:%s" id.Vendor id.Product

/// One port as the OS enumerated it, before we decide whether it is interesting.
type SerialPortInfo =
    { /// The OS path or name: `/dev/ttyACM0`, `/dev/cu.usbmodem1101`, `COM3`.
      Path : string
      Usb : UsbId option
      /// The device's own serial number, when it has one. This is what makes an id STABLE:
      /// unplug an adapter and plug it into another hub and its path moves, but this does
      /// not.
      SerialNumber : string option
      /// Whatever the OS volunteered, for the humans. Never used to identify anything.
      Manufacturer : string option }

/// A device we are willing to hand out, and what to call it.
type SerialDevice =
    { /// Stable across replug where the hardware allows it — see `Discovery.identify`.
      DeviceId : string
      Make : string
      Model : string
      Port : SerialPortInfo }

/// What one known `(vid,pid)` is. A table rather than a guess: an unrecognised port is
/// EXCLUDED rather than offered under its raw path, because a provider that hands an agent
/// every tty on the box has handed it the console.
type VidPidEntry =
    { Usb : UsbId
      Make : string
      Model : string }

module Discovery =

    let private entry vendor product make model =
        UsbId.create vendor product
        |> Option.map (fun usb -> { Usb = usb; Make = make; Model = model })

    /// The recognised devices. Small on purpose and easy to extend — an operator with
    /// hardware we have never seen adds a line, and until then that hardware is invisible
    /// rather than mislabelled.
    let known : VidPidEntry list =
        [ entry "2341" "0043" "Arduino" "Uno"
          entry "2341" "0001" "Arduino" "Uno"
          entry "2341" "8036" "Arduino" "Leonardo"
          entry "2341" "0042" "Arduino" "Mega 2560"
          entry "2a03" "0043" "Arduino" "Uno (org)"
          entry "1a86" "7523" "QinHeng" "CH340"
          entry "1a86" "55d4" "QinHeng" "CH9102"
          entry "0403" "6001" "FTDI" "FT232R"
          entry "0403" "6015" "FTDI" "FT231X"
          entry "10c4" "ea60" "Silicon Labs" "CP2102"
          entry "303a" "1001" "Espressif" "ESP32-S3"
          entry "2e8a" "0005" "Raspberry Pi" "Pico" ]
        |> List.choose id

    /// A device id that survives a replug. The serial number when the hardware has one —
    /// which is the whole point, because the PATH does not survive moving the cable to
    /// another port — and the path when it does not, stated rather than faked.
    let identify (entry: VidPidEntry) (port: SerialPortInfo) : string =
        let suffix =
            match port.SerialNumber with
            | Some serial when serial.Trim () <> "" -> serial.Trim ()
            // No serial number: the id is only as stable as the path, and a caller that
            // replugs into another socket gets a different device. Naming it after the path
            // says so; inventing a counter would hide it.
            | _ -> port.Path
        let clean (raw: string) =
            raw
            |> Seq.map (fun c ->
                if (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') then c
                elif c >= 'A' && c <= 'Z' then Char.ToLowerInvariant c
                else '_')
            |> Seq.toArray
            |> String
        sprintf "%s_%s" (clean (sprintf "%s_%s" entry.Make entry.Model)) (clean suffix)

    /// The ports worth offering, in path order so a list does not shuffle between calls.
    ///
    /// Unrecognised ports are dropped. That is the single most important line in this file:
    /// `/dev/ttyS0` is a serial port, and on a lot of machines it is the system console.
    let devices (ports: SerialPortInfo list) : SerialDevice list =
        ports
        |> List.sortBy (fun port -> port.Path)
        |> List.choose (fun port ->
            match port.Usb with
            | None -> None
            | Some usb ->
                known
                |> List.tryFind (fun entry -> entry.Usb = usb)
                |> Option.map (fun entry ->
                    { DeviceId = identify entry port
                      Make = entry.Make
                      Model = entry.Model
                      Port = port }))

/// The line settings a device is opened with. Defaults are 8N1 at 115200, which is what
/// nearly every board in the table above boots at.
type SerialSettings =
    { BaudRate : int
      DataBits : int
      StopBits : int
      /// "none" | "even" | "odd" — `serialport`'s own spelling, passed through.
      Parity : string }

module SerialSettings =

    let defaults : SerialSettings =
        { BaudRate = 115200; DataBits = 8; StopBits = 1; Parity = "none" }

    /// Refused rather than clamped: a caller who asked for 9 data bits has a bug, and
    /// silently giving them 8 makes it somebody else's bug later.
    let validate (settings: SerialSettings) : Result<SerialSettings, string> =
        if settings.BaudRate <= 0 then Error "baud rate must be positive"
        elif settings.DataBits < 5 || settings.DataBits > 8 then Error "data bits must be 5, 6, 7 or 8"
        elif settings.StopBits <> 1 && settings.StopBits <> 2 then Error "stop bits must be 1 or 2"
        elif [ "none"; "even"; "odd"; "mark"; "space" ] |> List.contains settings.Parity |> not then
            Error "parity must be none, even, odd, mark or space"
        else Ok settings

/// Who holds a device's port. One claim at a time, because the OS gives out one file
/// descriptor and a second opener gets `EBUSY` — a refusal that reads as a hardware fault
/// unless somebody names the holder.
type SerialClaim =
    { DeviceId : string
      /// The MCP session that acquired it. A claim dies with the session that took it.
      Holder : string
      Settings : SerialSettings
      /// The single-use path the holder attaches its byte stream to.
      AttachToken : string }

/// The provider's wire: what its tools take, and the two control frames the byte-stream leg
/// carries. Codecs rather than string surgery in the Host, so the protocol a third party has
/// to implement is written down in one testable place.
module SerialWire =

#if FABLE_COMPILER
    open Thoth.Json
#else
    open Thoth.Json.Net
#endif

    let private read (decoder: Decoder<'a>) (json: string) : Result<'a, string> =
        let json = if String.IsNullOrWhiteSpace json then "{}" else json
        match Decode.fromString decoder json with
        | Ok value -> Ok value
        | Error e -> Error (sprintf "could not read the arguments: %s" e)

    let deviceId (json: string) : Result<string, string> =
        read (Decode.object (fun get -> get.Required.Field "device_id" Decode.string)) json

    /// `configure_device`'s arguments. Everything but the baud rate is optional and falls
    /// back to the default rather than to the device's CURRENT setting: a partial configure
    /// that silently kept three of four parameters would make the answer depend on history.
    let configure (json: string) : Result<string * SerialSettings, string> =
        read
            (Decode.object (fun get ->
                get.Required.Field "device_id" Decode.string,
                { BaudRate = get.Required.Field "baud_rate" Decode.int
                  DataBits =
                    get.Optional.Field "data_bits" Decode.int
                    |> Option.defaultValue SerialSettings.defaults.DataBits
                  StopBits =
                    get.Optional.Field "stop_bits" Decode.int
                    |> Option.defaultValue SerialSettings.defaults.StopBits
                  Parity =
                    get.Optional.Field "parity" Decode.string
                    |> Option.defaultValue SerialSettings.defaults.Parity }))
            json

    /// The in-band termination frame. A WebSocket close carries nothing a client can read as
    /// an outcome, so the provider says how it ended BEFORE closing — which is what lets an
    /// abrupt drop be distinguishable from a device that finished.
    let exited (code: int) : string =
        Encode.object [ "type", Encode.string "exited"; "code", Encode.int code ]
        |> Encode.toString 0

    /// What a client's control frame asked for.
    type Control =
        | Resize
        | Kill
        | Unknown

    let control (json: string) : Control =
        let decoder = Decode.object (fun get -> get.Required.Field "type" Decode.string)
        match Decode.fromString decoder json with
        | Ok "resize" -> Resize
        | Ok "kill" -> Kill
        | _ -> Unknown
