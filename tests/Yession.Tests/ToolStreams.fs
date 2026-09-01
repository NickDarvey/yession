module Yession.Tests.ToolStreams

// A stream a provider offers becomes a terminal (Plan 19).
//
// The contract is the whole feature, so it is what is pinned here — not the plumbing that
// carries it. Four things have to hold no matter which provider is on the other end:
//
//   * the smallest conforming offer is a url, and everything absent reads conservatively —
//     a provider that says nothing about its capabilities gets the least a source can be,
//     never the most;
//   * a session dials only what the server it was DECLARED at can vouch for, and says so in
//     the answer when it will not;
//   * one stream is one terminal, however many times a provider mentions it — a `status`
//     tool polled every turn must not fill the pane;
//   * a tool that offers nothing is untouched, because the decorator sits on the path EVERY
//     call takes.

open Fable.Pyxpecto
open Yession.Domain
open Yession.Domain.Terminals
open Yession.Domain.Tools

let private expect result =
    match result with
    | Ok v -> v
    | Error e -> failwithf "invariant: %A" e

let private meta (body: string) = sprintf """{"%s":%s}""" StreamOffer.metaKey body

let private offerOf (body: string) = Codec.streamOffer (meta body)

let private ticket (url: string) : AttachTicket =
    { Url = url; Capabilities = SourceCapabilities.byteStream; Label = Some "a device" }

let private offer (url: string) : StreamOffer = { Ticket = ticket url; Renewable = false }

// --- the offer on the wire ----------------------------------------------------------------

let private decodeTests =
    testList "what a provider has to get right" [

        test "a url is the whole of it, and everything else reads conservatively" {
            match offerOf """{"url":"ws://127.0.0.1:7333/attach/abc"}""" with
            | None -> failwith "an offer carrying a url is an offer"
            | Some found ->
                Expect.equal found.Ticket.Url "ws://127.0.0.1:7333/attach/abc" "the address survives"
                Expect.equal
                    found.Ticket.Capabilities
                    SourceCapabilities.byteStream
                    "an unstated capability is one the source does not have"
                Expect.isFalse found.Renewable "and asking again is not assumed to be safe"
        }

        test "a source that CAN be instrumented says so, and is believed" {
            match offerOf """{"url":"ws://h/s","capabilities":{"instrument":true,"resize":true,"exitCode":true}}""" with
            | None -> failwith "the offer decoded to nothing"
            | Some found ->
                Expect.equal found.Ticket.Capabilities SourceCapabilities.shell "a remote pty is a shell's worth of source"
        }

        test "one stated capability does not imply the others" {
            match offerOf """{"url":"ws://h/s","capabilities":{"resize":true}}""" with
            | None -> failwith "the offer decoded to nothing"
            | Some found ->
                Expect.isTrue found.Ticket.Capabilities.CanResize "what was claimed"
                Expect.isFalse found.Ticket.Capabilities.CanInstrument "and nothing that was not"
                Expect.isFalse found.Ticket.Capabilities.HasExitCode "nor this"
        }

        // `_meta` is a place anybody may put anything, so reading it can never be how a tool
        // call fails: everything unreadable is "no offer", which is what a result with no
        // `_meta` at all already means.
        test "an unreadable offer is no offer, never a failed call" {
            Expect.isNone (offerOf """{"label":"no url here"}""") "an offer with no address"
            Expect.isNone (offerOf """"a string where an object goes" """) "the wrong shape entirely"
            Expect.isNone (Codec.streamOffer """{"someone.else/thing":{"url":"ws://h/s"}}""") "somebody else's key"
            Expect.isNone (Codec.streamOffer "{}") "an empty _meta"
            Expect.isNone (Codec.streamOffer "not json at all") "and something that is not JSON"
        }

        test "an offer travels beside other people's metadata rather than instead of it" {
            let both = sprintf """{"progressToken":7,"%s":{"url":"ws://h/s"}}""" StreamOffer.metaKey
            Expect.isSome (Codec.streamOffer both) "a key we do not read does not hide the one we do"
        }
    ]

// --- what a session will dial --------------------------------------------------------------

