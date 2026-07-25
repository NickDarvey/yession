#!/usr/bin/env node
// Benchmark fsautocomplete (the F# language server behind Ionide, and behind
// scripts/fsharp-lsp-mcp.mjs) against ripgrep, for the navigation questions an agent
// actually asks: where is this defined, what references it, what type is it.
//
// Run inside `devenv shell` (fsautocomplete is a devenv package):
//     node scripts/lsp-bench.mjs
//
// Reports three things, because they trade off against each other:
//   - cold:      time from process start to the first usable answer (paid per session)
//   - warm:      per-query latency once loaded (paid per lookup)
//   - precision: how many results each approach returns for the same question
//
// Measured in a 4-core Claude Code container, July 2026, FSAC 0.83.0:
//   cold ~20s and ~1.4GB RSS; warm definition ~2ms vs ripgrep ~7ms; warm references
//   ~80ms, but the FIRST references call costs ~11s (whole-workspace check).
//   Precision: rg '\bSessionId\b' = 321 hits; FSAC = 97 references to the type.

import { spawn, execFileSync } from 'node:child_process'
import { readFileSync } from 'node:fs'
import { pathToFileURL } from 'node:url'
import path from 'node:path'

const ROOT = path.resolve(import.meta.dirname, '..')
const FSAC = process.env.FSAC_BIN || 'fsautocomplete'
const REPS = +(process.env.BENCH_REPS ?? 5)

const now = () => Number(process.hrtime.bigint()) / 1e6
const T0 = now()
const since = () => +(now() - T0).toFixed(1)
const log = (...a) => console.error(`[${String(since()).padStart(8)}ms]`, ...a)
const median = (xs) => [...xs].sort((a, b) => a - b)[Math.floor(xs.length / 2)]

// ---------------------------------------------------------------------------
// Targets. Chosen to exercise the cases where text search is weakest: a
// cross-project jump, and a name shared by several distinct symbols.
// ---------------------------------------------------------------------------

const TARGETS = [
  { name: 'cross-project definition', file: 'app/Backends.fs', symbol: 'value', match: 'SessionId.value',
    note: 'app/ -> src/Yession.Domain/Identity.fs' },
  { name: 'type declaration site', file: 'src/Yession.Domain/Identity.fs', symbol: 'SessionId', match: 'type SessionId',
    note: 'large reference set' },
]

// Resolve each target to a 0-based LSP position by locating the line containing `match`
// and the `symbol` token within it, so the benchmark keeps working when the files move.
for (const t of TARGETS) {
  const lines = readFileSync(path.join(ROOT, t.file), 'utf8').split(/\r?\n/)
  const idx = lines.findIndex((l) => l.includes(t.match))
  if (idx < 0) throw new Error(`benchmark target not found: ${t.match} in ${t.file}`)
  const col = lines[idx].indexOf(t.symbol, Math.max(0, lines[idx].indexOf(t.match)))
  t.line = idx
  t.char = col + Math.floor(t.symbol.length / 2)
  t.source = lines[idx].trim()
}

// ---------------------------------------------------------------------------
// Minimal LSP client
// ---------------------------------------------------------------------------

const proc = spawn(FSAC, [], { cwd: ROOT, stdio: ['pipe', 'pipe', 'pipe'] })
let stderrTail = ''
proc.on('error', (e) => { console.error(`cannot run ${FSAC}: ${e.message}\nRun inside 'devenv shell'.`); process.exit(1) })
proc.stderr.on('data', (d) => { stderrTail = (stderrTail + d).slice(-4000) })
proc.on('exit', (c) => { if (c) { log(`FSAC exited ${c}`); console.error(stderrTail) } })

let buf = Buffer.alloc(0), nextId = 1
const pending = new Map()
let projectsLoaded = false

