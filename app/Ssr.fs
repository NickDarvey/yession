module Yession.Host.Ssr

// Our own dependency-free server-side renderer for Fable.Lit templates. A lit-html
// `TemplateResult` is `{ strings, values }`: the static markup parts interleaved with the
// hole values. Rendering to a string is just that interleave — recursing into nested
// templates and arrays, escaping text, and dropping the bindings a string can't carry
// (event/property/boolean holes, `@ . ?`). Because it never parses HTML, it handles
// `<textarea>` child-text bindings that @lit-labs/ssr miscounts, and because it pulls no
// dependencies it bundles trivially into the shipped single-file executable — no CDN, no
// parse5. The browser renders the same templates live; this is the first paint.

open Fable.Core
open Fable.Core.JsInterop
open Yession.Domain
open Yession.App
open Lit

// --- lit-html TemplateResult shape (read-only) ------------------------------------------

[<Emit("(function (v) { return v != null && v._$litType$ !== undefined })($0)")>]
let private isTemplateResult (v: obj) : bool = jsNative

[<Emit("$0.strings")>]
let private trStrings (v: obj) : string[] = jsNative

[<Emit("$0.values")>]
let private trValues (v: obj) : obj[] = jsNative

/// Any iterable that isn't a string (JS arrays AND Fable's F# lists, which lit-html renders
/// as a sequence of child parts). Excludes strings, which are handled as text.
[<Emit("(function (v) { return v != null && typeof v !== 'string' && typeof v[Symbol.iterator] === 'function' })($0)")>]
let private isIterable (v: obj) : bool = jsNative

[<Emit("Array.from($0)")>]
let private toArray (v: obj) : obj[] = jsNative

[<Emit("typeof $0")>]
let private jsTypeof (v: obj) : string = jsNative

[<Emit("String($0)")>]
let private jsString (v: obj) : string = jsNative

/// A static part ending in an event/property/boolean binding hole (`@x=`, `.x=`, `?x=`,
/// with or without an opening quote). These carry a listener/property a string cannot.
[<Emit("/[@.?][A-Za-z0-9_-]+=\"?$/.test($0)")>]
let private endsWithBinding (s: string) : bool = jsNative

[<Emit("$0.replace(/\\s*[@.?][A-Za-z0-9_-]+=\"?$/, '')")>]
let private stripBinding (s: string) : string = jsNative

/// A static part ending in an attribute-value hole (`name=` or `name="`).
[<Emit("/=\"?$/.test($0)")>]
let private endsWithAttr (s: string) : bool = jsNative

let private escapeText (s: string) =
    s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")

/// Public because the Manager's own standalone pages need it too (Plan 11's `/open`
/// landing page): one escaper for every string this process puts in an attribute.
let escapeAttr (s: string) =
    s.Replace("&", "&amp;").Replace("\"", "&quot;")

let rec private renderValue (inAttr: bool) (v: obj) : string =
    if isNull v then ""
    elif isTemplateResult v then renderTemplate v
    elif jsTypeof v = "string" then (let s = unbox<string> v in if inAttr then escapeAttr s else escapeText s)
    elif jsTypeof v = "number" || jsTypeof v = "boolean" then jsString v
    elif isIterable v then toArray v |> Array.map (renderValue false) |> String.concat ""
    // Functions (event handlers), lit's `nothing`/`noChange` sentinels, and any other
    // object have no string form — render nothing.
    else ""

and private renderTemplate (tr: obj) : string =
    let strings = trStrings tr
    let values = trValues tr
    let sb = System.Text.StringBuilder ()
    for i in 0 .. values.Length - 1 do
        let s = strings.[i]
        if endsWithBinding s then
            // Drop the binding syntax and its value — a string can't carry a listener.
            sb.Append (stripBinding s) |> ignore
        else
            sb.Append s |> ignore
            sb.Append (renderValue (endsWithAttr s) values.[i]) |> ignore
    sb.Append strings.[strings.Length - 1] |> ignore
    sb.ToString ()

