module Yession.Tests.Main

open Fable.Pyxpecto

#if !FABLE_COMPILER_JAVASCRIPT && !FABLE_COMPILER_TYPESCRIPT
let inline (!!) (any: 'a) = any
#endif
#if FABLE_COMPILER_JAVASCRIPT || FABLE_COMPILER_TYPESCRIPT
open Fable.Core.JsInterop
#endif

/// The single test suite for the whole repo: domain/process model + protocol unit tests
/// and the real WebRTC end-to-end test, all compiled by Fable and run on Node by Pyxpecto.
let all =
    testList "Yession" [
        Domain.tests
        SessionProcess.tests
        Sync.tests
        Agent.tests
        E2E.tests
        Client.tests
        Phase2.tests
        Acceptance.tests
    ]

[<EntryPoint>]
let main argv = !! Pyxpecto.runTests [||] all