proc.stdout.on('data', (chunk) => {
  buf = Buffer.concat([buf, chunk])
  for (;;) {
    const sep = buf.indexOf('\r\n\r\n')
    if (sep < 0) return
    const m = /Content-Length:\s*(\d+)/i.exec(buf.subarray(0, sep).toString('ascii'))
    if (!m) { buf = buf.subarray(sep + 4); continue }
    const len = +m[1]
    if (buf.length < sep + 4 + len) return
    const body = buf.subarray(sep + 4, sep + 4 + len).toString('utf8')
    buf = buf.subarray(sep + 4 + len)
    let msg; try { msg = JSON.parse(body) } catch { continue }
    // Responses have no `method`; server-originated requests reuse our id space.
    if (msg.method === undefined && pending.has(msg.id)) {
      const { resolve, t } = pending.get(msg.id)
      pending.delete(msg.id)
      resolve({ result: msg.result, ms: +(now() - t).toFixed(1) })
    } else if (msg.method && msg.id !== undefined) {
      send({ jsonrpc: '2.0', id: msg.id, result: null })
    } else if (msg.method === 'fsharp/notifyWorkspace') {
      let c = msg.params?.content
      try { c = JSON.parse(c) } catch { return }
      if (c?.Kind === 'workspaceLoad' && c?.Data?.Status === 'finished') {
        projectsLoaded = true
        log(`workspace loaded at ${since()}ms`)
      }
    }
  }
})

const send = (o) => { const s = JSON.stringify(o); proc.stdin.write(`Content-Length: ${Buffer.byteLength(s, 'utf8')}\r\n\r\n${s}`) }
const request = (method, params) => new Promise((resolve) => {
  const id = nextId++
  pending.set(id, { resolve, t: now() })
  send({ jsonrpc: '2.0', id, method, params })
})
const uri = (rel) => pathToFileURL(path.join(ROOT, rel)).href
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))
const count = (r) => (Array.isArray(r) ? r.length : r ? 1 : 0)

// ---------------------------------------------------------------------------
// Cold: process start -> first usable answer
// ---------------------------------------------------------------------------

log(`starting ${FSAC}…`)
const init = await request('initialize', {
  processId: process.pid,
  rootUri: pathToFileURL(ROOT).href,
  rootPath: ROOT,
  workspaceFolders: [{ uri: pathToFileURL(ROOT).href, name: 'yession' }],
  capabilities: {
    workspace: { workspaceFolders: true, configuration: true },
    textDocument: { synchronization: { didSave: true }, definition: { linkSupport: false }, references: {}, hover: {} },
  },
  initializationOptions: { AutomaticWorkspaceInit: true, EnableAnalyzers: false },
})
log(`initialize -> ${init.ms}ms`)
send({ jsonrpc: '2.0', method: 'initialized', params: {} })

for (const t of TARGETS) {
  send({ jsonrpc: '2.0', method: 'textDocument/didOpen', params: {
    textDocument: { uri: uri(t.file), languageId: 'fsharp', version: 1, text: readFileSync(path.join(ROOT, t.file), 'utf8') },
  } })
}

const primary = TARGETS[0]
let polls = 0
for (;;) {
  polls++
  const r = await request('textDocument/definition', {
    textDocument: { uri: uri(primary.file) }, position: { line: primary.line, character: primary.char },
  })
  if (count(r.result)) break
  if (since() > 300_000) { console.error('timed out waiting for FSAC to become ready'); process.exit(2) }
  await sleep(500)
}
const coldMs = since()
log(`first usable answer at ${coldMs}ms (${polls} poll(s))`)

// The first references call additionally forces a whole-workspace check; measure it
// separately, because it is a second cold cost most benchmarks hide inside the average.
const firstRefs = await request('textDocument/references', {
  textDocument: { uri: uri(primary.file) }, position: { line: primary.line, character: primary.char },
  context: { includeDeclaration: true },
})
log(`first references call -> ${firstRefs.ms}ms`)

const peakRssMb = (() => {
  try { return Math.round(+execFileSync('ps', ['-o', 'rss=', '-p', String(proc.pid)]).toString().trim() / 1024) }
  catch { return null }
})()

