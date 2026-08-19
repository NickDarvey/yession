module Yession.Host.Sse

// Server-sent events, both ends, once. Five routes across the Manager push over SSE — the three
// control reverse legs (`/control/notifications`, `/control/mcp`, `/control/connections`), the
// session registry stream, and the management page's rows — and each had its own copy of the same
// six lines: headers, an opening comment, a sink, a subscription, a 15s heartbeat, teardown on
// request close. So did the consuming side, twice. This module owns the protocol; a route supplies
// only what is actually its own — how to encode a payload, and what to subscribe to.
//
// The subscribe/sink/unsubscribe shapes are the repo's push vocabulary (`Yession.Domain.Push`), so
// a hub's `Register` plugs straight in with no adapter.

open Fable.Core
open Fable.Core.JsInterop
open Yession.Domain
open Yession.Host.Interop

/// Renders a payload as one SSE event's data. May be multi-line — `frame` handles that.
type Encode<'a> = 'a -> string

// The keep-alive interval, so an idle subscription is not reaped by an HTTP idle timeout.
[<Emit("setInterval($1, $0)")>]
let private setInterval (ms: int) (callback: unit -> unit) : obj = jsNative

[<Emit("clearInterval($0)")>]
let private clearInterval (handle: obj) : unit = jsNative

/// One SSE event: every line of the payload becomes its own `data:` line, which a client rejoins
/// with newlines (the browser's `EventSource` does exactly that, and so does `subscribe` below).
/// Single-line payloads — every JSON frame on the control legs — are just the one-line case, so
/// rendered markup and wire JSON need no different treatment.
let frame (payload: string) : string =
    let lines =
        payload.Replace("\r\n", "\n").Split '\n'
        |> Array.map (fun line -> "data: " + line)
    String.concat "\n" lines + "\n\n"

/// The `data:` payload of one SSE event, or `None` for a comment-only event (`: subscribed`,
/// `: ping`) — the inverse of `frame`, and the parsing half of `subscribe`.
let dataOf (event: string) : string option =
    let data =
        event.Replace("\r\n", "\n").Split '\n'
        |> Array.filter (fun line -> line.StartsWith "data:")
        |> Array.map (fun line ->
            let value = line.Substring 5
            if value.StartsWith " " then value.Substring 1 else value)
    if Array.isEmpty data then None else Some (String.concat "\n" data)

/// Serve an SSE stream: write the headers (and an opening comment, so the client sees them at
/// once), register a sink that frames every payload onto the response, keep the connection alive,
/// and tear both the heartbeat and the subscription down when the request closes.
///
/// Returns the sink, for a route that must push a frame of its own beyond what it subscribed to —
/// `/control/connections` answers with an awaited snapshot that way.
let stream (req: IncomingMessage) (res: ServerResponse) (encode: Encode<'a>) (subscribe: Subscribe<'a>) : Sink<'a> =
    res.writeHead (
        200,
        createObj
            [ "content-type", box "text/event-stream"
              "cache-control", box "no-store"
              "connection", box "keep-alive" ])
    |> ignore
    res.write ": subscribed\n\n" |> ignore
    let sink : Sink<'a> = fun payload -> res.write (frame (encode payload)) |> ignore
    let subscription = subscribe sink
    let heartbeat = setInterval 15000 (fun () -> res.write ": ping\n\n" |> ignore)
    req.on ("close", fun _ ->
        clearInterval heartbeat
        subscription.Stop ())
    |> ignore
    sink

// Consume an SSE stream: connect (with whatever request headers the caller's authentication
// wants — a control secret, a strategy's identity assertion, or none), hand each event's rejoined
// `data:` lines to the sink, reconnect with a fixed backoff when the connection drops, and cancel
// both the retry loop and the live fetch on unsubscribe. Best-effort by design: a transport error
// is a dropped connection, retried, never thrown.
//
// EVERY statement below is semicolon-terminated, and that is load-bearing: Fable inlines a lambda
// argument as a parenthesised IIFE, so `$2(event)` can expand to `((e) => {...})(event)`. Without
// the semicolon on the line above it, JS's automatic semicolon insertion glues the two together
// and calls the previous expression's result — which fails at runtime, inside a catch, as a stream
// that silently delivers nothing.
[<Emit("""(function (url, headers, onEvent, retry) {
  const controller = new AbortController();
  let cancelled = false;
  const run = async () => {
    while (!cancelled) {
      try {
        const res = await fetch(url, { method: 'GET', headers: { ...Object.fromEntries(headers), 'accept': 'text/event-stream' }, signal: controller.signal });
        if (!res.ok) { if (!retry(res.status)) return; throw new Error('sse subscribe failed: ' + res.status); }
        const reader = res.body.getReader();
        const decoder = new TextDecoder();
        let buffer = '';
        for (;;) {
          const { done, value } = await reader.read();
          if (done) break;
          buffer += decoder.decode(value, { stream: true });
          let idx;
          while ((idx = buffer.indexOf('\n\n')) >= 0) {
            const event = buffer.slice(0, idx);
            buffer = buffer.slice(idx + 2);
            onEvent(event);
          }
        }
      } catch (e) { if (cancelled) return; }
      if (cancelled) return;
      await new Promise(r => setTimeout(r, 1000));
    }
  };
  run();
  return () => { cancelled = true; controller.abort(); };
})($0, $1, $2, $3)""")>]
let private openStream
    (url: string)
    (headers: (string * string)[])
    (onEvent: Sink<string>)
    (retry: int -> bool)
    : (unit -> unit) =
    jsNative

/// Whether a connect that was REFUSED should be tried again, asked once per failure with the
/// status the server answered.
///
/// The default is yes, forever, and that is right for every leg inside this product: the
/// Manager is coming back, and a stream is the only way it can reach us. It is wrong the
/// moment we talk to a server somebody else wrote — a refusal can mean "this endpoint does
/// not exist here", which is permanent, and retrying it every second is a hot loop against a
/// server behaving correctly. So the caller that knows what a status MEANS says so; this
/// module does not guess.
type Retry = int -> bool

module Retry =

    /// Keep trying whatever the server says. What the control legs and the registry stream
    /// want: the peer is ours, and its absence is always temporary.
    let always : Retry = fun _ -> true

/// Subscribe, but stop for good when `retry` says a refusal is permanent. The stopped
/// subscription is inert rather than errored: a server that does not offer a stream is not a
/// fault, it is a server whose news has to arrive another way.
let subscribeWhile (url: string) (headers: (string * string) list) (retry: Retry) (onFrame: Sink<string>) : Subscription =
    Subscription.ofStop (
        openStream url (Array.ofList headers) (fun event -> dataOf event |> Option.iter onFrame) retry)

/// Subscribe to an SSE stream, receiving one call per event carrying data (comment-only
/// keep-alives are dropped). `headers` ride every connect and reconnect.
let subscribe (url: string) (headers: (string * string) list) (onFrame: Sink<string>) : Subscription =
    subscribeWhile url headers Retry.always onFrame
