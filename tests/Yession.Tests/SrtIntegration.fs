module Yession.Tests.SrtIntegration

// The srt backend, driven for real (PR 3 of the sandboxing plan). Everything
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
open Yession.Domain.Sandboxes
open Yession.Host
open Yession.Tests.Support

// --- Node helpers: host-side fixtures the sandbox is then pointed at ----------------------

let private nodeFs : obj = importAll "node:fs"
let private nodeOs : obj = importAll "node:os"
let private nodeNet : obj = importAll "node:net"

[<Emit("$0.mkdtempSync($1.tmpdir() + '/yession-srt-')")>]
let private mkdtemp (fs: obj) (os: obj) : string = jsNative

[<Emit("$0.mkdtempSync($1)")>]
let private mkdtempAt (fs: obj) (prefix: string) : string = jsNative

[<Emit("$0.writeFileSync($1, $2)")>]
let private writeFile (fs: obj) (path: string) (content: string) : unit = jsNative

[<Emit("$0.existsSync($1)")>]
let private exists (fs: obj) (path: string) : bool = jsNative

[<Emit("$0.symlinkSync($1, $2)")>]
let private symlink (fs: obj) (target: string) (path: string) : unit = jsNative

[<Emit("process.env.HOME || ''")>]
let private hostHome () : string = jsNative

[<Emit("process.execPath")>]
let private nodePath () : string = jsNative

[<Emit("process.platform")>]
let private platform () : string = jsNative

[<Emit("Date.now()")>]
let private nowMs () : float = jsNative

/// A unix socket with something listening on it, and a thunk that closes it.
///
/// A LISTENER, rather than probing an empty path: a refused connect and a denied connect are
/// both failures, and only a successful one says the grant reached the kernel.
[<Emit("""(function (net, path) {
  const server = net.createServer((c) => { c.end('ok') })
  server.listen(path)
  return () => { try { server.close() } catch {} }
})($0, $1)""")>]
let private listenOnImpl (net: obj) (path: string) : (unit -> unit) = jsNative

let private listenOn (path: string) : (unit -> unit) = listenOnImpl nodeNet path

// --- The sandbox under test ---------------------------------------------------------------

