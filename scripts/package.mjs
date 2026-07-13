// Package a self-contained Yession distribution for the CURRENT platform:
//
//   dist/yession-<version>-<platform>.tar.gz
//     yession            launcher (bundled Node runtime, no system deps)
//     bin/node           the packaging process's own Node runtime
//     app/out/**         Fable-compiled Session Manager/Process + browser bundle
//     node_modules/**    production dependencies (platform natives included)
//     package.json
//
// CI runs this once per platform runner (linux-x64, darwin-arm64) after the test gate;
// the version is GitVersion-style continuous delivery: 1.0.0-beta.<commits-on-master>.
//
// Usage: node scripts/package.mjs <version>

import { execSync } from 'node:child_process'
import { cpSync, mkdirSync, rmSync, writeFileSync, chmodSync, existsSync } from 'node:fs'

const version = process.argv[2]
if (!version) {
  console.error('usage: node scripts/package.mjs <version>')
  process.exit(1)
}
const platform = `${process.platform === 'darwin' ? 'darwin' : 'linux'}-${process.arch}`
const name = `yession-${version}-${platform}`
const stage = `dist/${name}`

for (const required of ['app/out/Main.js', 'app/out/public/client.js']) {
  if (!existsSync(required)) {
    console.error(`missing ${required} — run \`mise run build\` first`)
    process.exit(1)
  }
}

rmSync('dist', { recursive: true, force: true })
mkdirSync(`${stage}/bin`, { recursive: true })

// The runtime is the packaging process's own Node — no download, always matching.
cpSync(process.execPath, `${stage}/bin/node`)
chmodSync(`${stage}/bin/node`, 0o755)

cpSync('app/out', `${stage}/app/out`, { recursive: true })
// Fable emits the host project's own modules one level above the out dir.
for (const js of execSync('ls app/*.js').toString().trim().split('\n')) {
  cpSync(js, `${stage}/${js}`)
}
cpSync('package.json', `${stage}/package.json`)

// Production dependencies only, resolved on THIS platform so natives are right.
// node-datachannel's install script needs its prebuilt download; keep scripts enabled.
// YESSION_PACKAGE_COPY_NODE_MODULES=1 copies the working tree's node_modules instead
// (for sandboxes whose egress policy blocks the prebuilt download).
if (process.env.YESSION_PACKAGE_COPY_NODE_MODULES === '1') {
  cpSync('node_modules', `${stage}/node_modules`, { recursive: true })
} else {
  execSync(`npm install --omit=dev --prefix ${stage}`, { stdio: 'inherit' })
}

writeFileSync(
  `${stage}/yession`,
  `#!/bin/sh
# Yession ${version} — local-first collaborative sessions.
# Runs from YOUR working directory: session data lands in ./.yession/data, so separate
# directories are separate instances.
DIR="$(cd "$(dirname "$0")" && pwd)"
export YESSION_CLIENT_BUNDLE="$DIR/app/out/public/client.js"
exec "$DIR/bin/node" "$DIR/app/out/Main.js" "$@"
`)
chmodSync(`${stage}/yession`, 0o755)

writeFileSync(`${stage}/VERSION`, version + '\n')
execSync(`tar -czf dist/${name}.tar.gz -C dist ${name}`, { stdio: 'inherit' })
console.log(`packaged dist/${name}.tar.gz`)