let private admissionTests =
    testList "a url this session may dial" [

        test "the stream leg may be a different port on the same host" {
            Expect.isOk
                (StreamOffer.admit "http://127.0.0.1:7334/mcp" (offer "ws://127.0.0.1:9000/attach/t") |> Result.map ignore)
                "a provider need not serve both legs on one listener"
        }

        test "another host is a declaration, and declarations are the operator's" {
            match StreamOffer.admit "http://127.0.0.1:7333/mcp" (offer "ws://10.0.0.9:7333/attach/t") with
            | Ok _ -> failwith "a tool result pointed this session at another machine and was believed"
            | Error reason ->
                Expect.stringContains reason "127.0.0.1" "the refusal names where the server was declared"
                Expect.stringContains reason "10.0.0.9" "and where it tried to send us"
        }

        test "only a websocket url is a stream" {
            Expect.isError
                (StreamOffer.admit "http://127.0.0.1:7333/mcp" (offer "http://127.0.0.1:7333/attach") |> Result.map ignore)
                "http is the control leg's scheme, not the data leg's"
            Expect.isError
                (StreamOffer.admit "http://127.0.0.1:7333/mcp" (offer "file:///etc/passwd") |> Result.map ignore)
                "and a url that is not a network address at all"
        }

        test "credentials in the url are refused rather than carried into a connect" {
            Expect.isError
                (StreamOffer.admit "http://127.0.0.1:7333/mcp" (offer "ws://user:pw@127.0.0.1:7333/attach") |> Result.map ignore)
                "an authority with userinfo in it"
        }

        test "an unlabelled offer is named after the call it came from" {
            let bare = { Ticket = { ticket "ws://h/s" with Label = None }; Renewable = false }
            Expect.equal (StreamOffer.named "serial/acquire_device" bare).Ticket.Label (Some "serial/acquire_device") "a panel always has a title"
            Expect.equal
                (StreamOffer.named "serial/acquire_device" (offer "ws://h/s")).Ticket.Label
                (Some "a device")
                "and a provider that named its own stream keeps the name"
        }
    ]

// --- the decorator -------------------------------------------------------------------------

/// A registry of one tool, answering with whatever offer the test hands it.
let private serving (answers: StreamOffer option list) : ToolRegistry * (unit -> int) =
    let remaining = ref answers
    let calls = ref 0
    let registry =
        ToolRegistry.ofNamespace
            "serial"
            [ ToolDescriptor.create "serial" "acquire" "claim a device" ToolSchema.none ]
            (fun _ ->
                async {
                    calls.Value <- calls.Value + 1
                    let next, rest =
                        match remaining.Value with
                        | [] -> None, []
                        | head :: tail -> head, tail
                    remaining.Value <- rest
                    return Ok { ToolAnswer.text "ttyACM0 is yours." with Stream = next }
                })
    registry, (fun () -> calls.Value)

/// A terminal manager that records what it was asked to open, and can be told a terminal has
/// since been closed.
type private Panel () =
    let openedFor = ResizeArray<string> ()
    let closed = System.Collections.Generic.HashSet<string> ()
    member _.Opened = List.ofSeq openedFor
    member _.Close (id: string) = closed.Add id |> ignore
    member this.Attach : StreamAttach =
        { Open =
            fun offer ->
                async {
                    openedFor.Add offer.Ticket.Url
                    return TerminalId.create (sprintf "term-%d" openedFor.Count) |> Result.mapError (sprintf "%A")
                }
          IsOpen = fun id -> not (closed.Contains (TerminalId.value id)) }

let private invoke (registry: ToolRegistry) =
    registry.Invoke { Namespace = "serial"; Name = "acquire"; Arguments = "{}" }