/// A policy whose only writable place is `workspace`, whose only re-allowed read outside
/// the host's runtime is `workspace`, and which names no domain at all — the fail-closed
/// shape a session gets when nobody configured egress.
let private policyIn (workspace: string) (domains: string list) : SandboxPolicy =
    { ReadPaths = [ workspace ]
      WritePaths = [ workspace ]
      AllowedDomains = Some domains
      Sockets = []
      Realisation = []
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

            // #335 made a socket its own axis on the policy. This is that fix one level up,
            // and the same fault wearing a different hat: srt reads the socket allowance from
            // the config the MANAGER was initialized with, not the one a spawn carries, so a
            // sandbox that names a socket the FIRST sandbox of the session did not would hold
            // a grant that exists only in its own config object.
            //
            // Deliberately the second sandbox, and deliberately a socket outside the
            // workspace: inside it, the workspace's own read/write grant would satisfy the
            // connect and this would pass with the union deleted.
            // Only where the grant is PATH-SCOPED, which is macOS. On Linux srt filters unix
            // sockets with seccomp-bpf, which cannot read a socket path out of user-space
            // memory, so the wrapper takes `allowAllUnixSockets` and ignores the path list
            // entirely — see docs/GAPS.md. Skipped rather than asserted either way: the
            // invariant is real and this platform cannot express it, which is what a visible
            // skip says and a quiet pass does not.
            (if platform () <> "darwin" then
                ptestCase "a socket named by a later sandbox is one it can still connect to (macOS only: Linux cannot scope a socket grant to a path)" (fun () -> ())
             else
             testCaseAsync "a socket named by a later sandbox is one it can still connect to" (async {
                let workspace = mkdtemp nodeFs nodeOs
                // Canonical, because `mkdtemp` hands back `/var/folders/...` on macOS and
                // `/var` is a symlink — the exact fault #330 refuses for an operator, and it
                // bites a test that writes a path the same way.
                let elsewhere = mkdtemp nodeFs nodeOs |> Fs.canonical |> Option.get
                let socketPath = elsewhere + "/probe.sock"
                let close = listenOn socketPath

                // First, naming no socket at all — this is what initializes srt's manager.
                let! _ = startSandbox (policyIn workspace [])

                // Then one that does. The read and write halves are what `grantsFrom`
                // produces for a `Socket` leaf beside the socket itself, so this is the
                // policy a resource really becomes.
                let! second =
                    startSandbox
                        { policyIn workspace [] with
                            ReadPaths = [ workspace; socketPath ]
                            WritePaths = [ workspace; socketPath ]
                            Sockets = [ socketPath ] }

                let connect =
                    sprintf
                        "%s -e \"const n=require('node:net');const c=n.connect(%s);c.on('connect',()=>{c.end();process.exit(0)});c.on('error',e=>{console.error(e.code);process.exit(1)})\""
                        (nodePath ())
                        ("'" + socketPath + "'")
                let! run, _, err = shell second connect
                close ()
                Expect.equal (exitCode run) 0
                    (sprintf "the second sandbox reached the socket it named, said: %s" err)
            }))

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

            // macOS keeps /tmp, /etc and /var as symlinks into /private, and the kernel
            // consults DIFFERENT spellings of one path per operation: bind(2) the canonical,
            // lstat of the link node the as-written. The forked srt closes every grant over
            // its spellings and admits metadata on the symlink nodes a grant is written
            // through — narrowly, per node, never per vnode-type. These four pin that fix
            // and its narrowness; each goes red on unpatched srt (the first three) or on the
            // broad (vnode-type SYMLINK) variant (the fourth). Darwin only: on Linux /tmp is
            // a real directory and srt's socket grants are not path-scoped at all.
            (if platform () <> "darwin" then
                ptestCase "a granted path answers by both its spellings (macOS only)" (fun () -> ())
             else
             testCaseAsync "a granted path answers by both its spellings" (async {
                let workspace = mkdtempAt nodeFs "/tmp/yession-srt-spell-"
                let! sandbox = startSandbox (policyIn workspace [])
                let canonical = "/private" + workspace
                let! run, out, err = shell sandbox (sprintf "echo agreed > %s/marker; cat %s/marker" workspace canonical)
                Expect.equal (exitCode run) 0 (sprintf "wrote as-written, read canonical, said: %s" err)
                Expect.isTrue (out.Contains "agreed") "both spellings named the same file"
                do! sandbox.Dispose ()
            }))

            (if platform () <> "darwin" then
                ptestCase "a symlink node en route to a grant answers stat (macOS only)" (fun () -> ())
             else
             testCaseAsync "a symlink node en route to a grant answers stat" (async {
                // The .NET shape: named mutexes stat("/tmp/") — hardcoded, so no TMPDIR
                // redirection reaches it — before any build runs at all.
                let workspace = mkdtempAt nodeFs "/tmp/yession-srt-stat-"
                let! sandbox = startSandbox (policyIn workspace [])
                let! run, _, err = shell sandbox "stat /tmp/ > /dev/null"
                Expect.equal (exitCode run) 0 (sprintf "the link node a grant is spelled through answers, said: %s" err)
                do! sandbox.Dispose ()
            }))

            (if platform () <> "darwin" then
                ptestCase "a unix socket binds where the policy grants one (macOS only)" (fun () -> ())
             else
             testCaseAsync "a unix socket binds where the policy grants one" (async {
                // MSBuild's shape: worker nodes bind /tmp/MSBuild<pid>, spelled through the
                // symlink, in shared /tmp. Write and socket on /tmp is what a dotnet
                // resource grants; the closure is what makes the kernel's canonical check
                // and the as-written spelling agree.
                let workspace = mkdtempAt nodeFs "/tmp/yession-srt-bind-"
                let socketPath = workspace + "/probe.sock"
                let! sandbox =
                    startSandbox
                        { policyIn workspace [] with
                            ReadPaths = [ workspace; "/tmp" ]
                            WritePaths = [ workspace; "/tmp" ]
                            Sockets = [ "/tmp" ] }
                let bind =
                    sprintf
                        "%s -e \"const n=require('node:net');const s=n.createServer();s.on('error',e=>{console.error(e.code);process.exit(1)});s.listen(%s,()=>{s.close();process.exit(0)})\""
                        (nodePath ())
                        ("'" + socketPath + "'")
                let! run, _, err = shell sandbox bind
                Expect.equal (exitCode run) 0 (sprintf "the bind the policy granted succeeded, said: %s" err)
                do! sandbox.Dispose ()
            }))

            (if platform () <> "darwin" then
                ptestCase "a symlink en route to nothing granted stays invisible (macOS only)" (fun () -> ())
             else
             testCaseAsync "a symlink en route to nothing granted stays invisible" (async {
                // The narrowness itself, and what the reverted broad variant could not
                // promise: metadata answers per NODE, so even stat of a link that no grant
                // is spelled through is refused — not just the read of its target.
                let workspace = mkdtemp nodeFs nodeOs
                let elsewhere = mkdtemp nodeFs nodeOs |> Fs.canonical |> Option.get
                symlink nodeFs (elsewhere + "/real") (elsewhere + "/link")
                let! sandbox = startSandbox (policyIn workspace [])
                let! run, _, _ = shell sandbox (sprintf "stat %s/link > /dev/null 2>&1" elsewhere)
                Expect.notEqual (exitCode run) 0 "lstat of an unrelated link is refused"
                do! sandbox.Dispose ()
            }))

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
            // Linux only, because the refusal under test IS the Linux dependency probe:
            // initialize forks `which` for ripgrep/bwrap/socat there, and an empty PATH
            // fails it. On macOS initialize probes nothing — Seatbelt ships with the OS —
            // so `create` succeeds and an empty PATH surfaces at the first command's
            // shell resolution instead. Discovered the first time this tier ran on a Mac
            // (CI's Srt tier is Linux): the case errored on master, stock srt, same way.
            (if platform () = "darwin" then
                ptestCase "a probe that could not run is not an answer, and is not remembered (Linux only: macOS initialize probes nothing)" (fun () -> ())
             else
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
            }))

            // A symlink is not a second way in. srt normalizes an allow to its realpath
            // while Seatbelt matches the path AS WRITTEN, which cuts both ways: the
            // spelling a process uses may be refused (see docs/GAPS.md), and a link
            // planted inside a granted directory must never reach past what was granted.
            // Only the second is a promise, so only the second is asserted here.
            (if platform () <> "darwin" then
                ptestCase "a symlink is not a way into what nothing granted (macOS only: Seatbelt matches paths as written)" (fun () -> ())
             else
             testCaseAsync "a symlink is not a way into what nothing granted" (async {
                let workspace = mkdtemp nodeFs nodeOs |> Fs.canonical |> Option.get
                let elsewhere = mkdtemp nodeFs nodeOs |> Fs.canonical |> Option.get
                writeFile nodeFs (elsewhere + "/not-yours") "secret"
                // The link itself is inside the workspace, so it is granted and only its
                // target is not — the shape a policy cannot see by reading paths alone.
                symlink nodeFs elsewhere (workspace + "/out")

                let! sandbox = startSandbox (policyIn workspace [])
                let! run, out, _ = shell sandbox ("cat " + workspace + "/out/not-yours")
                Expect.notEqual (exitCode run) 0 "reading through the link was refused"
                Expect.isFalse (out.Contains "secret") "and nothing of it came back"
                do! sandbox.Dispose ()
             }))
        ])
