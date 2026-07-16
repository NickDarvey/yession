// Real-browser E2E: the shipped browser client, in actual Chromium, against a real
// Session Process — two browser peers converge on a draft over native WebRTC and see
// the sent message in both timelines; then the client-side IndexedDB persistence is
// proven by wiping the server's data and reloading (the draft can only come back from
// the browser). Event-driven throughout (waitForFunction), with one overall watchdog.
//
// Requires a Chromium install; CHROMIUM_PATH overrides discovery. Exits non-zero on
// any failure so `mise run test` fails loudly.

import { chromium } from 'playwright-core'
import { spawn, execSync } from 'node:child_process'
import { existsSync, rmSync } from 'node:fs'

const PORT = 8180
const BASE = `http://127.0.0.1:${PORT}/`
const WATCHDOG_MS = 120_000

const watchdog = setTimeout(() => {
  console.error(`browser E2E watchdog fired after ${WATCHDOG_MS}ms`)
  process.exit(3)
}, WATCHDOG_MS)

function chromiumPath() {
  if (process.env.CHROMIUM_PATH) return process.env.CHROMIUM_PATH
  const candidates = []
  const root = process.env.PLAYWRIGHT_BROWSERS_PATH || '/opt/pw-browsers'
  if (existsSync(root)) {
    try {
      candidates.push(
        ...execSync(`find ${root} -name chrome -type f 2>/dev/null || true`).toString().trim().split('\n').filter(Boolean))
    } catch {}
  }
  for (const c of ['/usr/bin/chromium', '/usr/bin/chromium-browser', '/usr/bin/google-chrome']) candidates.push(c)
  const found = candidates.find(existsSync)
  if (!found) {
    console.error('no Chromium found; set CHROMIUM_PATH')
    process.exit(4)
  }
  return found
}

const dataDir = 'tests/browser/.data'
rmSync(dataDir, { recursive: true, force: true })

// Start the real product entry (Manager topology) on a test port.
function startHost() {
  const host = spawn('node', ['app/out/Main.js'], {
    env: { ...process.env, YESSION_PORT: String(PORT), YESSION_DATA_DIR: dataDir },
    stdio: ['ignore', 'pipe', 'inherit'],
  })
  const ready = new Promise((resolve, reject) => {
    host.stdout.on('data', (d) => { if (String(d).includes('launched at')) resolve() })
    host.on('exit', (code) => { if (code !== null && code !== 0) reject(new Error(`host exited early (${code})`)) })
  })
  return { host, ready }
}
let { host, ready } = startHost()
await ready

const browser = await chromium.launch({
  executablePath: chromiumPath(),
  // Headless sandboxes stall ICE gathering when host candidates hide behind mDNS.
  args: ['--disable-features=WebRtcHideLocalIpsWithMdns'],
})
try {
  const pageA = await browser.newPage()
  const pageB = await browser.newPage()
  await pageA.goto(BASE)
  await pageB.goto(BASE)

  // Both browser peers reach Connected over native WebRTC.
  const connected = `document.querySelector('[data-connection]')?.textContent === 'Connected'`
  await pageA.waitForFunction(connected)
  await pageB.waitForFunction(connected)

  // A types in its always-present composer (one draft per client, materialised on the
  // first keystroke); B converges on the same body — it renders as a peer's draft there.
  await pageA.waitForSelector('textarea[data-draft-input]')
  await pageA.fill('textarea[data-draft-input]', 'hello from a real browser')
  await pageB.waitForFunction(
    `[...document.querySelectorAll('textarea[data-draft-input]')].some(t => t.value === 'hello from a real browser')`)

  // A sends; both timelines show the immutable message (from events, not Yjs). The
  // body node carries its own hook, so the snapshotted text is asserted exactly,
  // independent of the author/status markup around it.
  await pageA.click('[data-send-draft]')
  const inTimeline =
    `[...document.querySelectorAll('[data-conversation] [data-message-body]')]` +
    `.some(m => m.textContent === 'hello from a real browser')`
  await pageA.waitForFunction(inTimeline)
  await pageB.waitForFunction(inTimeline)

  console.log('browser E2E: two real browser peers converged and saw the sent message')

  // Client-side persistence (Step 20): A types a NEW draft (its composer cleared when the
  // first one sent), then the server is killed and its data wiped. After A reloads against
  // the fresh server, the draft can only have come back from the browser's IndexedDB — and
  // it re-syncs to B via the server.
  await pageA.waitForFunction(
    `document.querySelectorAll('textarea[data-draft-input]').length === 1`)
  await pageA.fill('textarea[data-draft-input]', 'persisted in the browser')
  await pageA.waitForFunction(
    `[...document.querySelectorAll('textarea[data-draft-input]')].some(t => t.value === 'persisted in the browser')`)

  const exited = new Promise((resolve) => host.on('exit', resolve))
  host.kill('SIGKILL')
  await exited
  rmSync(dataDir, { recursive: true, force: true })
  ;({ host, ready } = startHost())
  await ready

  await pageA.reload()
  await pageA.waitForFunction(connected)
  await pageA.waitForFunction(
    `[...document.querySelectorAll('textarea[data-draft-input]')]` +
    `.some(t => t.value === 'persisted in the browser')`)

  await pageB.reload()
  await pageB.waitForFunction(connected)
  await pageB.waitForFunction(
    `[...document.querySelectorAll('textarea[data-draft-input]')]` +
    `.some(t => t.value === 'persisted in the browser')`)

  console.log('browser E2E: the draft survived a full server wipe via the browser doc store')

  // The store is keyed by SESSION (embedded in the served page), not by address: the
  // page carries the id and the IndexedDB database name derives from it.
  const sessionId = await pageA.evaluate(
    () => document.querySelector('meta[name="yession-session"]')?.getAttribute('content'))
  if (!sessionId) throw new Error('the bootstrap page must embed the session id')
  const dbNames = (await pageA.evaluate(() => indexedDB.databases().then(dbs => dbs.map(d => d.name))))
  if (!dbNames.includes(`yession/session/${sessionId}`)) {
    throw new Error(`expected a session-keyed doc store, found: ${dbNames.join(', ')}`)
  }
  console.log(`browser E2E: the doc store is keyed by session (${sessionId})`)
} finally {
  await browser.close()
  host.kill('SIGKILL')
  clearTimeout(watchdog)
}
process.exit(0)
