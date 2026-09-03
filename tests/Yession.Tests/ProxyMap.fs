module Yession.Tests.ProxyMap

// The proxy example (`examples/proxy`), driven end to end: a real Manager under
// `--auth trusted-headers`, a real session process, and the map process following the
// registry stream into a file the way a deployment's reverse proxy reads it.
//
// The example is standalone by rule — it reads the stream and the two documented
// placeholders, nothing of this repository's code — so this is the only place the two halves
// of that contract are checked against each other: what the Manager publishes, and what the
// map makes of it. Its own README can say what it does; only a run can say it still does.
//
// `Ports` and `Native`: a session's port is what gets rendered, and only a real session
// process has one.

open System
open Fable.Core
open Fable.Pyxpecto
open Yession.Oidc
open Yession.Host
open Yession.Tests.Support

[<ImportAll("node:fs")>]
let private nodeFs : obj = jsNative

[<Emit("$0.existsSync($1)")>]
let private existsSync (fs: obj) (path: string) : bool = jsNative

[<Emit("$0.readFileSync($1, 'utf8')")>]
let private readFileSync (fs: obj) (path: string) : string = jsNative

[<Emit("process.execPath")>]
let private nodePath : string = jsNative

/// The map process, running, with what it has said so far for the failure report.
type private MapProcess =
    abstract said : unit -> string
    abstract stop : unit -> JS.Promise<unit>

/// Spawn `main.mjs` with the deployment's arguments and wait for it to announce which stream
/// it follows — so a process that dies on its arguments fails here, with its own words,
/// rather than as a wait on a file that never appears.
[<Emit("""(async function (args, timeoutMs) {
  const { spawn } = await import('node:child_process')
  const child = spawn(process.execPath, ['examples/proxy/main.mjs'].concat(args),
                      { stdio: ['ignore', 'pipe', 'pipe'] })
  let said = ''
  await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('sessions-map never announced itself; said:\n' + said)), timeoutMs)
    const watch = (stream) => stream.on('data', (chunk) => {
      said += String(chunk)
      if (/ follows /.test(said)) { clearTimeout(timer); resolve() }
    })
    watch(child.stdout); watch(child.stderr)
    child.on('exit', (code) => { clearTimeout(timer); reject(new Error('sessions-map exited with ' + code + ':\n' + said)) })
  })
  return {
    said: () => said,
    stop: () => new Promise((resolve) => {
      child.on('exit', () => resolve())
      try { child.kill('SIGTERM') } catch (_) { resolve() }
    })
  }
})($0, $1)""")>]
let private startMap (args: string []) (timeoutMs: int) : JS.Promise<MapProcess> = jsNative

/// Run `main.mjs` to completion — for the arguments it refuses.
[<Emit("""(async function (args) {
  const { spawn } = await import('node:child_process')
  return await new Promise((resolve) => {
    const child = spawn(process.execPath, ['examples/proxy/main.mjs'].concat(args),
                        { stdio: ['ignore', 'pipe', 'pipe'] })
    let stderr = ''
    child.stderr.on('data', (chunk) => { stderr += String(chunk) })
    child.on('exit', (code) => resolve({ code, stderr }))
  })
})($0)""")>]
let private runMap (args: string []) : JS.Promise<{| code: int; stderr: string |}> = jsNative

let private dataDirFor (label: string) =
    sprintf "tests/Yession.Tests/out/.data/proxy-map-%s-%d" label (int (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ()) % 1000000)

let tests =
    testList "The proxy example: the registry, rendered into a proxy's config (examples/proxy)" [
        testCaseAsync "a launched session is rendered through the template, and leaves the map when it stops" <|
            async {
                let dataDir = dataDirFor "follow"
                // trusted-headers, so this also proves the subscribe asserts a subject: under
                // that rule a header-less stream is a 401 with no frames, and the map would
                // never fill.
                let! pm =
                    ProcessManager.createWithUi
                        { ProcessManager.Options.defaults dataDir nodePath [ "app/SessionMain.js" ] with
                            Strategy = Some Strategy.trustedHeaders }
                        (Some ManagerUi.tryHandle)
                let manager = sprintf "http://127.0.0.1:%d" pm.EndpointPort.Value
                let out = dataDir + "/sessions.map"
                let! map =
                    startMap
                        [| "--manager"; manager; "--as"; "proxy-map"; "--template"; "{id} -> 127.0.0.1:{port}"; "--out"; out |]
                        10000
                    |> Async.AwaitPromise
                // `Async.Catch` rather than `try/finally`: on Node nothing may block the loop
                // to await a cleanup, and a failed assertion still has to take the child down.
                let! outcome =
                    async {
                        let record = pm.CreateSession "mapped" "Mapped" |> expect
                        let! launched = pm.Launch record.SessionId
                        let port = expect launched
                        let expected = sprintf "mapped -> 127.0.0.1:%d" port
                        do! waitUntil "the map to carry the launched session" (fun () ->
                                existsSync nodeFs out && (readFileSync nodeFs out).Contains expected)
                        Expect.equal (readFileSync nodeFs out) expected "one running session is one rendering, and nothing else"
                        let! stopped = pm.Stop record.SessionId
                        expect stopped
                        do! waitUntil "the map to empty" (fun () -> readFileSync nodeFs out = "")
                    }
                    |> Async.Catch
                do! map.stop () |> Async.AwaitPromise
                do! pm.StopAll ()
                match outcome with
                | Choice1Of2 () -> ()
                | Choice2Of2 error -> failwithf "%s\n--- sessions-map said ---\n%s" error.Message (map.said ())
            }

        testCaseAsync "a template naming neither placeholder is refused before anything is written" <|
            async {
                let out = dataDirFor "refused" + "/never.map"
                let! result = runMap [| "--template"; "static"; "--out"; out |] |> Async.AwaitPromise
                Expect.equal result.code 64 "EX_USAGE, the way the bins refuse an argument"
                Expect.isTrue (result.stderr.Contains "neither {id} nor {port}") "it names the rule that refused it"
                Expect.isFalse (existsSync nodeFs out) "refused means nothing was written"
            }
    ]
