module Yession.Tests.Cli

// The command-line boundary. Pure — cheap tier, every environment — because `Cli.parse`
// takes its args as a value rather than reading `process.argv`, which is the whole reason
// the boundary is testable at all.
//
// What these pin is that a MISUSE IS REFUSED. The parsing itself is Node's (`node:util`
// parseArgs) and is not this repo's to test; what is this repo's is that a mistyped or
// malformed command line stops the process instead of running it with the option missing.
// The old hand-rolled `process.argv` scan could not tell "not given" from "given wrong", so
// `yession-manager --auht localhost` booted a deny-everything Manager and looked like a hang.

open Fable.Core
open Fable.Core.JsInterop
open Fable.Pyxpecto
open Yession.Host

// Reading this repository's own workflow files, for the retirement scan below. The Cli suite
// is Node-only (it needs no browser), so `node:fs` is always there when this runs, and the
// suite's working directory is the repository root.
let private nodeFs : obj = importAll "node:fs"

[<Emit("$0.readdirSync($1)")>]
let private readDir (fs: obj) (dir: string) : string array = jsNative

[<Emit("$0.readFileSync($1, 'utf8')")>]
let private readText (fs: obj) (path: string) : string = jsNative

let private auth = Cli.value "auth" "rule" "how a request's subject is established"
let private secrets = Cli.value "secrets" "mode" "whether secrets persist"
let private webhook = Cli.values "webhook" "name" "a webhook endpoint to serve"
let private spec = Cli.spec "yession-manager" [ auth; secrets; webhook ]

let private parse (args: string list) = Cli.parse spec (Array.ofList args)

let private parsed (args: string list) =
    match parse args with
    | Ok p -> p
    | Error e -> failwithf "expected a parse, got: %s" e

let private refused (args: string list) =
    match parse args with
    | Error e -> e
    | Ok _ -> failwithf "expected a refusal for %A" args

