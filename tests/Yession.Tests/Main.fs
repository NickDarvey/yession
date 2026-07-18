module Yession.Tests.Main

open Fable.Pyxpecto

#if !FABLE_COMPILER_JAVASCRIPT && !FABLE_COMPILER_TYPESCRIPT
let inline (!!) (any: 'a) = any
#endif
#if FABLE_COMPILER_JAVASCRIPT || FABLE_COMPILER_TYPESCRIPT
open Fable.Core.JsInterop
#endif

/// The repo's one test suite, shared by both runtimes Pyxpecto runs on. Each suite declares
/// what it NEEDS (`Tag.needs`), and the harness runs it only where those needs are met — so
/// the SAME list serves both targets with no `#if` here:
///   * Fable → JS on Node (`mise run test` / `verify`): the model/protocol/WebRTC suites — the
///     JavaScript the product actually runs.
///   * .NET CLR (`dotnet run --project tests/Yession.Tests`): the real-browser E2E, driven
///     through the Microsoft.Playwright driver.
/// A suite with no needs runs on Node (the product runtime); `[Ports]`/`[Docker]`/`[LiveAgent]`
/// add the verify tier and its env; `[Browser]` pins the .NET CLR. Each need fixes a runtime,
/// so nothing runs twice; whatever this run cannot host shows as one visible skip.
let all =
    testList "Yession" [
        Tag.needs "Domain" [] (fun () -> Domain.tests)
        Tag.needs "SessionProcess" [] (fun () -> SessionProcess.tests)
        Tag.needs "Sync" [] (fun () -> Sync.tests)
        Tag.needs "Agent" [] (fun () -> Agent.tests)
        Tag.needs "WebRTC E2E" [ Tag.Ports ] (fun () -> E2E.tests)
        Tag.needs "Client shell E2E" [ Tag.Ports ] (fun () -> Client.tests)
        Tag.needs "Phase2" [] (fun () -> Phase2.tests)
        Tag.needs "Docker integration" [] (fun () -> DockerIntegration.tests)
        Tag.needs "Phase3" [] (fun () -> Phase3.tests)
        Tag.needs "EventsHttp" [] (fun () -> EventsHttp.tests)
        Tag.needs "Phase4" [] (fun () -> Phase4.tests)
        Tag.needs "Properties" [] (fun () -> Properties.tests)
        Tag.needs "Acceptance" [] (fun () -> Acceptance.tests)
        Tag.needs "InMemory" [] (fun () -> InMemory.tests)
        Tag.needs "Browser E2E" [ Tag.Browser ] (fun () -> Browser.tests)
    ]

[<EntryPoint>]
let main argv = !! Pyxpecto.runTests [||] all