// ---------------------------------------------------------------------------
// Warm
// ---------------------------------------------------------------------------

const warm = []
for (const t of TARGETS) {
  const pos = { textDocument: { uri: uri(t.file) }, position: { line: t.line, character: t.char } }
  for (const [label, method, params] of [
    ['definition', 'textDocument/definition', pos],
    ['references', 'textDocument/references', { ...pos, context: { includeDeclaration: true } }],
    ['hover', 'textDocument/hover', pos],
  ]) {
    const runs = []
    let hits = 0
    for (let i = 0; i < REPS; i++) { const r = await request(method, params); runs.push(r.ms); hits = count(r.result) }
    warm.push({ what: `${label} (${t.name})`, median: median(runs), hits })
  }
}
{
  const runs = []
  let hits = 0
  for (let i = 0; i < REPS; i++) { const r = await request('workspace/symbol', { query: 'SessionId' }); runs.push(r.ms); hits = count(r.result) }
  warm.push({ what: 'workspace symbol search "SessionId"', median: median(runs), hits })
}

// ---------------------------------------------------------------------------
// ripgrep baseline, same questions
// ---------------------------------------------------------------------------

const rg = (args) => {
  const t = now()
  let out = ''
  for (let i = 0; i < REPS; i++) {
    const s = now()
    try { out = execFileSync('rg', args, { cwd: ROOT }).toString() } catch { out = '' }
    if (i === 0) void s
  }
  const ms = (now() - t) / REPS
  return { ms: +ms.toFixed(1), hits: out.split('\n').filter(Boolean).length }
}
const GLOBS = ['--glob', '!node_modules', '--glob', '*.fs']
const grepRows = [
  { what: 'rg "SessionId" (what you actually type)', ...rg(['-n', ...GLOBS, 'SessionId', '.']) },
  { what: 'rg "\\bSessionId\\b" (word boundary)', ...rg(['-n', ...GLOBS, '\\bSessionId\\b', '.']) },
  { what: 'rg "^type SessionId" (guess the decl form)', ...rg(['-n', ...GLOBS, '^type SessionId\\b', '.']) },
]

// ---------------------------------------------------------------------------
// Report
// ---------------------------------------------------------------------------

const pad = (s, n) => String(s).padEnd(n)
const padL = (s, n) => String(s).padStart(n)
console.log(`\n${'='.repeat(78)}\nF# navigation: fsautocomplete vs ripgrep\n${'='.repeat(78)}`)
console.log(`\nrepo: ${TARGETS.length} probe targets, ${REPS} repetitions per warm query\n`)

console.log('COLD (paid once per language-server process)')
console.log(`  ${pad('process start -> first usable answer', 48)} ${padL(Math.round(coldMs) + 'ms', 10)}`)
console.log(`  ${pad('first references call (workspace-wide check)', 48)} ${padL(Math.round(firstRefs.ms) + 'ms', 10)}`)
if (peakRssMb) console.log(`  ${pad('resident memory once loaded', 48)} ${padL(peakRssMb + 'MB', 10)}`)

console.log('\nWARM (median per query)')
for (const r of warm) console.log(`  ${pad(r.what, 48)} ${padL(r.median.toFixed(1) + 'ms', 10)}  ${r.hits} result(s)`)

console.log('\nRIPGREP (same questions, text only)')
for (const r of grepRows) console.log(`  ${pad(r.what, 48)} ${padL(r.ms.toFixed(1) + 'ms', 10)}  ${r.hits} hit(s)`)

const refRow = warm.find((r) => r.what.startsWith('references (type declaration'))
const wordRow = grepRows[1]
if (refRow && wordRow) {
  console.log(`\nPRECISION\n  ripgrep returns ${wordRow.hits} hits for "SessionId"; the compiler finds ${refRow.hits} references`)
  console.log(`  to the type itself. The difference is same-named modules, DU cases, record`)
  console.log(`  fields, strings and comments — lines you would otherwise read to discard.`)
}
console.log()

proc.kill()
process.exit(0)
