module Yession.Tests.LockSource

// What the COMMITTED devenv.lock is allowed to say.
//
// One rule: it carries the `nixpkgs` node and nothing else. `devenv.yaml` names nixpkgs by a
// CHANNEL, which moves, so the lock is what makes two jobs in one pull request build against
// the same thing; devenv's own input is deliberately absent, because in a github-restricted
// sandbox `devenv.local.yaml` replaces it with a /nix/store path from THAT container, which is
// not a fact about this repository and is not valid anywhere else. `.gitignore` states both
// halves at length.
//
// It was already guarded, by a clean filter `.claude/setup.sh` installs into `.git/info`, and
// the guard was believed total — "the store path cannot reach the index even by `git commit
// -a`". That is true of a clone where the filter is INSTALLED. It says nothing about a clone
// where it is not yet, and the filter arrives only when setup runs, which is at session start.
// A container whose session began before the filter existed spent two commits in that window
// and put `/nix/store/lxdvfnj1yvx2jg9wn9680792dvhwbx0v-source` on master, silently, through a
// guard that had just been hardened against the OTHER way it fails (a filter that runs and
// errors — #254). Absent is a third state, and it is fail-open.
//
// So the check moved to where no clone's local configuration can be missing it. A filter keeps
// a working tree clean, which is worth having and is why it stays; this refuses the content.
// `TestSources` says the same thing about a different rule: a guard on the source fires on the
// pull request that writes it.
//
// No capability: it reads a file.

open Fable.Core
open Fable.Core.JsInterop
open Fable.Pyxpecto

let private childProcess : obj = importAll "node:child_process"


[<Emit("Object.keys(JSON.parse($0).nodes ?? {})")>]
let private nodeNames (json: string) : string array = jsNative

[<Emit("Object.keys(((JSON.parse($0).nodes ?? {}).root ?? {}).inputs ?? {})")>]
let private rootInputs (json: string) : string array = jsNative

/// The lock as GIT has it, never the working copy. The working copy carries the devenv node
/// on any machine that has run devenv — that is the normal state and not what this is about.
/// Read through `git show` so this asks the question that matters: what would a laptop or a CI
/// runner get when it checks this out.
[<Emit("(() => { try { return $0.execSync('git show HEAD:devenv.lock', { encoding: 'utf8', stdio: ['ignore','pipe','ignore'] }) } catch { return '' } })()")>]
let private committedLockWith (cp: obj) : string = jsNative

/// This module is compiled to an ES module, where `require` is not defined — a `require` in
/// an emit body throws, and the `catch` around it turns that into "the lock is missing"
/// rather than "this check cannot run". It read as the former for one whole run.
let private committedLock () : string = committedLockWith childProcess

let tests =
    testList "the committed devenv.lock" [

        testCase "carries nixpkgs, and nothing that belongs to one machine" <| fun () ->
            match committedLock () with
            | "" ->
                // Not a pass. A run that cannot read the tracked lock has not checked
                // anything, and saying so is the difference between a guard and a decoration.
                failwith "could not read HEAD:devenv.lock — this check needs a git checkout, and proves nothing without one"
            | json ->
                Expect.equal
                    (nodeNames json |> Array.sort |> List.ofArray)
                    [ "nixpkgs"; "root" ]
                    "the lock pins the channel this repo names, and records nobody's container"

        // Belt and braces would be two mechanisms for one requirement; this is the other half
        // of the same one. The node is how the path gets in TODAY, and the thing that must
        // never be true is the PATH — so that is what the second case says, and it would still
        // fire if devenv started writing it somewhere else in the file.
        testCase "names no /nix/store path at all" <| fun () ->
            match committedLock () with
            | "" -> failwith "could not read HEAD:devenv.lock"
            | json ->
                Expect.isFalse
                    (json.Contains "/nix/store/")
                    "a store path is valid on exactly one machine, and this file is read by every checkout"

        testCase "the root still takes its inputs from the lock" <| fun () ->
            match committedLock () with
            | "" -> failwith "could not read HEAD:devenv.lock"
            | json ->
                Expect.equal
                    (rootInputs json |> Array.sort |> List.ofArray)
                    [ "nixpkgs" ]
                    "stripping the node without its root input would leave a lock that does not resolve"
    ]
