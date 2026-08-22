module Yession.Tests.SrtIntegration

// The srt backend, driven for real (docs/plans, PR 3 of the sandboxing plan). Everything
// here asserts DENIAL: a sandbox that runs commands is easy to build and proves nothing —
// what has to hold is that a command cannot read outside its policy, cannot write outside
// it, and cannot reach a domain the policy never named. The suite sits under
// `Tag.needs [Srt]`, and `check` probes for a WORKING bubblewrap (installed is not the
// same as permitted — user namespaces can be off) before declaring the capability, so a
// box without one reports a skip instead of a wall of failures.
//
// The one non-denial test is latency: srt was chosen over a container per command because
// it starts in milliseconds, and a regression there is a regression in the reason.

open Fable.Core
open Fable.Core.JsInterop
open Fable.Pyxpecto
open Yession.Domain
open Yession.Host
open Yession.Tests.Support

// --- Node helpers: host-side fixtures the sandbox is then pointed at ----------------------

let private nodeFs : obj = importAll "node:fs"
let private nodeOs : obj = importAll "node:os"

[<Emit("$0.mkdtempSync($1.tmpdir() + '/yession-srt-')")>]
let private mkdtemp (fs: obj) (os: obj) : string = jsNative

[<Emit("$0.writeFileSync($1, $2)")>]
let private writeFile (fs: obj) (path: string) (content: string) : unit = jsNative

[<Emit("$0.existsSync($1)")>]
let private exists (fs: obj) (path: string) : bool = jsNative

[<Emit("process.env.HOME || ''")>]
let private hostHome () : string = jsNative

[<Emit("process.execPath")>]
let private nodePath () : string = jsNative

[<Emit("Date.now()")>]
let private nowMs () : float = jsNative

// --- The sandbox under test ---------------------------------------------------------------

/// A policy whose only writable place is `workspace`, whose only re-allowed read outside
/// the host's runtime is `workspace`, and which names no domain at all — the fail-closed
/// shape a session gets when nobody configured egress.
let private policyIn (workspace: string) (domains: string list) : SandboxPolicy =
    { ReadPaths = [ workspace ]
      WritePaths = [ workspace ]
      AllowedDomains = Some domains
      Env = Sandboxes.hostBaseline (Sandboxes.ambientEnv ())
      WorkingDirectory = Some workspace
      Filesystem = Confined }

/// How this box confines, as the run's environment configures it — the same parse the
/// Session Process does at boot, so the suite exercises the deployed shape.
let private srtTools () =
    match Sandboxes.SrtSandbox.toolsFrom (Sandboxes.ambientEnv ()) with
    | Ok tools -> tools
    | Error reason -> failwithf "srt tools: %s" reason

let private startSandbox (policy: SandboxPolicy) : Async<Sandbox> =
    async {
        let create = Sandboxes.SrtSandbox.create (srtTools ())
        match! create policy with
        | Error reason -> return failwithf "srt sandbox failed: %s" reason
        | Ok sandbox -> return sandbox
    }

/// `sh -c` inside the sandbox: the denial cases are all shell one-liners, and the shell's
/// exit code is the answer.
let private shell (sandbox: Sandbox) (script: string) : Async<SandboxRun * string * string> =
    runInSandbox sandbox "/bin/sh" [ "-c"; script ] Map.empty None

let private exitCode (run: SandboxRun) =
    match run with
    | SandboxExited code -> code
    | SandboxRunFailed reason -> failwithf "the sandboxed process did not run: %s" reason

// --- The agent CLI's spawner, driven directly ----------------------------------------------

/// Write to the proxy's stdin, close it, and resolve with (stdout, exit code) — the exact
/// shape the SDK drives `spawnClaudeCodeProcess`'s result through.
[<Emit("""(function (spawner, command, args, cwd, env, stdin) { return (
(new Promise((resolve) => {
  const child = spawner({ command: command, args: args, cwd: cwd, env: Object.fromEntries(env) })
  let out = ''
  child.stdout.on('data', (d) => { out += String(d) })
  child.on('exit', (code) => resolve([out, code == null ? -1 : code]))
  child.on('error', (e) => resolve([String((e && e.message) || e), -1]))
  child.stdin.write(stdin)
  child.stdin.end()
}))
) })($0, $1, $2, $3, $4, $5)""")>]
let private driveSpawner
    (spawner: obj)
    (command: string)
    (args: string array)
    (cwd: string)
    (env: (string * string) array)
    (stdin: string)
    : JS.Promise<string * int> = jsNative

