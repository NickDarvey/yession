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
///   * Fable → JS on Node (`check` / `verify`): the model/protocol/WebRTC suites — the
///     JavaScript the product actually runs.
///   * .NET CLR (`dotnet run --project tests/Yession.Tests`): the real-browser E2E, driven
///     through the Microsoft.Playwright driver.
/// A suite with no needs runs on Node (the product runtime). `[Browser]` pins the .NET CLR;
/// every other need is a capability the run declares via `YESSION_TEST_CAPS` (e.g.
/// `check Browser Native`). `Native` marks suites that spawn the real Session
/// Process (which loads the node-datachannel addon), so they skip — rather than error — where
/// that addon is absent. Whatever this run cannot host or satisfy shows as one visible skip.
let all =
    testList "Yession" [
        Tag.needs "Domain" [] (fun () -> Domain.tests)
        Tag.needs "Routes" [] (fun () -> Routes.tests)
        Tag.needs "Idle reaping" [] (fun () -> Reaper.tests)
        Tag.needs "Secrets" [] (fun () -> Secrets.tests)
        Tag.needs "Connections" [] (fun () -> Connections.tests)
        Tag.needs "SessionProcess" [] (fun () -> SessionProcess.tests)
        Tag.needs "Sync" [] (fun () -> Sync.tests)
        Tag.needs "Terminals" [] (fun () -> Terminals.tests)
        Tag.needs "Timeline" [] (fun () -> Timeline.tests)
        Tag.needs "Editor" [] (fun () -> Editor.tests)
        Tag.needs "Agent" [] (fun () -> Agent.tests)
        Tag.needs "Version" [] (fun () -> Version.tests)
        Tag.needs "Telemetry" [] (fun () -> Telemetry.tests)
        Tag.needs "Telemetry E2E" [] (fun () -> TelemetryE2E.tests)
        Tag.needs "WebRTC E2E" [ Tag.Ports; Tag.Native ] (fun () -> E2E.tests)
        Tag.needs "Client shell E2E" [ Tag.Ports; Tag.Native ] (fun () -> Client.tests)
        Tag.needs "Phase2" [] (fun () -> Phase2.tests)
        Tag.needs "Docker integration" [ Tag.Docker ] (fun () -> DockerIntegration.tests)
        Tag.needs "Srt integration" [ Tag.Srt ] (fun () -> SrtIntegration.tests)
        Tag.needs "Pty integration" [ Tag.Pty ] (fun () -> PtyIntegration.tests)
        Tag.needs "Phase3" [] (fun () -> Phase3.tests)
        Tag.needs "EventsHttp" [] (fun () -> EventsHttp.tests)
        Tag.needs "Transport resilience" [] (fun () -> Resilience.tests)
        Tag.needs "Oidc" [] (fun () -> Oidc.tests)
        Tag.needs "Phase4" [] (fun () -> Phase4.tests)
        Tag.needs "Properties" [] (fun () -> Properties.tests)
        Tag.needs "Acceptance" [] (fun () -> Acceptance.tests)
        Tag.needs "InMemory" [] (fun () -> InMemory.tests)
        // What the Nix derivations may see of this repo. `Nix` because it evaluates the
        // derivation for real; it is the only check of a contract every CI route is blind to
        // (they build flake source copies, which git already filtered).
        Tag.needs "Nix build source" [ Tag.Nix ] (fun () -> NixSource.tests)
        // The rich editor rendering E2E stands alone: it needs a browser but NOT the native
        // WebRTC host, so it runs wherever Chromium exists ([Browser]). The full two-peer
        // convergence/persistence E2E spawns the real Session Process, so it also needs Native.
        Tag.needs "Editor rendering (browser)" [ Tag.Browser ] (fun () -> Browser.editorTests)
        Tag.needs "Browser E2E" [ Tag.Browser; Tag.Native ] (fun () -> Browser.tests)
        // A session served under a path, driven at its PUBLIC address through a
        // path-preserving proxy: the only check that `<base href>` resolution works in a
        // real browser rather than in reasoning about one (docs/plans/10).
        Tag.needs "Path-mounted session (browser)" [ Tag.Browser; Tag.Native ] (fun () -> Browser.mountedTests)
    ]

[<EntryPoint>]
let main argv = !! Pyxpecto.runTests [||] all
