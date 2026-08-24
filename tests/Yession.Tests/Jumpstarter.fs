module Yession.Tests.Jumpstarter

// The jumpstarter provider (Plan 18), end to end — and the one thing its own pytest suite
// structurally cannot prove.
//
// That suite drives the provider with a client written beside it, in the same language, out
// of the same head. This one drives it with OUR client: `McpClient`, the code a session
// actually uses, over real HTTP against a real provider process talking to a real exporter.
// Two implementations that were never checked against each other agreeing is the only
// evidence that either read the protocol right — the same argument the serial suite makes,
// and worth making twice because this provider is somebody else's SDK behind our contract.
//
// `Jumpstarter`, because it needs uv, a resolvable Python environment, and two processes:
// see `jumpstarterAvailable` in tasks.fsx, which probes it by RUNNING it.

open System
open Fable.Core
open Fable.Pyxpecto
open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Domain.Tools
open Yession.Host

/// The exporter and the provider, running, with the control url the provider announced.
type private Stack =
    abstract url : string
    abstract stop : unit -> JS.Promise<unit>

/// Spawn both halves and wait for the provider to say where it is.
///
/// The provider's port is OS-assigned and read back off its readiness line, so concurrent
/// runs cannot collide. The EXPORTER's cannot be: `jmp run` refuses port 0, so one is picked
/// by binding and releasing — the narrowest race available, and the reason the readiness
/// wait below fails loudly rather than hanging.
///
/// `uv run --project` rather than a bare `jumpstarter-provider`: the example is a uv project,
/// its dependencies are locked, and resolving them through uv is exactly how the deployment
/// runs it.
[<Emit("""(async function (timeoutMs) {
  const { spawn } = await import('node:child_process')
  const net = await import('node:net')

  const project = 'examples/jumpstarter'
  const freePort = () => new Promise((resolve) => {
    const probe = net.createServer()
    probe.listen(0, '127.0.0.1', () => {
      const port = probe.address().port
      probe.close(() => resolve(port))
    })
  })

  const grpc = await freePort()
  const children = []
  const start = (args, env) => {
    const child = spawn('uv', ['run', '--project', project].concat(args),
                        { env: Object.assign({}, process.env, env), stdio: ['ignore', 'pipe', 'pipe'] })
    children.push(child)
    return child
  }

  // Waits for a line on either stream, so a process that dies before printing it fails here
  // with its own words rather than as a timeout.
  const awaitLine = (child, pattern, what) => new Promise((resolve, reject) => {
    let seen = ''
    const timer = setTimeout(() => reject(new Error(what + ' never printed ' + pattern + '; saw:\n' + seen)), timeoutMs)
    const watch = (stream) => stream.on('data', (chunk) => {
      seen += String(chunk)
      const found = seen.match(pattern)
      if (found) { clearTimeout(timer); resolve(found) }
    })
    watch(child.stdout); watch(child.stderr)
    child.on('exit', (code) => { clearTimeout(timer); reject(new Error(what + ' exited with ' + code + ':\n' + seen)) })
  })

  const exporter = start(
    ['jmp', 'run', '--exporter-config', project + '/tests/exporter.yaml',
     '--tls-grpc-listener', '127.0.0.1:' + grpc, '--tls-grpc-insecure'], {})
  await awaitLine(exporter, /Session server started/, 'the exporter')

  const provider = start(['jumpstarter-provider'], {
    JUMPSTARTER_HOST: '127.0.0.1:' + grpc,
    JUMPSTARTER_PROVIDER_PORT: '0',
    // Longer than this suite can possibly take, so a claim never expires mid-test: what is
    // under test here is the protocol, not the timeout (that is the pytest suite's).
    JUMPSTARTER_PROVIDER_TTL: '600'
  })
  // The readiness line names both ends — `MCP at <url>, exporter at <host:port>` — so the
  // url stops at the comma. `\S+` would take it with the comma attached, and a url with a
  // comma on the end fails as a connection refused rather than as anything legible.
  const announced = await awaitLine(provider, /MCP at (http:\/\/[^\s,]+)/, 'the provider')

  return {
    url: announced[1],
    stop: () => new Promise((resolve) => {
      for (const child of children) { try { child.kill('SIGTERM') } catch (_) {} }
      setTimeout(resolve, 200)
    })
  }
})($0)""")>]
let private startStack (timeoutMs: int) : JS.Promise<Stack> = jsNative

/// Start the stack, run the body against it, and stop it either way.
///
/// `Async.Catch` rather than `try/finally`: the suite runs on Node, where nothing may block
/// the loop to await a cleanup — and a failed assertion still has to take two child processes
/// down with it, or the next test inherits them.
let private withStack (body: Stack -> Async<unit>) =
    async {
        let! stack = startStack 180000 |> Async.AwaitPromise
        let! outcome = body stack |> Async.Catch
        do! stack.stop () |> Async.AwaitPromise
        match outcome with
        | Choice1Of2 () -> ()
        | Choice2Of2 error -> raise error
    }

let private expect result =
    match result with
    | Ok value -> value
    | Error e -> failwithf "invariant: %A" e

let private name = McpServerName.create "jumpstarter" |> expect

let private declared (url: string) : McpServerSet =
    { Servers = [ { Name = name; Transport = McpHttp url; Description = "" } ] }

let private toolNames (registries: ToolRegistry list) =
    registries |> List.collect ToolRegistry.allowedTools

