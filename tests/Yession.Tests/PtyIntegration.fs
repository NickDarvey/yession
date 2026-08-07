module Yession.Tests.PtyIntegration

// `SpawnPty` on the sandbox seam, driven for real (Plan 13, stage 2c).
//
// Everything here asserts the ONE thing a pipe can never do. A test that merely spawns a
// process and reads its output would pass over `Spawn` just as happily, and would therefore
// prove nothing about ptys at all — so the assertions are the properties a terminal device
// has and a pipe does not: `tty` names a device, `isatty` is true, a resize is visible to
// the program, and stdout and stderr arrive on one stream because a tty has one.
//
// The suite sits under `Tag.needs [Pty]`, and `check` probes the capability by OPENING a
// pty rather than by looking for the addon's file — a package that resolves is not a kernel
// that will hand out `/dev/pts`.

open Fable.Core
open Fable.Pyxpecto
open Yession.Domain
open Yession.Host
open Yession.Tests.Support

/// Everything a pty emits until it exits, plus how it ended. One string, not two: that is
/// the shape of a terminal.
let private runOnPty (executable: string) (arguments: string list) : Async<Result<string * SandboxRun, string>> =
    async {
        let policy =
            { ReadPaths = []
              WritePaths = []
              AllowedDomains = None
              Env = Sandboxes.hostBaseline (Sandboxes.ambientEnv ())
              WorkingDirectory = None }
        match! Sandboxes.HostSandbox.create () policy with
        | Error e -> return Error e
        | Ok sandbox ->
            match sandbox.SpawnPty with
            | None -> return Error "the host backend reports no pty support"
            | Some spawnPty ->
                let output = System.Text.StringBuilder ()
                let exec =
                    { Executable = executable; Arguments = arguments; Env = Map.empty; WorkingDirectory = None }
                match! spawnPty exec 80 24 (fun data -> output.Append data |> ignore) with
                | Error e -> return Error e
                | Ok pty ->
                    let! ended = pty.Exited
                    do! sandbox.Dispose ()
                    return Ok (string output, ended)
    }

