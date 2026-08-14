namespace Yession.App

/// Every static file a build ships, declared once — the case, its path, and what its bytes
/// ARE. Three consumers read this and nothing else: `tasks.fsx` (which produces each file),
/// `Assets` (which reads and serves them), and the documents that link them. Each consumer
/// matches over the type, so adding a file is a compile error everywhere until it is handled.
/// That is the point of a union here rather than a directory listing: the build cannot ship
/// what the server will not serve, and the server cannot claim what the build does not ship.
///
/// This does NOT make one process's set another's business. A session upgraded on its own has
/// its own binary, so its own declaration, and serves faces this Manager has never heard of —
/// the route (`SessionRoute.Asset`) still carries a path and nothing more. What is exhaustive
/// is a build against ITSELF.
///
/// The identifier would be the file name if F# allowed it. It does not: `.` and `/` are
/// invalid in a union case even inside backticks (FS0883), and a lowercase case needs
/// `RequireQualifiedAccess` (FS0053, which is why the attribute is here). So the case is the
/// backticked stem, and the name lives exactly once — in `describe`, beside the content type,
/// because the two are one fact. Telling a browser the wrong thing about bytes is the same
/// class of bug as serving the wrong bytes: a stylesheet that is not `text/css` is DROPPED by
/// a standards-mode document, and a module script at a non-JS type is refused outright.
[<RequireQualifiedAccess>]
type AssetFile =
    /// The browser client bundle (esbuild over the Fable output).
    | ``client``
    /// The shell's stylesheet (Tailwind over `app/tailwind.css`).
    | ``app``
    /// The replay player's stylesheet, its own file because the shell defers it.
    | ``player``
    // The four voices, vendored under `app/fonts` (both SIL OFL 1.1; see the two LICENCE files
    // beside them). One case per FACE, not per family, so a weight a stylesheet asks for cannot
    // be forgotten by the build — and the weights are exactly the ones the surfaces wear, which
    // is why the families differ: the chrome sets four, a message three, and a terminal two
    // (400, and 600 because that is what ANSI bold renders as).
    | ``noto-sans-200``
    | ``noto-sans-300``
    | ``noto-sans-400``
    | ``noto-sans-600``
    | ``neon-400``
    | ``neon-600``
    | ``xenon-300``
    | ``xenon-400``
    | ``xenon-600``
    | ``krypton-300``
    | ``krypton-400``
    | ``krypton-600``

module AssetFile =

    /// A module script is REFUSED at anything but a JavaScript type, and a stylesheet in a
    /// standards-mode document is dropped at anything but `text/css` — both measured, by
    /// serving the set as `application/octet-stream` and watching the page paint nothing and
    /// the client never start. `font/woff2` is the odd one out: a browser sniffs the container
    /// and would take the face regardless. It is here because the set says what its bytes are,
    /// and a font is not an unlabelled blob.
    let private javascript = "text/javascript; charset=utf-8"
    let private stylesheet = "text/css; charset=utf-8"
    let private woff2 = "font/woff2"

    /// Where the file sits inside the asset set, and what its bytes are. THE declaration: no
    /// other place in the product spells an asset's name.
    let describe (file: AssetFile) : string * string =
        match file with
        | AssetFile.``client`` -> "client.js", javascript
        | AssetFile.``app`` -> "app.css", stylesheet
        | AssetFile.``player`` -> "player.css", stylesheet
        | AssetFile.``noto-sans-200`` -> "fonts/noto-sans-latin-200-normal.woff2", woff2
        | AssetFile.``noto-sans-300`` -> "fonts/noto-sans-latin-300-normal.woff2", woff2
        | AssetFile.``noto-sans-400`` -> "fonts/noto-sans-latin-400-normal.woff2", woff2
        | AssetFile.``noto-sans-600`` -> "fonts/noto-sans-latin-600-normal.woff2", woff2
        | AssetFile.``neon-400`` -> "fonts/monaspace-neon-latin-400-normal.woff2", woff2
        | AssetFile.``neon-600`` -> "fonts/monaspace-neon-latin-600-normal.woff2", woff2
        | AssetFile.``xenon-300`` -> "fonts/monaspace-xenon-latin-300-normal.woff2", woff2
        | AssetFile.``xenon-400`` -> "fonts/monaspace-xenon-latin-400-normal.woff2", woff2
        | AssetFile.``xenon-600`` -> "fonts/monaspace-xenon-latin-600-normal.woff2", woff2
        | AssetFile.``krypton-300`` -> "fonts/monaspace-krypton-latin-300-normal.woff2", woff2
        | AssetFile.``krypton-400`` -> "fonts/monaspace-krypton-latin-400-normal.woff2", woff2
        | AssetFile.``krypton-600`` -> "fonts/monaspace-krypton-latin-600-normal.woff2", woff2

    let path (file: AssetFile) : string = fst (describe file)
    let contentType (file: AssetFile) : string = snd (describe file)

    /// The whole set: what a build produces, and what a server reads. Hand-listed, because F#
    /// will not enumerate a union's cases without reflection and this compiles to the browser
    /// too — so the suite counts the cases by reflection instead, where that is affordable, and
    /// fails when one is missing here. The same shape the route contract's `every` uses.
    let all =
        [ AssetFile.``client``
          AssetFile.``app``
          AssetFile.``player``
          AssetFile.``noto-sans-200``
          AssetFile.``noto-sans-300``
          AssetFile.``noto-sans-400``
          AssetFile.``noto-sans-600``
          AssetFile.``neon-400``
          AssetFile.``neon-600``
          AssetFile.``xenon-300``
          AssetFile.``xenon-400``
          AssetFile.``xenon-600``
          AssetFile.``krypton-300``
          AssetFile.``krypton-400``
          AssetFile.``krypton-600`` ]

    /// The file a request's path names, if it names one at all. The lookup a server does — and
    /// the reason nothing derived from a request path ever reaches the file system.
    let ofPath (requested: string) : AssetFile option =
        all |> List.tryFind (fun file -> path file = requested)
