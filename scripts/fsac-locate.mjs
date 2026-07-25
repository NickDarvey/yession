// Find fsautocomplete without requiring the caller to be inside `devenv shell`.
//
// Agents are usually launched from the plain container shell, so PATH does not have the
// devenv packages on it. devenv publishes the built environment two ways, and both work
// from outside the shell:
//
//   $DEVENV_PROFILE      set when you ARE inside the shell; documented as the store path
//                        "useful for teaching other programs about /bin"
//   .devenv/profile      a symlink to that same store path, refreshed on every `devenv
//                        shell`. Gitignored local state, but it is what makes this work
//                        for a process devenv did not spawn.
//
// Resolution is deliberately re-run on every attempt rather than cached at import: an
// agent that bootstraps the environment mid-session should not have to restart anything.

import { existsSync, statSync, accessSync, constants } from 'node:fs'
import path from 'node:path'

const BIN = 'fsautocomplete'

const isExecutable = (p) => {
  try {
    if (!existsSync(p) || !statSync(p).isFile()) return false
    accessSync(p, constants.X_OK)
    return true
  } catch { return false }
}

function findOnPath(name) {
  for (const dir of (process.env.PATH ?? '').split(path.delimiter)) {
    if (!dir) continue
    const p = path.join(dir, name)
    if (isExecutable(p)) return p
  }
  return null
}

/**
 * Locate fsautocomplete. Returns { path, source, binDir } or null.
 * Order matters: an explicit override wins, then the devenv environment (whose version is
 * pinned by devenv.nix), then whatever happens to be on PATH.
 */
export function locateFsac(root) {
  const hit = (p, source) => ({ path: p, source, binDir: path.dirname(p) })

  if (process.env.FSAC_BIN) {
    // Honour the override even if it looks wrong — the caller gets a spawn error naming
    // their own value, which is a better clue than silently falling through to a
    // different binary than the one they asked for.
    return hit(process.env.FSAC_BIN, 'FSAC_BIN')
  }
  if (process.env.DEVENV_PROFILE) {
    const p = path.join(process.env.DEVENV_PROFILE, 'bin', BIN)
    if (isExecutable(p)) return hit(p, 'DEVENV_PROFILE (inside devenv shell)')
  }
  const profileLink = path.join(root, '.devenv', 'profile', 'bin', BIN)
  if (isExecutable(profileLink)) return hit(profileLink, '.devenv/profile')

  const onPath = findOnPath(BIN)
  if (onPath) return hit(onPath, 'PATH')

  return null
}

/**
 * The environment fsautocomplete must be spawned with.
 *
 * Finding the binary is not enough: FSAC loads projects by shelling out to MSBuild, so
 * without `dotnet` on PATH it starts cleanly, reports zero projects, and answers every
 * query with "Couldn't find <file> in LoadedProjects" — which reads like a broken
 * workspace rather than a missing toolchain. Putting the profile's bin directory on PATH
 * fixes it; the nix dotnet wrapper supplies DOTNET_ROOT and friends by itself, so nothing
 * else needs reconstructing here.
 */
export function fsacEnv(found) {
  const prefix = found.binDir
  const current = process.env.PATH ?? ''
  return { ...process.env, PATH: current ? `${prefix}${path.delimiter}${current}` : prefix }
}

/** Why it wasn't found, and the shortest way for the reader to fix it. */
export function fsacNotFoundMessage(root) {
  const profileLink = path.join(root, '.devenv', 'profile')
  const built = existsSync(profileLink)
  return built
    ? `fsautocomplete not found: ${path.join(profileLink, 'bin', BIN)} is missing. The devenv ` +
      `profile exists but predates fsautocomplete being added to devenv.nix — re-enter the ` +
      `environment (\`devenv shell\`) to rebuild it.`
    : `fsautocomplete not found: no devenv profile at ${profileLink}. Build the environment ` +
      `once (see Bootstrap in AGENTS.md — \`devenv shell -- build\`) and it will be picked up ` +
      `automatically; no need to restart this session. Or set FSAC_BIN to a fsautocomplete binary.`
}
