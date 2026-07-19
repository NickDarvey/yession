# Agents

## Communication

Respond terse like smart caveman. All technical substance stay. Only fluff die.

Rules:
- Drop: articles (a/an/the), filler (just/really/basically), pleasantries, hedging
- Fragments OK. Short synonyms. Technical terms exact. Code unchanged.
- Pattern: [thing] [action] [reason]. [next step].
- Not: "Sure! I'd be happy to help you with that."
- Yes: "Bug in auth middleware. Fix:"

Switch level: /caveman lite|full|ultra|wenyan
Stop: "stop caveman" or "normal mode"

Auto-Clarity: drop caveman for security warnings, irreversible actions, user confused. Resume after.

Boundaries: code/commits/PRs written normal.

## Bootstrap

Dev container has Node + .NET on disk but NOT `mise` — and `mise` is the repo interface
(everything is `mise run <task>`). Install it first, ONCE per fresh container:

```
curl -fsSL https://mise.run | sh          # -> /root/.local/bin/mise
export PATH="/root/.local/bin:$PATH"       # every shell that runs mise
mise trust && mise install                 # pinned node 24.16 + dotnet 10 (~235 MB, first run only)
```

On a box with Nix, skip the curl bootstrap entirely: `nix develop` gives Node, the
.NET SDK, and mise from nixpkgs, with mise pointed at those tools (no downloads). See
`flake.nix`.

Then use tasks (`mise tasks` lists them): `mise run test` / `build` / `verify`. `restore`
(npm + dotnet tools) is a `depends` of the others — no need to run it by hand. Do NOT invoke
`dotnet`/`fable`/`esbuild` directly to "run the suite"; go through `mise run` so tool versions
and PATH match CI.

Preinstalled, no action: Chromium at `$PLAYWRIGHT_BROWSERS_PATH` (`/opt/pw-browsers`) — the
`Browser` cap works here. NOT built: the `node-datachannel` WebRTC addon — so `Native`-capped
suites can't run in this container (see Testing).

## Testing

Tests gated by CAPABILITIES the run declares, not folders (`tests/Yession.Tests/Tags.fs`). A
suite runs only when this environment has every capability it needs; otherwise it reports a
skip — never an error. Pass the caps THIS box has as args:

```
mise run test                    # cheap tier: pure/model/protocol on Node. Every PR. Fast.
mise run test -- Browser         # + host-free rich-editor E2E. Needs only Chromium.
mise run test -- Ports Native    # + WebRTC/host suites. Need the node-datachannel addon.
mise run verify                  # == -- Browser Ports Native Docker LiveAgent. Release gate.
```

Capabilities:
- `Browser` — Chromium via the .NET Playwright driver. Pins the .NET CLR runtime.
- `Ports` — binds TCP ports / spawns processes.
- `Native` — the native `node-datachannel` WebRTC addon, loaded by the real Session Process.
  ABSENT in the dev container (not prebuilt), so `Native`-tagged suites (all host-spawning ones)
  can't run here — pass `-- Browser` (not `Native`) and they skip cleanly.
- `Docker` — a reachable daemon. `LiveAgent` — real model credentials.

To eyeball a rich-editor change in a real browser without any of the WebRTC machinery:
`mise run test -- Browser` (drives Chromium against `tests/browser/editor-harness.html`).
The dev container has Chromium but not the addon, so this is the browser signal you CAN get
locally; the full two-peer WebRTC E2E only runs where `Native` is available (CI, `mise run verify`).