let private decoratorTests =
    testList "an offer becomes a terminal" [

        testCaseAsync "a tool that offers nothing is untouched" <|
            async {
                let panel = Panel ()
                let registry, _ = serving [ None ]
                let decorated = (ToolStreams.create (fun () -> []) panel.Attach).Decorate registry
                let! answer = invoke decorated
                Expect.equal (expect answer).Text "ttyACM0 is yours." "the provider's own prose, unaltered"
                Expect.isEmpty panel.Opened "and nothing was opened"
            }

        testCaseAsync "an offer opens a terminal, and the model is told which one" <|
            async {
                let panel = Panel ()
                let registry, _ = serving [ Some (offer "ws://127.0.0.1:7333/attach/one") ]
                let decorated = (ToolStreams.create (fun () -> []) panel.Attach).Decorate registry
                let! answer = invoke decorated
                let text = (expect answer).Text
                Expect.stringContains text "ttyACM0 is yours." "the provider's answer survives"
                Expect.stringContains text "term-1" "and the session adds the one fact only it knows"
                Expect.equal panel.Opened [ "ws://127.0.0.1:7333/attach/one" ] "the stream was attached"
            }

        // A provider that reports its live stream on every `status` offers the same url every
        // time. One stream is one terminal.
        testCaseAsync "the same stream offered twice is one terminal" <|
            async {
                let panel = Panel ()
                let url = "ws://127.0.0.1:7333/attach/one"
                let registry, _ = serving [ Some (offer url); Some (offer url) ]
                let attacher = ToolStreams.create (fun () -> []) panel.Attach
                let decorated = attacher.Decorate registry
                let! _ = invoke decorated
                // A LATER turn, which is the case the session-scoped map exists for.
                let! second = invoke (attacher.Decorate registry)
                Expect.equal panel.Opened [ url ] "the second offer found the terminal it already had"
                Expect.stringContains (expect second).Text "term-1" "and the model is pointed at that one"
            }

        testCaseAsync "a stream whose terminal was closed opens a new one" <|
            async {
                let panel = Panel ()
                let url = "ws://127.0.0.1:7333/attach/one"
                let registry, _ = serving [ Some (offer url); Some (offer url) ]
                let attacher = ToolStreams.create (fun () -> []) panel.Attach
                let! _ = invoke (attacher.Decorate registry)
                panel.Close "term-1"
                let! second = invoke (attacher.Decorate registry)
                Expect.equal panel.Opened [ url; url ] "a closed terminal is not the one you have"
                Expect.stringContains (expect second).Text "term-2" "and the new one is what the model is told about"
            }

        testCaseAsync "a turn cannot be given more terminals than it asked for" <|
            async {
                let panel = Panel ()
                let offers = [ for i in 1..ToolStreams.perTurnLimit + 2 -> Some (offer (sprintf "ws://127.0.0.1:7333/attach/%d" i)) ]
                let registry, _ = serving offers
                let decorated = (ToolStreams.create (fun () -> []) panel.Attach).Decorate registry
                let mutable last = ""
                for _ in offers do
                    let! answer = invoke decorated
                    last <- (expect answer).Text
                Expect.equal (List.length panel.Opened) ToolStreams.perTurnLimit "the ceiling holds"
                Expect.stringContains last "not opened" "and the model is told why, rather than left waiting"
            }

        // The ceiling counts terminals, not offers — otherwise a provider that mentions its
        // one stream five times would exhaust a turn that opened a single panel.
        testCaseAsync "an offer that opened nothing new does not spend the ceiling" <|
            async {
                let panel = Panel ()
                let url = "ws://127.0.0.1:7333/attach/one"
                let repeated = [ for _ in 1..ToolStreams.perTurnLimit + 2 -> Some (offer url) ]
                let registry, _ = serving (repeated @ [ Some (offer "ws://127.0.0.1:7333/attach/two") ])
                let decorated = (ToolStreams.create (fun () -> []) panel.Attach).Decorate registry
                for _ in repeated do
                    let! _ = invoke decorated
                    ()
                let! answer = invoke decorated
                Expect.equal panel.Opened [ url; "ws://127.0.0.1:7333/attach/two" ] "the second stream still got its terminal"
                Expect.stringContains (expect answer).Text "term-2" "and the model was told about it"
            }

        testCaseAsync "a session that cannot open the stream says so where the model reads" <|
            async {
                let registry, _ = serving [ Some (offer "ws://127.0.0.1:7333/attach/one") ]
                let attach : StreamAttach =
                    { Open = fun _ -> async { return Error "the provider is not listening" }
                      IsOpen = fun _ -> false }
                let decorated = (ToolStreams.create (fun () -> []) attach).Decorate registry
                let! answer = invoke decorated
                Expect.stringContains (expect answer).Text "the provider is not listening" "the reason reaches the model"
            }
    ]