// --- The suite ------------------------------------------------------------------------------

let tests =
    Tag.needs "Srt integration" [ Tag.Srt ] (fun () ->
        testList "Srt integration" [

            testCaseAsync "a command runs confined, writes its workspace, and streams its output" (async {
                let workspace = mkdtemp nodeFs nodeOs
                let! sandbox = startSandbox (policyIn workspace [])
                let! run, out, _ = shell sandbox "echo confined > marker; cat marker"
                Expect.equal (exitCode run) 0 "the command ran"
                Expect.isTrue (out.Contains "confined") "its stdout reached the caller"
                Expect.isTrue (exists nodeFs (workspace + "/marker")) "the workspace write landed on the host"
                do! sandbox.Dispose ()
            })

            testCaseAsync "a write outside the policy's paths is refused" (async {
                let workspace = mkdtemp nodeFs nodeOs
                let outside = mkdtemp nodeFs nodeOs
                let! sandbox = startSandbox (policyIn workspace [])
                let! run, _, _ = shell sandbox (sprintf "echo escaped > %s/escaped" outside)
                Expect.notEqual (exitCode run) 0 "writing outside the allowed paths fails"
                Expect.isFalse (exists nodeFs (outside + "/escaped")) "and nothing was written"
                do! sandbox.Dispose ()
            })

            testCaseAsync "a read outside the policy's paths is refused" (async {
                // The half the write case above does not cover, and the one that was missing:
                // reads used to be denied only inside the operator's home, so anything else
                // nobody had thought to name — another session's data directory, a checkout
                // this session was never given — was readable by every command.
                let outside = mkdtemp nodeFs nodeOs
                writeFile nodeFs (outside + "/secret") "not-yours"
                let workspace = mkdtemp nodeFs nodeOs
                let! sandbox = startSandbox (policyIn workspace [])
                let! run, out, _ = shell sandbox (sprintf "cat %s/secret" outside)
                Expect.notEqual (exitCode run) 0 "reading a path the policy never named fails"
                Expect.isFalse (out.Contains "not-yours") "and its contents never reach stdout"
                do! sandbox.Dispose ()
            })

            testCaseAsync "the host runtime stays readable, or no command could run at all" (async {
                // The other half of denying every read: an interpreter the sandbox cannot read
                // is a sandbox that runs nothing. This is the case that goes red when the
                // allow-back list stops matching where this box keeps its runtime.
                let workspace = mkdtemp nodeFs nodeOs
                let! sandbox = startSandbox (policyIn workspace [])
                let! run, out, _ =
                    runInSandbox sandbox (nodePath ()) [ "-e"; "process.stdout.write('ran')" ] Map.empty None
                Expect.equal (exitCode run) 0 "the interpreter this very process runs on is reachable inside"
                Expect.isTrue (out.Contains "ran") "and it produced its output"
                do! sandbox.Dispose ()
            })

            testCaseAsync "the agent's own sandbox keeps the runtime that starts the CLI" (async {
                // The AgentSandbox names ONE path — its per-session scratch HOME — so it is
                // the narrowest read scope in the product, and the place a missing allow-back
                // would surface as a session that cannot start a turn at all.
                let home = mkdtemp nodeFs nodeOs
                let ambient = Sandboxes.ambientEnv ()
                let policy =
                    Sandboxes.AgentSandbox.policyFor ambient home (Sandboxes.AgentSandbox.envFor ambient home None)
                let! sandbox = startSandbox policy
                let! run, out, _ =
                    runInSandbox sandbox (nodePath ()) [ "-e"; "process.stdout.write('ran')" ] Map.empty (Some home)
                Expect.equal (exitCode run) 0 "the interpreter the CLI runs on is readable under the agent's policy"
                Expect.isTrue (out.Contains "ran") "and it produced its output"
                do! sandbox.Dispose ()
            })

            testCaseAsync "a read of the operator's home is refused, while the workspace inside it is not" (async {
                // The home is denied like everything else now; a workspace under it is
                // re-allowed by name. Both halves matter: deny too little and secrets leak,
                // deny too much and a session cannot use its own workspace.
                let home = hostHome ()
                let secret = home + "/.yession-srt-probe"
                writeFile nodeFs secret "top-secret"
                let workspace = mkdtemp nodeFs nodeOs
                let! sandbox = startSandbox (policyIn workspace [])
                let! denied, out, _ = shell sandbox (sprintf "cat %s" secret)
                Expect.notEqual (exitCode denied) 0 "the home-directory read fails"
                Expect.isFalse (out.Contains "top-secret") "and the secret never reaches stdout"
                let! allowed, _, _ = shell sandbox "echo ok > in-workspace; cat in-workspace"
                Expect.equal (exitCode allowed) 0 "the workspace stays readable and writable"
                do! sandbox.Dispose ()
            })

            testCaseAsync "egress to a domain the policy never named is refused" (async {
                // Deterministic without reaching the internet: the sandbox's network namespace
                // is unshared, so an unlisted host cannot be connected to whether or not this
                // box has a route to it.
                let workspace = mkdtemp nodeFs nodeOs
                let! sandbox = startSandbox (policyIn workspace [ "api.anthropic.com" ])
                let! run, _, _ =
                    runInSandbox
                        sandbox
                        (nodePath ())
                        [ "-e"; "fetch('https://example.com').then(() => process.exit(0), () => process.exit(9))" ]
                        Map.empty
                        None
                Expect.notEqual (exitCode run) 0 "an unlisted domain is unreachable"
                do! sandbox.Dispose ()
            })

            testCaseAsync "a confined spawn stays in the millisecond range" (async {
                // The reason srt was chosen over a container per command. The bound is loose
                // (a loaded CI runner is not a benchmark rig) but a regression to container
                // start-up — seconds, not milliseconds — cannot hide under it.
                let workspace = mkdtemp nodeFs nodeOs
                let! sandbox = startSandbox (policyIn workspace [])
                let! _ = shell sandbox "true"          // the manager is up; measure a spawn, not a start
                let started = nowMs ()
                let! run, _, _ = shell sandbox "true"
                let elapsed = nowMs () - started
                Expect.equal (exitCode run) 0 "the probe ran"
                Expect.isTrue (elapsed < 3000.0) (sprintf "a confined spawn took %.0fms, which is container territory" elapsed)
                do! sandbox.Dispose ()
            })

            testCaseAsync "the agent spawner hands the SDK a live process before srt has wrapped it" (async {
                // The SDK's seam is synchronous and srt's wrap is not, so what the SDK gets
                // back is a stand-in that joins the real child later. This drives it exactly
                // as the SDK does — write stdin, read stdout, wait for exit — through a
                // command that only answers if every one of those was plumbed through.
                let workspace = mkdtemp nodeFs nodeOs
                let policy = policyIn workspace []
                let spawner =
                    Sandboxes.AgentSandbox.srtClaudeSpawner (Sandboxes.SrtSandbox.wrapperFor (srtTools ()) policy)
                let! out, code =
                    driveSpawner spawner "/bin/cat" [||] workspace (Map.toArray policy.Env) "round-trip"
                    |> Interop.awaitPromise
                Expect.equal code 0 "the confined process exited cleanly"
                Expect.equal out "round-trip" "stdin reached it and its stdout came back"
            })

            // srt probes ripgrep by forking `which` under a one-second timeout, and reports
            // every way that fork can fail as `ripgrep (<path>) not found`. Taking a PATH
            // away is the deterministic member of that family — the others (a box too busy
            // to hand out a fork, EMFILE, ENOMEM) arrive by luck, which is how this cost a
            // whole tier twice in four runs while the file it named sat there, executable.
            testCaseAsync "a probe that could not run is not an answer, and is not remembered" (async {
                let workspace = mkdtemp nodeFs nodeOs
                // A manager is already up by now, and `initialize` returns early once srt
                // has one — probe included. So the question can only be asked of a process
                // that has none, which is what forgetting both halves leaves behind.
                do! Sandboxes.SrtSandbox.forgetManager ()
                let! refused =
                    Support.withEnv [ "PATH", Some "" ] (fun () ->
                        Sandboxes.SrtSandbox.create (srtTools ()) (policyIn workspace []))
                match refused with
                | Ok _ -> failwith "srt started with no `which` to probe with, which it cannot do"
                | Error reason ->
                    Expect.isTrue
                        (reason.Contains "executable in this process")
                        (sprintf "the refusal contradicts srt's `not found` rather than repeating it: %s" reason)
                // And the box is itself again: the next sandbox starts, because nothing was
                // remembered from a question that never got an answer.
                let! sandbox = startSandbox (policyIn workspace [])
                let! run, _, _ = shell sandbox "true"
                Expect.equal (exitCode run) 0 "the sandbox after the refusal runs commands"
                do! sandbox.Dispose ()
            })
        ])
