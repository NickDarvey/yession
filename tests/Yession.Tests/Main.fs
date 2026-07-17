module Yession.Tests.Main

open Fable.Pyxpecto

#if !FABLE_COMPILER_JAVASCRIPT && !FABLE_COMPILER_TYPESCRIPT
let inline (!!) (any: 'a) = any
#endif
#if FABLE_COMPILER_JAVASCRIPT || FABLE_COMPILER_TYPESCRIPT
open Fable.Core.JsInterop
#endif

/// The repo's test suite. Pyxpecto is multi-runtime, so the entry point serves two targets:
///   * Fable → JS on Node (`mise run test` / `verify`): the domain/process model + protocol
///     unit tests and the real WebRTC end-to-end test — the same JavaScript the product runs.
///   * .NET CLR (`dotnet run --project tests/Yession.Tests`): the real-browser E2E, which needs
///     the Microsoft.Playwright driver and so lives on the CLR, not under Fable.
/// The Node suites cannot run on the CLR (Fable interop stubs are JS-only), so each target
/// selects the suites it can actually run; the other side is a visible one-case placeholder.
let all =
#if FABLE_COMPILER
    testList "Yession" [
        Domain.tests
        SessionProcess.tests
        Sync.tests
        Agent.tests
        Tag.verify E2E.tests
        Tag.verify Client.tests
        Phase2.tests
        DockerIntegration.tests
        Phase3.tests
        EventsHttp.tests
        Phase4.tests
        Properties.tests
        Acceptance.tests
        InMemory.tests
        Browser.tests
    ]
#else
    testList "Yession" [
        Browser.tests
    ]
#endif

[<EntryPoint>]
let main argv = !! Pyxpecto.runTests [||] all
