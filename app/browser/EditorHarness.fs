module Yession.Browser.EditorHarness

// A host-free browser harness for the rich-editor E2E. Mounts a ProseMirror editor on a fresh
// Yjs fragment into `#host` and exposes its serialized Markdown as `window.__md`. There is NO
// Session Process, NO WebRTC and NO native addon here — just the editor and a doc — so the
// editor-rendering E2E (`Tag.needs [Browser]`) runs wherever Chromium exists, decoupled from
// `node-datachannel`. Pure F# (the no-authored-JS invariant holds); Fable-compiles alongside
// the app browser entry, and the `Browser`-cap test build esbuilds it into the served bundle.
//
// It also exercises presence cursors headlessly-but-in-a-browser: the editor reports its own
// selection (base64 relative anchor/head) through `reportFocus`; `window.__pushRemote(name)`
// feeds that same selection back in as a *remote* peer's cursor, so the on-screen decorations
// (the caret widget + label and the selection highlight) render for the E2E to assert on.
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

[<Emit("(function(f){ window.__pushRemote = f; })($0)")>]
let private exposePush (f: string -> unit) : unit = jsNative

/// How many times Enter has asked to send. The harness mounts the editor exactly as the
/// COMPOSER does (`onSubmit` supplied), so the E2E drives the real binding: Enter sends and
/// inserts nothing, Alt+Enter is the new line. A counter rather than a callback because what
/// the test needs to know is "did it fire", and the send itself belongs to the app.
[<Emit("(function(n){ window.__sends = n; })($0)")>]
let private exposeSends (n: int) : unit = jsNative

let private doc = Y.Doc.Create ()
let private fragment = doc.getXmlFragment "body"

do
    // The editor reports its local selection here; keep the latest so the harness can replay it
    // as a remote peer's cursor on demand.
    let mutable lastSelection : (string * string) option = None
    let mutable sends = 0
    exposeSends 0
    let handle =
        Editor.mountEditor
            host
            fragment
            false
            (fun sel -> lastSelection <- sel)
            (Some (fun () ->
                sends <- sends + 1
                exposeSends sends))
    exposeMd (fun () -> Markdown.ofFragment fragment)
    exposePush (fun name ->
        match lastSelection with
        | Some (anchor, head) ->
            handle.PushPresences
                [ ({ Colour = "hsl(200, 70%, 55%)"
                     Selection = "hsla(200, 70%, 55%, 0.25)"
                     Name = name
                     Anchor = anchor
                     Head = head } : Editor.RemoteBodyCursor) ]
        | None -> ())
