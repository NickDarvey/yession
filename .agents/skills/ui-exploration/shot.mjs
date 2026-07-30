// Honest headless screenshots + page measurement over CDP.
//
// Headless Chromium clamps its window to a ~500px minimum width, so `--window-size=390
// --screenshot` renders a 500px layout and crops the image — mobile bugs vanish. This
// driver uses Emulation.setDeviceMetricsOverride (a true device viewport) and prints
// {vw, docW, overflowX} so a lying capture is self-evident: trust the shot only when
// vw equals the width you asked for.
//
//   node shot.mjs <url> <width> <height> <out.png> [expression]
//
// The optional expression is evaluated in the page after load (return a JSON string) —
// measure real boxes instead of theorizing from CSS. Pass /dev/null as out.png to only
// measure. Chromium's path matches this container (see AGENTS.md); adjust if needed.
import { spawn } from 'node:child_process'
import { writeFileSync } from 'node:fs'

const chrome = process.env.CHROME_BIN ?? '/opt/pw-browsers/chromium-1194/chrome-linux/chrome'
const [url, w, h, out, expression] = [process.argv[2], Number(process.argv[3]), Number(process.argv[4]), process.argv[5], process.argv[6]]
if (!url || !w || !h || !out) { console.error('usage: node shot.mjs <url> <width> <height> <out.png> [expression]'); process.exit(2) }

const port = 9333
const proc = spawn(chrome, ['--headless=new', '--no-sandbox', '--disable-gpu', `--remote-debugging-port=${port}`, 'about:blank'], { stdio: 'ignore' })
await new Promise(r => setTimeout(r, 1500))

const list = await (await fetch(`http://127.0.0.1:${port}/json`)).json()
const ws = new WebSocket(list[0].webSocketDebuggerUrl)
let id = 0
const pending = new Map()
const send = (method, params) => new Promise(res => { const n = ++id; pending.set(n, res); ws.send(JSON.stringify({ id: n, method, params })) })
ws.onmessage = e => { const m = JSON.parse(e.data); if (m.id && pending.has(m.id)) { pending.get(m.id)(m.result); pending.delete(m.id) } }
await new Promise(r => { ws.onopen = r })

await send('Emulation.setDeviceMetricsOverride', { width: w, height: h, deviceScaleFactor: 2, mobile: w < 600 })
await send('Page.enable', {})
await send('Page.navigate', { url })
await new Promise(r => setTimeout(r, 2500))

const metrics = await send('Runtime.evaluate', {
  expression: `JSON.stringify({ vw: innerWidth, docW: document.documentElement.scrollWidth, overflowX: document.documentElement.scrollWidth > innerWidth })`,
  returnByValue: true })
console.log(metrics.result.value)
if (expression) {
  const extra = await send('Runtime.evaluate', { expression, returnByValue: true })
  console.log(extra.result.value ?? JSON.stringify(extra))
}
if (out !== '/dev/null') {
  const shot = await send('Page.captureScreenshot', { format: 'png' })
  writeFileSync(out, Buffer.from(shot.data, 'base64'))
  console.log('wrote', out)
}
ws.close(); proc.kill(); process.exit(0)
