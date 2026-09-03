#!/usr/bin/env node
// sessions-map — the Manager's session registry, rendered into whatever your reverse proxy
// reads, and kept there.
//
// A fronted deployment (docs/deployment.md §Addressing) answers for its sessions at
// `YESSION_SESSION_URL`, a template over `{id}` and `{port}`. Something has to turn that
// template into the proxy's own routing, and the port half changes on every launch — so it is
// a process rather than a file somebody edits. This is that process: it holds
// `/sessions/stream` open, and on every frame renders each running session through a
// template written in the same two placeholders, writes the result to one file, and — where
// the proxy does not watch its config on its own — runs a reload command.
//
//   node main.mjs --manager http://127.0.0.1:8321 --as proxy-map \
//     --template '@s_{id} path /s/{id} /s/{id}/*
//   handle @s_{id} {
//   	reverse_proxy 127.0.0.1:{port}
//   }' \
//     --out /var/lib/yession/proxy/sessions.caddy
//
// Level-based, never delta-based: a frame is the WHOLE running set, so the file is rewritten
// from scratch each time and a missed frame costs nothing — the next one is the truth again.
// Rewritten only when the rendering changed, atomically (write beside, rename over), so a
// proxy re-reading it never sees half a file and a quiet Manager never churns a watcher.
//
// Two things the stream's shape decides, and this file follows rather than re-decides:
//
//   * The stream ending is NOT the sessions ending. A Manager restart closes it, and the
//     reconnect's first frame is a fresh snapshot that heals whatever was missed; emptying
//     the map on every EOF would unmap and instantly remap healthy sessions for nothing.
//   * A connection REFUSED is. Sessions are the Manager's child processes and cannot outlive
//     it, so a Manager nothing can reach means no session is reachable either — that is the
//     one case where the desired set is genuinely empty, and the map is written empty.
//
// The stream is gated by the Manager's trust rule like every management route. Under
// `--auth trusted-headers` a header-less subscribe is a 401 with no frames, so this asserts a
// subject of its own (`--as`) from inside the loopback trust boundary the proxy defines; under
// `--auth localhost` the header is read by nothing and costs nothing. A 401 is logged and
// retried, never treated as "no sessions": a misconfigured header must not unmap a deployment.
//
// Node only — no dependencies, so it runs wherever the Manager does, and nothing here reads
// Yession's own code: the contract is the stream and the two placeholders, both documented.

import { parseArgs } from 'node:util'
import { writeFile, rename, readFile, mkdir } from 'node:fs/promises'
import { exec } from 'node:child_process'
import { dirname } from 'node:path'

const usage = `usage: node main.mjs --template TEXT --out PATH [--manager URL] [--as SUBJECT] [--reload CMD]

  --template TEXT   rendered once per running session; {id} and {port} are the session's
  --out PATH        the file the rendering is written to (atomically, only when it changed)
  --manager URL     the Manager (default http://127.0.0.1:8321)
  --as SUBJECT      the x-yession-user this subscriber asserts (default sessions-map)
  --reload CMD      run through the shell after every write, for a proxy that does not
                    watch its own config
  --separator TEXT  between renderings (default a newline)
  --empty TEXT      what to write when nothing is running (default nothing at all); a proxy
                    that warns on an empty file gets a comment here`

let options
try {
  options = parseArgs({
    options: {
      template: { type: 'string' },
      out: { type: 'string' },
      manager: { type: 'string', default: 'http://127.0.0.1:8321' },
      as: { type: 'string', default: 'sessions-map' },
      reload: { type: 'string' },
      separator: { type: 'string', default: '\n' },
      empty: { type: 'string', default: '' },
      help: { type: 'boolean', short: 'h' },
    },
    strict: true,
  }).values
} catch (error) {
  fail(error.message)
}
if (options.help) {
  console.log(usage)
  process.exit(0)
}
if (!options.template) fail('--template is required')
if (!options.out) fail('--out is required')
// A template naming neither placeholder renders the same line for every session, so the
// proxy would route them all to one place — the same refusal the Manager makes of a
// `YESSION_SESSION_URL` with no placeholder, for the same reason.
if (!options.template.includes('{id}') && !options.template.includes('{port}')) {
  fail('--template names neither {id} nor {port}, so every session would render alike')
}