let private answer (registries: ToolRegistry list) (tool: string) (args: string) =
    match registries |> List.tryFind (fun r -> ToolRegistry.namespaces r = [ "jumpstarter" ]) with
    | None -> failwith "the session has no registry for the jumpstarter namespace"
    | Some registry ->
        async {
            match! registry.Invoke { Namespace = "jumpstarter"; Name = tool; Arguments = args } with
            | Ok answer -> return answer
            | Error e -> return failwithf "the call never reached the tool: %s" e
        }

let private call (registries: ToolRegistry list) (tool: string) (args: string) =
    async {
        let! answered = answer registries tool args
        return answered.Text
    }

/// Poll rather than sleep: what is being waited on is a round trip through two processes and
/// a device, and a fixed sleep is either flaky or slow.
let private until (predicate: unit -> bool) : Async<bool> =
    let rec loop (remaining: int) =
        async {
            if predicate () then return true
            elif remaining <= 0 then return false
            else
                do! Async.Sleep 50
                return! loop (remaining - 50)
        }
    loop 30000

let tests =
    testList "The jumpstarter provider (Plan 18)" [

        testCaseAsync "a session declares it and gets the exporter as tools it can call" <|
            withStack (fun stack ->
                async {
                    let mcp = McpClient.create ()
                    do! mcp.Apply (declared stack.url)

                    // The names are the contract between a provider we did not write in our
                    // language and a model that will only ever see these strings.
                    Expect.equal
                        (toolNames (mcp.Registries ()) |> List.sort)
                        [ "mcp__jumpstarter__acquire"
                          "mcp__jumpstarter__driver_call"
                          "mcp__jumpstarter__power"
                          "mcp__jumpstarter__release"
                          "mcp__jumpstarter__status" ]
                        "our client and their server agree about what this exporter offers"

                    let registries = mcp.Registries ()

                    // Reaching the hardware: status names the drivers the exporter config
                    // declares, which means the whole path is live — HTTP, MCP, the SDK,
                    // gRPC, and back.
                    let! status = call registries "status" "{}"
                    Expect.stringContains status "power" "status names the power driver"
                    Expect.stringContains status "serial" "status names the console"

                    // The claim, from the outside: refused before it is taken, and taken
                    // once asked for.
                    let! refused = call registries "power" """{"action":"on"}"""
                    Expect.stringContains refused "acquire" "an unclaimed exporter says how to claim it"
                    let! acquired = call registries "acquire" "{}"
                    Expect.stringContains acquired "yours" "the claim is granted in words a model can act on"
                    let! powered = call registries "power" """{"action":"on"}"""
                    Expect.stringContains powered "done" "the power driver answered"

                    // The console is NOT here, and that is the claim: this provider owns
                    // control, and its console is reached as a terminal over the stream leg
                    // — which the next case drives.
                })

        // The loop Plan 19 closes, across two languages: THEIR provider offers a stream in
        // MCP's `_meta`, OUR client reads it, and OUR WebSocket attach opens it. Neither end
        // has ever seen the other's code, which is the only reason this proves anything.
        testCaseAsync "the console arrives as a stream this session can open" <|
            withStack (fun stack ->
                async {
                    let mcp = McpClient.create ()
                    do! mcp.Apply (declared stack.url)
                    let registries = mcp.Registries ()

                    let! acquired = answer registries "acquire" "{}"
                    let offer =
                        match acquired.Stream with
                        | Some offer -> offer
                        | None -> failwithf "acquire offered no stream: %s" acquired.Text
                    Expect.isFalse
                        offer.Ticket.Capabilities.CanInstrument
                        "a console is bytes: no blocks, no exit codes, nothing claimed that is not true"
                    Expect.isTrue offer.Renewable "and asking again is how you get another one"

                    let heard = System.Text.StringBuilder ()
                    let! attached = AttachWs.attach offer.Ticket 80 24 (fun text -> heard.Append text |> ignore)
                    let handle = attached |> expect

                    // Both ways, over the exporter's loopback line.
                    handle.Write "over the stream\r"
                    let! echoed = until (fun () -> heard.ToString().Contains "over the stream")
                    Expect.isTrue echoed "what a person types reaches the device and comes back"

                    // The console has ONE door, and this is it. There used to be two — the
                    // provider offered its own read and write tools beside the stream — and
                    // the pair had to be kept in step by a tee that starved neither. Both are
                    // gone: what an agent reads it reads through the terminal this stream
                    // becomes, past the lease, where everyone can see who is typing.
                    handle.Kill ()
                    let! ending = handle.Exited
                    Expect.equal ending (SandboxExited 0) "the stream ends in band, not by an abrupt close"
                })

        testCaseAsync "a second session is refused, and told who has it" <|
            withStack (fun stack ->
                async {
                    // Two clients, so two MCP sessions — which is what the provider keys a
                    // claim by, and the only way to test arbitration honestly.
                    let first = McpClient.create ()
                    let second = McpClient.create ()
                    do! first.Apply (declared stack.url)
                    do! second.Apply (declared stack.url)

                    let! taken = call (first.Registries ()) "acquire" "{}"
                    Expect.stringContains taken "yours" "the first session took it"

                    let! refused = call (second.Registries ()) "acquire" "{}"
                    Expect.stringContains refused "already held by" "the refusal says somebody has it"
                    Expect.stringContains refused "in use, not broken" "and that this is not a fault"

                    // And giving it back is what makes it available, rather than waiting.
                    let! _ = call (first.Registries ()) "release" "{}"
                    let! taken = call (second.Registries ()) "acquire" "{}"
                    Expect.stringContains taken "yours" "a released exporter is the next asker's"
                })
    ]
