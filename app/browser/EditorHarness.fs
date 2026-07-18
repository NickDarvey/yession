module Yession.Browser.EditorHarness

// A host-free browser harness for the rich-editor E2E. Mounts a ProseMirror editor on a fresh
// Yjs fragment into `#host` and exposes its serialized Markdown as `window.__md`. There is NO
// Session Process, NO WebRTC and NO native addon here — just the editor and a doc — so the
// editor-rendering E2E (`Tag.needs [Browser]`) runs wherever Chromium exists, decoupled from
// `node-datachannel`. Pure F# (the no-authored-JS invariant holds); Fable-compiles alongside
// the app browser entry, and the `Browser`-cap test build esbuilds it into the served bundle.
//
// Module-level `do` runs on import — module scripts are deferred, so `#host` already exists.

open Fable.Core
open Yjs
open Yession.Domain
open Yession.App

[<Emit("document.getElementById('host')")>]
let private host : obj = jsNative

[<Emit("(function(f){ window.__md = f; })($0)")>]
let private exposeMd (f: unit -> string) : unit = jsNative

let private doc = Y.Doc.Create ()
let private fragment = doc.getXmlFragment "body"

do
    Editor.mountEditor host fragment false |> ignore
    exposeMd (fun () -> Markdown.ofFragment fragment)