/// Render a Fable.Lit template to an HTML string.
let render (template: TemplateResult) : string = renderTemplate (box template)

/// Render the client shell for `model` to a string (the view's `ViewActions` are no-ops —
/// the handlers never fire during rendering).
let renderModel (model: ClientModel) : string =
    render (View.view ViewActions.ssr model ignore)

/// The full bootstrap document: the served page IS the client shell. Embeds the serving
/// session id (so the browser keys its local doc store before any connection), the local
/// stylesheet, and the `#app` mount whose classes persist across the browser's re-renders.
///
/// `mount` is the path this session is served under (`""` at an origin root) and it is a
/// REQUIRED parameter for a reason: it becomes the `<base href>` every relative URL in
/// the page and in the client bundle resolves against (`SessionRoute.relative` emits
/// nothing else), so a page rendered without one would ask the origin root for its own
/// assets. There is no way to render this document and forget it.
/// `assets` is the BUILD this shell is rendered against — one address covering every static
/// file the server will hand out, so the document names bytes that exist. That pairing is what
/// makes the assets cacheable forever and the shell the only thing that has to be fresh. The
/// shell names the files it links and nothing else knows they exist; a build that adds one
/// changes nothing here but the line that links it.
let page (sessionId: SessionId) (mount: string) (managerOrigin: string option) (ephemeralStorage: bool) (assets: AssetBuild) (model: ClientModel) : string =
    String.concat "" [
        "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">"
        "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
        // Before the stylesheet and the bundle, because it governs how both resolve.
        sprintf "<base href=\"%s/\">" (escapeAttr mount)
        sprintf "<meta name=\"%s\" content=\"%s\">" Dom.sessionMetaName (escapeAttr (SessionId.value sessionId))
        // Where to ask for this session back once it has stopped (Plan 11). OMITTED, never
        // emitted empty, when this session has no Manager: the client's offer to bring the
        // session back is a total function of the model, so a missing tag is what makes it
        // structurally impossible to render a button with nowhere to go.
        (match managerOrigin with
         | Some origin -> sprintf "<meta name=\"%s\" content=\"%s\">" Dom.managerMetaName (escapeAttr origin)
         | None -> "")
        // Said only when it is true, so the shell of a path-mounted deployment carries
        // nothing about storage at all.
        (if ephemeralStorage then sprintf "<meta name=\"%s\" content=\"1\">" Dom.ephemeralStorageMetaName else "")
        "<title>Yession</title>"
        Style.headTags (AssetBuild.url assets AssetFile.``app``)
        // The replay player's sheet, linked but inert: most sessions never open a recording,
        // and a second render-blocking stylesheet in the head would make all of them pay for
        // the ones that do. `Replay.mount` flips its `media` when a replay is first shown.
        Style.deferredHeadTags (AssetBuild.url assets AssetFile.``player``) Dom.playerStylesheetHook
        // What makes this installable, and chrome-less once it is (`WebApp`). Both URLs are
        // relative, like every other one here, so they resolve under the mount rather than at
        // the origin root — and the manifest's own contents then resolve against ITS address.
        WebApp.headTags (SessionRoute.relative Manifest) (SessionRoute.relative Icon)
        // The ONE inline script in the shell, and the only thing that has to run before first
        // paint: a collapsed sidebar is a stored preference (written by the nav toggle), and
        // applying it from the bundle would paint the sidebar open and then shut it. Desktop
        // only — below the breakpoint the same class means "the drawer is open" (Style.sidebar),
        // which is never a preference.
        "<script>try{if(matchMedia('(min-width: 768px)').matches"
        + "&&localStorage.getItem('yession.nav')==='collapsed')"
        + "document.documentElement.classList.add('nav-alt')}catch(e){}</script>"
        "</head><body>"
        sprintf "<main id=\"%s\" class=\"%s\">%s</main>" Dom.appId Style.app (renderModel model)
        sprintf "<script type=\"module\" src=\"%s\"></script>" (AssetBuild.url assets AssetFile.``client``)
        "</body></html>"
    ]