const manager = options.manager.replace(/\/+$/, '')
const stream = `${manager}/sessions/stream`
const reconnectDelayMs = 2000

function fail(message) {
  console.error(`sessions-map: ${message}\n\n${usage}`)
  process.exit(64) // EX_USAGE
}

function log(message) {
  console.error(`sessions-map: ${message}`)
}

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms))

/**
 * One frame, rendered: the running sessions in id order, each through the template — or
 * `--empty` when there are none, which is also what "no Manager" renders as.
 */
function render(frame) {
  const sessions = (Array.isArray(frame?.sessions) ? frame.sessions : [])
    .filter((s) => typeof s.id === 'string' && Number.isInteger(s.port))
    .sort((a, b) => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0))
  if (sessions.length === 0) return options.empty
  return sessions
    .map((s) => options.template.replaceAll('{id}', s.id).replaceAll('{port}', String(s.port)))
    .join(options.separator)
}

/** What the file says now, or null when there is no file. */
async function current() {
  try {
    return await readFile(options.out, 'utf8')
  } catch (error) {
    if (error.code === 'ENOENT') return null
    throw error
  }
}

/**
 * Put `text` in the out file if it is not there already. Beside-then-rename, so a reader
 * never sees a partial file; `mkdir -p` first, because the deployment owns the directory's
 * lifecycle and this process should not fail its first write on a fresh box.
 */
async function publish(text, why) {
  if ((await current()) === text) return false
  await mkdir(dirname(options.out), { recursive: true })
  const beside = `${options.out}.${process.pid}.tmp`
  await writeFile(beside, text, 'utf8')
  await rename(beside, options.out)
  log(`wrote ${options.out} (${why})`)
  if (options.reload) await reload()
  return true
}

function reload() {
  return new Promise((resolve) => {
    exec(options.reload, (error, stdout, stderr) => {
      if (error) log(`reload failed (${error.code ?? error.signal}): ${options.reload}\n${stderr || stdout}`.trimEnd())
      resolve()
    })
  })
}

/**
 * Hold the stream open, applying each frame. Resolves when the stream ends (reconnect), and
 * throws when no connection could be made at all (the Manager is gone).
 */
async function follow(signal) {
  const response = await fetch(stream, {
    headers: { accept: 'text/event-stream', 'x-yession-user': options.as },
    signal,
  })
  if (!response.ok) {
    const body = (await response.text().catch(() => '')).trim()
    log(`${stream} answered ${response.status}${body ? `: ${body}` : ''} — keeping the map as it is`)
    return
  }
  const decoder = new TextDecoder()
  let buffered = ''
  let data = []
  for await (const chunk of response.body) {
    buffered += decoder.decode(chunk, { stream: true })
    let newline
    while ((newline = buffered.indexOf('\n')) >= 0) {
      const line = buffered.slice(0, newline).replace(/\r$/, '')
      buffered = buffered.slice(newline + 1)
      if (line === '') {
        if (data.length > 0) await apply(data.join('\n'))
        data = []
      } else if (line.startsWith('data:')) {
        data.push(line.slice(5).replace(/^ /, ''))
      }
      // `: subscribed` and any other comment or field: not ours.
    }
  }
}

async function apply(json) {
  let frame
  try {
    frame = JSON.parse(json)
  } catch {
    log(`frame was not JSON — ignored: ${json.slice(0, 200)}`)
    return
  }
  const count = Array.isArray(frame.sessions) ? frame.sessions.length : 0
  await publish(render(frame), `${count} session${count === 1 ? '' : 's'}`)
}

const controller = new AbortController()
for (const signal of ['SIGTERM', 'SIGINT']) {
  process.on(signal, () => {
    log(`${signal} — stopping; the map stays as written`)
    controller.abort()
    process.exit(0)
  })
}

log(`${options.out} follows ${stream} as ${options.as}`)
for (;;) {
  try {
    await follow(controller.signal)
    log('stream ended — reconnecting')
  } catch (error) {
    if (controller.signal.aborted) break
    // Nothing answered. Whatever the map said is now about sessions that cannot exist.
    log(`${stream} unreachable (${error.cause?.code ?? error.message}) — no Manager, so no sessions`)
    await publish(render(null), 'no Manager')
  }
  await sleep(reconnectDelayMs)
}
