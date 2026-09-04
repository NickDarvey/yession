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

open Fable.Pyxpecto
open Yession.Host

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
