namespace Yession.App

/// The shell as an INSTALLABLE app — the web manifest, the icon, and the head tags that make
/// a browser offer "add to home screen" and then launch this without its chrome.
///
/// Why it is here and not in a static file: a session does not necessarily own the root of
/// its origin (an operator's proxy may mount it under a path — docs/plans/09), so both the
/// manifest and every URL inside it have to be relative, and the icon has to come from a
/// process that may be serving from a store path with no assets directory beside it. The
/// icon is therefore a constant in the binary, like every other thing the shell needs to be
/// self-contained (`Style.headTags`, the inline nav script): no CDN, no sidecar file.
///
/// What each tag is for, because they overlap and it is easy to add a fourth that says the
/// same thing again:
///   * `manifest` — the standard declaration. `display: standalone` is what drops the
///     browser's chrome, and Android/desktop Chrome read it to offer an install.
///   * `apple-mobile-web-app-capable` — the SAME statement to a browser that does not read
///     the manifest (iOS before 16.4). Not a fallback beside a primary: it is the only place
///     those versions look, and the manifest is the only place newer ones do.
///   * `apple-mobile-web-app-status-bar-style: black` — the status bar over a black app. The
///     `black-translucent` variant would put the app UNDER the clock, which then needs
///     safe-area insets on every fixed panel; `black` keeps the system bars as system bars.
///   * `theme-color` — what the browser tints its own bars with BEFORE anyone installs
///     anything, which is most of the value on a phone: the address bar stops being a light
///     slab over a black product.
module WebApp =

    /// `#000` said the way each consumer wants it. The manifest takes JSON, the meta tag takes
    /// an attribute, and neither can read `--color-bg` — this is the one place the ground's
    /// hex is repeated outside `app/tailwind.css`, because it is the one place a stylesheet
    /// cannot reach.
    let private ground = "#000000"

    /// The app icon: 512x512, flat colour, drawn from the palette — the agent's blue square
    /// and the human's green one on the product's black. PNG rather than SVG because iOS
    /// takes only PNG for a home-screen icon, and base64 rather than a file because the
    /// process serving it has no assets directory it can count on.
    let iconPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAgAAAAIACAIAAAB7GkOtAAAF80lEQVR42u3VsQ0AIAhFQfZwejuXsmESW0o7I7nLH4HwIgAA"
        + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
        + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
        + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACAa2Om"
        + "fTQXCwiAAAAIgAAACIAAAAiAAAAIgAAACIAAAAiAAAACYAIACIAJACAAJgCAAJgAAAJgAgAIgAkAIAAmAIAAmAAAAmACAAiA"
        + "CQAgACYAgACYAAACYAIACIAJACAAJgCAAJgAAAIgAAACIAAAAiAAAAIgAAACIAAAAiAAAAIgAAACIACAAJgAAAJgAgAIgAkA"
        + "IAAmAIAAmAAAAmACAAiACQAgACYAgACYAAACYAIACIAJACAAJgCAAJgAAAJgAgAIgAkAIAACACAAAgAgAAIAIAACACAAAgAg"
        + "AAIAIAACACAAAgAIgAkAIAAmAIAAmAAAAmACAAiACQAgACYAgACYAAACYAIACIAJACAAJgCAAJgAAAJgAgAIgAkAIAAmAIAA"
        + "mAAAAmACAAiAAAAIgAAACIAAAAiAAAAIgAAACIAAAAiAAAB4qQIACIAJACAAJgCAAJgAAAJgAgAIgAkAIAAmAIAAmAAAAmAC"
        + "AAiACQAgACYAgACYAAACYAIACIAJACAAJgCAAJgAAAIgAAACIAAAAiAAAAIgAAACIAAAAiAAAAIgAAACIACAAJgAAAJgAgAI"
        + "gAkAIAAmAIAAmAAAAmACAAiACQAgACYAgACYAAACYAIACIAJACAAJgCAAJgAAAJgAgAIgAkAIAAmAIAACACAAAgAgAAIAIAA"
        + "CACAAAgAgAAIAIAACAAgAL6qAAACYAIACIAJACAAJgCAAJgAAAJgAgAIgAkAIAAmAIAAmAAAAmACAAiACQAgACYAgACYAAAC"
        + "YAIACIAJACAAJgCAAAgAgAAIAIAACAAAVGvbywEIgAAACIAAAAiAAAAIgAAACIAAAAiAAAAIgAAACIAAAAiAAAAIgAAACIAA"
        + "AHjBAgAIgAkAIAAmAIAAmAAAAmACAAiACQAgACYAgACYAAACYAIACIAJACAAJgCAAJgAAAIgAAACIAAAAiAAAAIgAAACIAAA"
        + "AiAAAAIgAAACIAAAAiAAAAIgAAACIAAAAiAAgACYAAACYAIACIAJACAAJgCAAJgAAAJgAgAIgAkAIAAmAIAAmAAAAmACAAiA"
        + "CQAgAAIAIAACACAAAgAgAAIAIAACACAAAgAgAAIAIAACACAAAgAgAAIAIAACACAAAgAIgC8sAIAAmAAAAmACAAiACQAgACYA"
        + "gACYAAACYAIACIAJACAAJgCAAJgAAAJgAgAIgAkAIAACACAAAgAgAAIAIAACACAAAgAgAAIAIAACACAAAgAgAAIAIAACACAA"
        + "AgAgAAIACIAJACAAJgCAAJgAAAJgAgAIgAkAIAAmAIAAmAAAAmACAAiACQAgACYAgACYAAACIAAAAiAAAAIgAAACIAAAAiAA"
        + "AAIgAAACIAAAAiAAAAIgAAACIAAAAiAAAAIgAIAAmAAAAmACAAiACQAgACYAgACYAAACYAIACIAJACAAJgCAAJgAAAJgAgAI"
        + "gAkAIAAmAIAACACAAAgAgAAIAIAACACAAAgAgAAIAIAACACAAAgAgAAIAIAACAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
        + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
        + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAQCsHrIJLXSSN3hoAAAAASUVORK5CYII="

    /// The manifest. `start_url` and the icon are relative to the MANIFEST's own address,
    /// which is what makes a path-mounted session install as ITSELF rather than as whatever
    /// sits at the origin root.
    ///
    /// `scope` is the exception, and deliberately the whole origin: a navigation outside the
    /// scope leaves the installed app for the browser, and this shell has one — the sign-in
    /// bounce through the Manager, which lands wherever the Manager is rather than under the
    /// session's mount. A session that owns its origin loses nothing by it (there `/` and
    /// `./` are the same place); a mounted one keeps its login flow inside the app.
    let manifest =
        sprintf
            """{"name":"Yession","short_name":"Yession","start_url":"./","scope":"/","display":"standalone","orientation":"any","background_color":"%s","theme_color":"%s","icons":[{"src":"./%s","sizes":"512x512","type":"image/png","purpose":"any"}]}"""
            ground ground (SessionRoute.relative Icon)

    /// The head tags, given the routes as this document addresses them. Emitted by both
    /// shells; the Manager takes only the icon and the tint (there is nothing to install
    /// about a session list).
    let headTags (manifestUrl: string) (iconUrl: string) =
        String.concat "" [
            sprintf "<link rel=\"manifest\" href=\"%s\">" manifestUrl
            sprintf "<link rel=\"icon\" type=\"image/png\" href=\"%s\">" iconUrl
            sprintf "<link rel=\"apple-touch-icon\" href=\"%s\">" iconUrl
            sprintf "<meta name=\"theme-color\" content=\"%s\">" ground
            "<meta name=\"apple-mobile-web-app-capable\" content=\"yes\">"
            "<meta name=\"apple-mobile-web-app-status-bar-style\" content=\"black\">"
            "<meta name=\"apple-mobile-web-app-title\" content=\"Yession\">"
        ]

    /// The Manager's half: no manifest, because a session list is not a thing to launch
    /// chrome-less — but the same mark in the tab and the same tint on the browser's bars.
    let managerHeadTags (iconUrl: string) =
        String.concat "" [
            sprintf "<link rel=\"icon\" type=\"image/png\" href=\"%s\">" iconUrl
            sprintf "<link rel=\"apple-touch-icon\" href=\"%s\">" iconUrl
            sprintf "<meta name=\"theme-color\" content=\"%s\">" ground
        ]
