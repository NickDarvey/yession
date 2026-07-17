// Watchdog runner for the Pyxpecto test suite (the F# replacement for run.mjs). Runs the
// Fable-compiled tests on Node with a hard timeout so a hung WebRTC connection (or any
// hang) can never block CI. Forwards Pyxpecto's exit code (0 = all passed); exits 124 on
// timeout. Same BCL-only `System.Diagnostics.Process` shape as scripts/build.fsx.
//
//     dotnet fsi scripts/run-tests.fsx <target-js> [timeoutMs]

open System
open System.Diagnostics

let args = fsi.CommandLineArgs

let target =
    match Array.tryItem 1 args with
    | Some t -> t
    | None -> failwith "usage: run-tests.fsx <target-js> [timeoutMs]"

let timeoutMs =
    match Array.tryItem 2 args with
    | Some ms -> int ms
    | None -> 30000

// Inherit stdio (no redirects): the child's test output streams straight to the console,
// and the parent env — including YESSION_TEST_TIER — passes through unchanged.
let psi = ProcessStartInfo "node"
psi.ArgumentList.Add target
psi.UseShellExecute <- false

let child = Process.Start psi

if child.WaitForExit timeoutMs then
    exit child.ExitCode
else
    eprintfn "runner: timed out after %dms — killing" timeoutMs
    child.Kill true
    exit 124