let tests =
    testList "Pty (Plan 13)" [
        testCaseAsync "the host backend offers a pty at all" <|
            async {
                let policy =
                    { ReadPaths = []
                      WritePaths = []
                      AllowedDomains = None
                      Env = Map.empty
                      WorkingDirectory = None }
                match! Sandboxes.HostSandbox.create () policy with
                | Error e -> failwith e
                | Ok sandbox ->
                    Expect.isTrue (Option.isSome sandbox.SpawnPty) "SpawnPty is Some where the addon is present"
                    do! sandbox.Dispose ()
            }

        testCaseAsync "the process gets a real terminal device, which is the whole point" <|
            async {
                // `tty` prints the device name and exits 0 on a terminal; on a pipe it prints
                // "not a tty" and exits 1. This is the assertion that separates this seam from
                // the piped one, and the only one that could not be satisfied by `Spawn`.
                match! runOnPty "/bin/sh" [ "-c"; "tty" ] with
                | Error e -> failwith e
                | Ok (output, ended) ->
                    Expect.isTrue (output.Contains "/dev/pts/" || output.Contains "/dev/ttys")
                        (sprintf "tty names a terminal device, got: %s" output)
                    Expect.equal ended (SandboxExited 0) "and exits 0, which it cannot do on a pipe"
            }

        testCaseAsync "stdout and stderr arrive on ONE stream, because a tty has one device" <|
            async {
                // Not an omission in `PtyHandle` — a property of ptys. The piped handle keeps
                // the split because it genuinely has one; this must not pretend to.
                match! runOnPty "/bin/sh" [ "-c"; "echo out; echo err 1>&2" ] with
                | Error e -> failwith e
                | Ok (output, _) ->
                    Expect.isTrue (output.Contains "out") "stdout is there"
                    Expect.isTrue (output.Contains "err") "and so is stderr, on the same stream"
            }

        testCaseAsync "the size the pty is opened with is the size the program sees" <|
            async {
                match! runOnPty "/bin/sh" [ "-c"; "stty size" ] with
                | Error e -> failwith e
                | Ok (output, _) ->
                    // `stty size` prints "rows cols".
                    Expect.isTrue (output.Contains "24 80") (sprintf "opened 80x24, program saw: %s" (output.Trim ()))
            }

        testCaseAsync "a resize reaches the program, which is what SIGWINCH is for" <|
            async {
                let policy =
                    { ReadPaths = []
                      WritePaths = []
                      AllowedDomains = None
                      Env = Sandboxes.hostBaseline (Sandboxes.ambientEnv ())
                      WorkingDirectory = None }
                match! Sandboxes.HostSandbox.create () policy with
                | Error e -> failwith e
                | Ok sandbox ->
                    let spawnPty = Option.get sandbox.SpawnPty
                    let output = System.Text.StringBuilder ()
                    // Report the size on SIGWINCH, then again at exit. A shell that never
                    // learns its size is the one that redraws wrongly, so this is the
                    // behaviour that matters rather than the call returning unit.
                    let script = "trap 'stty size' WINCH; sleep 2 & wait; stty size"
                    let exec = { Executable = "/bin/sh"; Arguments = [ "-c"; script ]; Env = Map.empty; WorkingDirectory = None }
                    match! spawnPty exec 80 24 (fun d -> output.Append d |> ignore) with
                    | Error e -> failwith e
                    | Ok pty ->
                        do! Async.Sleep 300
                        pty.Resize 120 40
                        let! _ = pty.Exited
                        do! sandbox.Dispose ()
                        Expect.isTrue ((string output).Contains "40 120")
                            (sprintf "the program saw the new size, got: %s" ((string output).Replace ("\n", " | ")))
            }

        testCaseAsync "writing to the pty reaches the program's stdin" <|
            async {
                let policy =
                    { ReadPaths = []
                      WritePaths = []
                      AllowedDomains = None
                      Env = Sandboxes.hostBaseline (Sandboxes.ambientEnv ())
                      WorkingDirectory = None }
                match! Sandboxes.HostSandbox.create () policy with
                | Error e -> failwith e
                | Ok sandbox ->
                    let spawnPty = Option.get sandbox.SpawnPty
                    let output = System.Text.StringBuilder ()
                    let exec =
                        { Executable = "/bin/sh"
                          Arguments = [ "-c"; "read line; echo \"got:$line\"" ]
                          Env = Map.empty
                          WorkingDirectory = None }
                    match! spawnPty exec 80 24 (fun d -> output.Append d |> ignore) with
                    | Error e -> failwith e
                    | Ok pty ->
                        do! Async.Sleep 200
                        pty.Write "hello\r"
                        let! _ = pty.Exited
                        do! sandbox.Dispose ()
                        Expect.isTrue ((string output).Contains "got:hello") (sprintf "got: %s" (string output))
            }

        testCaseAsync "killing the pty settles Exited rather than hanging" <|
            async {
                let policy =
                    { ReadPaths = []
                      WritePaths = []
                      AllowedDomains = None
                      Env = Sandboxes.hostBaseline (Sandboxes.ambientEnv ())
                      WorkingDirectory = None }
                match! Sandboxes.HostSandbox.create () policy with
                | Error e -> failwith e
                | Ok sandbox ->
                    let spawnPty = Option.get sandbox.SpawnPty
                    let exec =
                        { Executable = "/bin/sh"; Arguments = [ "-c"; "sleep 30" ]; Env = Map.empty; WorkingDirectory = None }
                    match! spawnPty exec 80 24 ignore with
                    | Error e -> failwith e
                    | Ok pty ->
                        do! Async.Sleep 200
                        pty.Kill ()
                        // The assertion is that this RESOLVES. A handle whose Exited never
                        // settles would hang the drain that awaits it, for ever.
                        let! _ = pty.Exited
                        do! sandbox.Dispose ()
                        Expect.isTrue true "Exited resolved after Kill"
            }
    ]