let tests =
    testList "Cli" [
        testCase "an option's value is read back with the option that declared it" <| fun () ->
            let p = parsed [ "--auth"; "localhost" ]
            Expect.equal (Cli.valueOf auth p) (Some "localhost") "the value"
            Expect.isTrue (Cli.isSet auth p) "and it counts as given"
            // An option that was not given is None, not an error — absent is a legitimate
            // answer for every option here (`--auth` absent means deny-everything).
            Expect.equal (Cli.valueOf secrets p) None "an option not given"
            Expect.isFalse (Cli.isSet secrets p) "and does not count as given"

        testCase "both spellings of a value reach the same place" <| fun () ->
            Expect.equal (Cli.valueOf auth (parsed [ "--auth=localhost" ])) (Some "localhost") "--name=value"
            Expect.equal (Cli.valueOf auth (parsed [ "--auth"; "localhost" ])) (Some "localhost") "--name value"

        testCase "an empty command line parses to nothing given" <| fun () ->
            let p = parsed []
            Expect.isFalse (Cli.isSet auth p) "no auth"
            Expect.isFalse (Cli.isSet Cli.version p) "no version"
            Expect.isFalse (Cli.isSet Cli.help p) "no help"

        testCase "switches answer to their long and short spellings" <| fun () ->
            Expect.isTrue (Cli.isSet Cli.version (parsed [ "--version" ])) "--version"
            Expect.isTrue (Cli.isSet Cli.version (parsed [ "-v" ])) "-v"
            Expect.isTrue (Cli.isSet Cli.help (parsed [ "--help" ])) "--help"
            Expect.isTrue (Cli.isSet Cli.help (parsed [ "-h" ])) "-h"

        // A repeatable option is configuration that is a SET, so what it pins is that every
        // value survives IN ORDER — a reader that took the last would look identical for the
        // one-value case every test writes first.
        testCase "a repeatable option collects every value, in order" <| fun () ->
            let p = parsed [ "--webhook"; "github"; "--webhook"; "shopify" ]
            Expect.equal (Cli.valuesOf webhook p) [ "github"; "shopify" ] "both, in order"
            Expect.isTrue (Cli.isSet webhook p) "and it counts as given"

        testCase "a repeatable option given once is one value, and given none is empty" <| fun () ->
            Expect.equal (Cli.valuesOf webhook (parsed [ "--webhook"; "github" ])) [ "github" ] "one"
            Expect.equal (Cli.valuesOf webhook (parsed [])) [] "none"
            Expect.isFalse (Cli.isSet webhook (parsed [])) "and does not count as given"

        testCase "an option that takes one value reads back as a list of at most one" <| fun () ->
            // Both readers answer for any option, off ONE parse, so a caller cannot pick the
            // reader that disagrees with the declaration.
            Expect.equal (Cli.valuesOf auth (parsed [ "--auth"; "localhost" ])) [ "localhost" ] "the one given"
            Expect.equal (Cli.valuesOf auth (parsed [])) [] "or none"

        testCase "a repeatable option's last value is what valueOf answers" <| fun () ->
            let p = parsed [ "--webhook"; "github"; "--webhook"; "shopify" ]
            Expect.equal (Cli.valueOf webhook p) (Some "shopify") "the last of them"

        // The five refusals, one per way a command line can be wrong. Each was accepted
        // silently before, which is the defect: an ignored option is indistinguishable from
        // one the operator never wrote.
        testCase "an unknown option is refused, and named" <| fun () ->
            let message = refused [ "--auht"; "localhost" ]
            Expect.isTrue (message.Contains "--auht") "says which option"
            Expect.isTrue (message.Contains "yession-manager") "and which bin"

        testCase "an option missing its value is refused" <| fun () ->
            (refused [ "--auth" ]) |> ignore

        testCase "a bare word is refused: no bin here takes a positional" <| fun () ->
            (refused [ "localhost" ]) |> ignore

        testCase "a value given to a switch is refused" <| fun () ->
            (refused [ "--version=1.2.3" ]) |> ignore

        testCase "an option that takes one value is refused a second" <| fun () ->
            // Node keeps the LAST of a repeat and says nothing, so `--auth localhost --auth
            // none` would have run as deny-everything with both spellings on the line. The
            // declaration is what makes the repeat visible; this is it being believed.
            let message = refused [ "--auth"; "localhost"; "--auth"; "none" ]
            Expect.isTrue (message.Contains "--auth") "says which option"
            Expect.isTrue (message.Contains "2 times") "and how many times it was given"

        testCase "every refusal carries the usage, so the answer is in the failure" <| fun () ->
            // The point of the usage being HERE rather than in a separate --help run: an
            // operator who got it wrong is already looking at the terminal.
            for args in [ [ "--auht"; "x" ]; [ "--auth" ]; [ "localhost" ] ] do
                let message = refused args
                Expect.isTrue (message.Contains "usage: yession-manager") "names the bin"
                Expect.isTrue (message.Contains "--auth <rule>") "and lists the options with their placeholders"

        testCase "the usage lists every declared option, and the two every bin answers" <| fun () ->
            let text = Cli.usage spec
            for expected in [ "--auth <rule>"; "--secrets <mode>"; "--webhook <name>..."; "-v, --version"; "-h, --help" ] do
                Expect.isTrue (text.Contains expected) (sprintf "usage mentions %s" expected)

        // `--port`, resolved beside the port it configures. `0` is the case worth pinning:
        // it is the one place an unpredictable address is deliberate, and refusing it broke
        // every smoke boot at once — a bin nothing could start, discovered by CI.
        testCase "a port argument resolves, and 0 asks the OS for one" <| fun () ->
            Expect.equal (ProcessManager.ManagerPort.ofName None) (Ok ProcessManager.ManagerPort.Default) "absent = the default"
            Expect.equal (ProcessManager.ManagerPort.ofName (Some "9000")) (Ok 9000) "a port"
            Expect.equal (ProcessManager.ManagerPort.ofName (Some "0")) (Ok 0) "and 0, which the OS answers"

        testCase "a port argument that is not a port number is refused, not defaulted" <| fun () ->
            // Refused because the alternative reaches `listen` as NaN, which BINDS — on a
            // random port, reporting itself as a Manager answering somewhere nobody was told.
            for bad in [ "banana"; ""; "65536"; "-1"; "80.5" ] do
                match ProcessManager.ManagerPort.ofName (Some bad) with
                | Error message -> Expect.isTrue (message.Contains bad) (sprintf "names %A back" bad)
                | Ok port -> failwithf "expected %A to be refused, got port %d" bad port

        // `--check`: what the report SAYS, over values a boot has already resolved. The
        // rendering is the half worth pinning — the resolution is every other case in this
        // file, and a report that cannot be read is a report nobody believes.
        testCase "the report says what every setting came to" <| fun () ->
            let report : ManagerCli.Report =
                { Version = "1.2.3"
                  TrustRule = "trusted-headers"
                  Secrets = "durable"
                  Port = 8321
                  DataDir = "/srv/yession"
                  DefaultSession = "local-session"
                  IdleTimeout = "00:30:00"
                  Spawn = "/nix/store/x/bin/yession-session"
                  Addressing = [ "addressing", "fronted"; "  manager", "https://host.ts.net:8321" ]
                  Webhooks = [ "github"; "shop@1=x-shop-hmac:base64" ]
                  Inherited = [ "YESSION_MANAGER_URL"; "YESSION_PROXY_PORT" ] }
            let text = ManagerCli.Report.render report
            for expected in
                [ "yession-manager 1.2.3"; "trusted-headers"; "8321"; "/srv/yession"; "00:30:00"
                  "https://host.ts.net:8321"; "shop@1=x-shop-hmac:base64"; "YESSION_PROXY_PORT" ] do
                Expect.isTrue (text.Contains expected) (sprintf "the report says %s" expected)

        testCase "a setting that is empty says so in words, never as a blank" <| fun () ->
            // A label with nothing after it reads as a rendering fault, which is the wrong
            // thing for a report whose whole job is to be believed. Asserted as the PROMISE
            // — every labelled line says something — rather than against the column the
            // labels happen to be padded to, which is a layout a redesign is free to move.
            let report : ManagerCli.Report =
                { Version = "1.2.3"; TrustRule = "none"; Secrets = "ephemeral"; Port = 0
                  DataDir = ".yession"; DefaultSession = "local-session"
                  IdleTimeout = "never (sessions are not reaped)"; Spawn = "node app/SessionMain.js"
                  Addressing = [ "addressing", "loopback" ]; Webhooks = []; Inherited = [] }
            let text = ManagerCli.Report.render report
            for label in [ "webhooks"; "inherited" ] do
                match text.Split '\n' |> Array.tryFind (fun l -> l.Trim().StartsWith label) with
                | None -> failwithf "the report has no %s line at all" label
                | Some found ->
                    let after = found.Trim().Substring(label.Length).Trim ()
                    Expect.notEqual after "" (sprintf "%s says something, even when it is empty" label)

        testCase "an idle window is reported in the vocabulary the option accepts" <| fun () ->
            // `parse (describe w) = w`, because a report shows what an operator could type
            // back. Rendered as a TimeSpan it read `1800000` — a number whose unit a reader
            // has to guess, and would guess wrong.
            for text in [ "90s"; "30m"; "2h"; "45s" ] do
                let window =
                    match Yession.Manager.IdleWindow.parse text with
                    | Ok w -> w
                    | Error e -> failwithf "%s did not parse: %s" text e
                Expect.equal (Yession.Manager.IdleWindow.describe window) text (sprintf "%s round-trips" text)
            Expect.equal (Yession.Manager.IdleWindow.describe None) "never" "and absence has a word"

        testCase "--check is a switch the Manager declares" <| fun () ->
            Expect.isTrue ((Cli.usage ManagerCli.spec).Contains "--check") "so --help names it"
            Expect.isTrue (Cli.isSet ManagerCli.checkOption (Cli.parse ManagerCli.spec [| "--check" |] |> function Ok p -> p | Error e -> failwith e)) "and it parses"

        // Retirements: a setting that MOVED, and an environment that has not caught up.
        testCase "a retired variable the environment still sets is found, and named with its option" <| fun () ->
            let retirements = [ { Retirements.Was = "YESSION_PORT"; Retirements.Now = "--port" } ]
            let set = dict [ "YESSION_PORT", "8321" ]
            let lookup name = if set.ContainsKey name then set.[name] else ""
            let found = Retirements.found retirements lookup
            Expect.equal (found |> List.map (fun r -> r.Was)) [ "YESSION_PORT" ] "the variable"
            let message = Retirements.complaint found
            Expect.isTrue (message.Contains "YESSION_PORT") "says which variable"
            Expect.isTrue (message.Contains "--port") "and what to write instead"

        testCase "a retirement the environment does not set is not found" <| fun () ->
            // Unset and empty are the same thing to a bin, and `Interop.envOr name ""` cannot
            // tell them apart — so an empty value must not refuse a boot that set nothing.
            let retirements = [ { Retirements.Was = "YESSION_PORT"; Retirements.Now = "--port" } ]
            Expect.equal (Retirements.found retirements (fun _ -> "")) [] "unset"
            Expect.equal (Retirements.found retirements (fun _ -> "   ")) [] "or blank"

        testCase "every retirement still set is reported at once" <| fun () ->
            // One boot, one report. A deployment that moved one of these moved all of them at
            // the same time, and learning about the next only after fixing this one is a boot
            // cycle spent per variable — which is exactly how the renames that motivated this
            // were found in the first place.
            let found = Retirements.found Retirements.manager (fun _ -> "x")
            Expect.equal (List.length found) (List.length Retirements.manager) "all of them"
            let message = Retirements.complaint found
            for r in Retirements.manager do
                Expect.isTrue (message.Contains r.Was) (sprintf "names %s" r.Was)
                Expect.isTrue (message.Contains r.Now) (sprintf "and its option %s" r.Now)

        testCase "every option a retirement points at is one the Manager declares" <| fun () ->
            // Against the bin's OWN spec, not a copy of it. This case first went red for the
            // wrong reason: it listed the options by hand, so the first retirement added
            // after it was written broke a check that was supposed to be watching for
            // exactly that. A list you have to keep in step is not a check, it is a second
            // list — which is why `ManagerCli.spec` is a value.
            let usage = Cli.usage ManagerCli.spec
            for r in Retirements.manager do
                Expect.isTrue (usage.Contains (r.Now + " <")) (sprintf "%s is a declared option" r.Now)

        testCase "the detector sees an assignment and lets a mention be" <| fun () ->
            let retirements = [ { Retirements.Was = "YESSION_PORT"; Retirements.Now = "--port" } ]
            let assigned = Retirements.assignedIn retirements
            Expect.equal (assigned "run: YESSION_PORT=0 ./yession-manager" |> List.length) 1 "a shell assignment"
            Expect.equal (assigned "env:\n  YESSION_PORT: 0" |> List.length) 1 "a yaml one"
            Expect.equal (assigned "# YESSION_PORT was retired; use --port") [] "prose naming it is not setting it"

        testCase "no workflow file sets a variable the bins no longer read" <| fun () ->
            let dir = ".github/workflows"
            let offences =
                readDir nodeFs dir
                |> Array.toList
                |> List.filter (fun name -> name.EndsWith ".yml" || name.EndsWith ".yaml")
                |> List.collect (fun name ->
                    Retirements.assignedIn Retirements.manager (readText nodeFs (dir + "/" + name))
                    |> List.map (fun r -> sprintf "%s sets %s (now %s)" name r.Was r.Now))
            Expect.equal offences [] "a workflow that sets one of these fails the bin it starts"

        testCase "a bin with no options of its own still answers version and help" <| fun () ->
            // `yession-session` and `yession-serial` take everything from the environment.
            let bare = Cli.spec "yession-session" []
            match Cli.parse bare [| "--version" |] with
            | Ok p -> Expect.isTrue (Cli.isSet Cli.version p) "version still parses"
            | Error e -> failwithf "expected a parse, got: %s" e
            match Cli.parse bare [| "--auth"; "localhost" |] with
            | Error _ -> ()
            | Ok _ -> failwith "a bin that declares no options must refuse one"
    ]
