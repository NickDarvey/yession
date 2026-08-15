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

    /// The service worker, for one build. Registered from the client bundle; served at the
    /// mount root, because a worker controls its own path and below (Plan 20).
    ///
    /// It keeps exactly two things, and the split is the whole design:
    ///
    /// * **the shell** — network-first, because it is `no-cache` for a real reason (it NAMES
    ///   the fingerprinted assets, so a stale one pins the whole UI to a build that is gone).
    ///   The cached copy is the fallback, and it is what makes a cold open possible at all.
    /// * **fingerprinted assets** — cache-first, for ever, because their address pins their
    ///   bytes. Kept on the way past rather than pre-fetched from a list: the build already
    ///   decides what it ships, and a list here would be a second place that has to agree.
    ///
    /// And it keeps NOTHING else. Not the event log — the page owns that cache directly and a
    /// copy here would be the redundant spare. Not `/me`, `/signal`, `/queries`, `/claude*`:
    /// those are liveness questions, and a cached answer to "can I reach this session" is a
    /// wrong answer.
    ///
    /// `!response.ok` counts as failure for the shell, and that is not defensive coding. A
    /// session behind an operator's proxy that is still up answers a DEAD session with 502 —
    /// a perfectly successful fetch carrying an error — and a worker that only caught thrown
    /// requests would serve that 502 as the page, which is the exact case this exists for.
    ///
    /// `build` names the cache, so a new build is a byte-different worker: the browser
    /// installs it, and `activate` drops every cache that is not this build's.
    let serviceWorker (build: string) (shellUrl: string) (assetsPrefix: string) (assetUrls: string list) =
        sprintf
            """const BUILD = '%s'
const CACHE = 'yession/shell/' + BUILD
const SHELL = new URL('%s', self.registration.scope).href
const ASSETS = new URL('%s', self.registration.scope).href

// What this build ships, named by the server that ships it — never a list kept by hand.
const KEEP = %s

self.addEventListener('install', (e) => {
  // FETCHED here rather than kept on the way past, and that is the whole difference between
  // working and not: the first navigation happens before this worker controls anything, so
  // nothing it would have "kept as it went" was ever seen by it. A client that installed a
  // worker and then went offline would have an empty cache and a dead page.
  //
  // Each file settles on its own. A set where one entry 404s is a set that should still open
  // offline missing that one thing, not an install that fails and leaves nothing at all.
  e.waitUntil((async () => {
    const cache = await caches.open(CACHE)
    await Promise.all(
      [SHELL, ...KEEP.map((p) => new URL(p, self.registration.scope).href)].map(async (href) => {
        try {
          const response = await fetch(href, { cache: 'reload' })
          if (response.ok) await cache.put(new Request(href), response)
        } catch (err) { /* offline at install time: the next load will fill it */ }
      }))
    // Take over at once: the alternative is a client that installed a worker and is still
    // waiting for a tab it will never close.
    await self.skipWaiting()
  })())
})

self.addEventListener('activate', (e) => {
  e.waitUntil((async () => {
    for (const name of await caches.keys()) {
      if (name.startsWith('yession/shell/') && name !== CACHE) await caches.delete(name)
    }
    await self.clients.claim()
  })())
})

// The shell, and ONLY the shell. A session's other navigations are the sign-in bounce
// (`/login`, `/callback`), and inside a worker a navigation request carries
// `redirect: 'manual'` — so fetching one returns an opaque redirect, whose `status` is 0 and
// whose `ok` is false. Treating that as a failure (it looks exactly like one) swallowed the
// bounce and left the client on a page that never arrived. Nothing here has any business
// touching them: they are the one part of this surface that MUST reach the network.
const isShell = (u) => {
  const asked = new URL(u)
  const shell = new URL(SHELL)
  return asked.origin === shell.origin && asked.pathname === shell.pathname
}

const keep = async (request, response) => {
  if (response && response.ok) {
    const cache = await caches.open(CACHE)
    await cache.put(request, response.clone())
  }
  return response
}

self.addEventListener('fetch', (e) => {
  const request = e.request
  if (request.method !== 'GET') return
  const url = request.url

  if (request.mode === 'navigate' && isShell(url)) {
    e.respondWith((async () => {
      try {
        const fresh = await fetch(request)
        if (!fresh.ok) throw new Error('shell answered ' + fresh.status)
        return await keep(new Request(SHELL), fresh)
      } catch (err) {
        const kept = await caches.match(new Request(SHELL))
        // The one moment this worker makes a decision nobody can otherwise see: the session
        // did not answer, and either there is a shell kept here or the page is about to be a
        // browser error. Debugging it without this line means inferring it from a timeout,
        // which cost three full runs of the gate once. Open devtools -> Application ->
        // Service Workers to read it; Playwright cannot (no `ServiceWorkers` on a context).
        console.debug('yession/sw: shell unreachable', { asked: request.url, reason: String(err), served: kept ? 'kept copy' : 'nothing kept' })
        if (kept) return kept
        throw err
      }
    })())
    return
  }

  if (url.startsWith(ASSETS)) {
    e.respondWith((async () => {
      const kept = await caches.match(request)
      if (kept) return kept
      return await keep(request, await fetch(request))
    })())
  }
})
"""
            build
            shellUrl
            assetsPrefix
            (assetUrls |> List.map (sprintf "'%s'") |> String.concat "," |> sprintf "[%s]")

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