// --- asking again -------------------------------------------------------------------------

let private renewable (url: string) : StreamOffer = { Ticket = ticket url; Renewable = true }

let private reattachTests =
    testList "asking a provider for a stream again" [

        // A person who closed a device panel should not have to ask the agent to say the
        // magic word again — but the magic word is a tool name this session learned at
        // runtime, so what it replays is the CALL, not a url somebody typed.
        testCaseAsync "replays the call that produced the stream, with its own arguments" <|
            async {
                let panel = Panel ()
                let calls = ResizeArray<ToolCall> ()
                let registry =
                    ToolRegistry.ofNamespace
                        "serial"
                        [ ToolDescriptor.create "serial" "acquire" "claim a device" ToolSchema.none ]
                        (fun call ->
                            async {
                                calls.Add call
                                let url = sprintf "ws://127.0.0.1:7333/attach/%d" calls.Count
                                return Ok { ToolAnswer.text "ttyACM0 is yours." with Stream = Some (renewable url) }
                            })
                let attacher = ToolStreams.create (fun () -> [ registry ]) panel.Attach
                let! _ = invoke (attacher.Decorate registry)
                panel.Close "term-1"

                match! attacher.Reattach (TerminalId.create "term-1" |> expect) with
                | Error e -> failwithf "a renewable stream should be askable for again: %s" e
                | Ok id ->
                    Expect.equal (TerminalId.value id) "term-2" "a new terminal, because a spent ticket is spent"
                    Expect.equal (List.ofSeq calls |> List.map (fun c -> c.Name, c.Arguments)) [ "acquire", "{}"; "acquire", "{}" ] "the same call, with the same arguments"
            }

        // The default, and the reason it is the default: replaying an unknown call could
        // power-cycle somebody's board.
        testCaseAsync "refuses when the provider never said asking again was safe" <|
            async {
                let panel = Panel ()
                let registry, _ = serving [ Some (offer "ws://127.0.0.1:7333/attach/one") ]
                let attacher = ToolStreams.create (fun () -> [ registry ]) panel.Attach
                let! _ = invoke (attacher.Decorate registry)
                match! attacher.Reattach (TerminalId.create "term-1" |> expect) with
                | Ok _ -> failwith "a stream nobody said was renewable was asked for again anyway"
                | Error reason -> Expect.stringContains reason "again" "and it says what is missing"
            }

        testCaseAsync "a provider that has nothing to give answers in its own words" <|
            async {
                let panel = Panel ()
                let answers = ResizeArray<Result<ToolAnswer, string>> ()
                answers.Add (Ok { ToolAnswer.text "yours." with Stream = Some (renewable "ws://h/s") })
                answers.Add (Ok (ToolAnswer.text "ttyACM0 is held by somebody else — it is in use, not broken"))
                let mutable next = 0
                let registry =
                    ToolRegistry.ofNamespace
                        "serial"
                        [ ToolDescriptor.create "serial" "acquire" "claim a device" ToolSchema.none ]
                        (fun _ ->
                            async {
                                let answer = answers.[min next (answers.Count - 1)]
                                next <- next + 1
                                return answer
                            })
                let attacher = ToolStreams.create (fun () -> [ registry ]) panel.Attach
                let! _ = invoke (attacher.Decorate registry)
                panel.Close "term-1"
                match! attacher.Reattach (TerminalId.create "term-1" |> expect) with
                | Ok _ -> failwith "there was no stream to attach"
                | Error reason -> Expect.stringContains reason "in use, not broken" "the provider's words survive"
            }
    ]

let tests =
    testList "ToolStreams (Plan 19)" [ decodeTests; admissionTests; decoratorTests; reattachTests ]
