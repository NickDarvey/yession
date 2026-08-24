module Yession.Host.Queries

// The query registry (Plan 15): the ONE place a read-only view of session state is
// declared, projected to two audiences that never see each other's projection.
//
//   agent  ◄── MCP tools, generated from `Definitions` + `Read`, carrying `readOnlyHint`
//   human  ◄── one multiplexed SSE stream, rendered by one generic settings component
//
// The asymmetry with COMMANDS is the point. A command mutates and only the agent runs
// one — a human who wants something changed asks, and the mutation lands as an attributed
// event in the timeline. A query reads, and everybody gets it, because verifying what the
// agent did should not cost a conversation turn.
//
// Nothing here knows what a repo or a sandbox is: a registration is a declaration plus a
// thunk, so the capability that owns the state owns the answer, and adding a query is one
// value in SessionMain rather than a panel, a route, a message and a reducer case.

open Yession.Domain
open Yession.Domain.Tools

/// One query, wired: what it declares, and how to answer it. `Read` is called on demand —
/// nothing is cached, because every value here is somebody else's truth (the filesystem's
/// answer, the process table's answer), and a cache is where a panel starts disagreeing
/// with reality.
type QueryRegistration =
    { Def : QueryDef
      Read : unit -> Async<Result<QueryValue, string>> }

/// The registry, as its consumers see it.
type QueryRegistry =
    { /// What this session declares, in registration order — the agent's tool list and
      /// the settings surface's section order, which are deliberately the same order.
      Definitions : QueryDef list
      /// Answer one query. `Error` for an unregistered name, or whatever the owner said.
      Read : QueryName -> Async<Result<QueryValue, string>>
      /// Say that a query's answer may have changed, so subscribers are told. Called by
      /// the COMMAND that changed it: a command is the only thing that can, which is what
      /// keeps this from needing to poll.
      Invalidate : QueryName -> unit
      /// Live frames for one subscriber: the declarations, then every query's current
      /// value, then a value whenever `Invalidate` names one.
      ///
      /// `RetainedHub`'s semantics, at per-query grain: a subscriber is handed the
      /// current state at once and every later frame carries a query's WHOLE value, never
      /// a delta — so a reconnect is the entire recovery protocol, and one
      /// connect-read-disconnect is a poll. Two differences, both deliberate: the opening
      /// state spans a frame per query rather than one frame, and nothing is retained —
      /// each frame is READ when it is sent, because the truth is the filesystem's and the
      /// process table's, so a cached snapshot could hand a reconnecting client an answer
      /// that is already wrong.
      Subscribe : Subscribe<QueryFrame> }

/// A registry over a fixed set of registrations. Duplicate names are refused rather than
/// silently shadowed — two queries answering to one name would make the stream ambiguous
/// and the agent's tool list contradictory.
let create (registrations: QueryRegistration list) : Result<QueryRegistry, string> =
    let duplicate =
        registrations
        |> List.countBy (fun r -> QueryName.value r.Def.Name)
        |> List.tryFind (fun (_, count) -> count > 1)
    match duplicate with
    | Some (name, _) -> Error (sprintf "query '%s' is registered twice" name)
    | None ->
        let definitions = registrations |> List.map (fun r -> r.Def)
        let mutable sinks : (int * Sink<QueryFrame>) list = []
        let mutable nextSink = 0

        /// Read, and hold the owner to the shape it declared. A capability that answers a
        /// `Rows` query with a scalar is a bug in the capability; saying so here beats
        /// shipping a value no renderer can draw and no model can read.
        let read (name: QueryName) : Async<Result<QueryValue, string>> =
            async {
                match registrations |> List.tryFind (fun r -> r.Def.Name = name) with
                | None -> return Error (sprintf "no query named '%s'" (QueryName.value name))
                | Some registration ->
                    match! registration.Read () with
                    | Error e -> return Error e
                    | Ok value when not (QueryValue.fits registration.Def.Shape value) ->
                        return Error (sprintf "query '%s' answered in a shape it does not declare" (QueryName.value name))
                    | Ok value -> return Ok value
            }

        /// Read one query and hand it to a sink. Errors are NOT pushed: a stream frame
        /// says what a query answers, and "it failed" is not an answer — a subscriber
        /// keeps the last good value, and the failure surfaces where it is actionable
        /// (the agent's tool call) rather than blanking a panel.
        let pushValue (sink: Sink<QueryFrame>) (name: QueryName) =
            Async.StartImmediate (
                async {
                    match! read name with
                    | Ok value -> sink (QueryValued (name, value))
                    | Error _ -> ()
                })

        let invalidate (name: QueryName) =
            let current = sinks
            pushValue (fun frame -> current |> List.iter (fun (_, sink) -> sink frame)) name

        let subscribe : Subscribe<QueryFrame> =
            fun sink ->
                let id = nextSink
                nextSink <- nextSink + 1
                sinks <- sinks @ [ id, sink ]
                // The declarations first, so a client can render the surface before any
                // value arrives; then each value, so there is nothing else to ask for. A
                // subscriber that connects mid-flight therefore cannot miss an update it
                // needed — this opening burst IS the snapshot, which is why there is no
                // separate snapshot route to race against it.
                sink (QueriesDeclared definitions)
                for registration in registrations do
                    pushValue sink registration.Def.Name
                Subscription.ofStop (fun () ->
                    sinks <- sinks |> List.filter (fun (existing, _) -> existing <> id))

        Ok
            { Definitions = definitions
              Read = read
              Invalidate = invalidate
              Subscribe = subscribe }

/// A registry with nothing in it — the composition's fallback when a session runs without
/// the capabilities that declare queries.
let empty : QueryRegistry =
    match create [] with
    | Ok registry -> registry
    | Error e -> failwithf "empty query registry: %s" e

// --- the browser-facing /queries route (Plan 15) ------------------------------------------
// ONE route, and it is the stream. There is no snapshot fetch beside it because there is
// nothing a snapshot fetch would answer that the stream's opening burst does not — and a
// second mechanism nobody exercises is a second mechanism that rots.

open Fable.Core.JsInterop
open Yession.App
open Yession.Host.Interop

let private respondText (res: ServerResponse) (status: int) (text: string) =
    res.writeHead (status, createObj [ "content-type", box "text/plain"; "cache-control", box "no-store" ]) |> ignore
    res.``end`` text

/// Build the `/queries` route handler over a registry. Cookie-gated like the connection
/// panels: reading session state is for the people in the session. Per-query
/// authorization is deliberately not here (stated in the plan) — every member reads every
/// query, exactly as every member already reads the timeline the commands land in.
let routes
    (auth: SessionAuth.Auth)
    (registry: QueryRegistry)
    (mount: string)
    : IncomingMessage -> ServerResponse -> bool =
    fun req res ->
        match SessionRoute.parseUnder mount req.``method`` (req.url.Split('?').[0]) with
        | Some SessionRoute.Queries ->
            match auth.IdentityOf req with
            | None -> respondText res 401 "unauthorized"
            | Some _ -> Sse.stream req res (Codec.toString Codec.queryFrame) registry.Subscribe |> ignore
            true
        | Some _
        | None -> false
