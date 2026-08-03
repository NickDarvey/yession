module Yession.Tests.YlmishRace

// A PINNED UPSTREAM DEFECT. This file asserts what Ylmish 1.0.0-beta0219 currently DOES,
// not what it should do — so it goes red the day the defect is fixed, which is the signal
// to delete both this file and the mitigation it justifies
// (`App.ConnectOptions.ReadPosition` and its doc-update re-arm).
//
// Reduced to a two-field model with no Yession types in it, so it can be handed upstream
// as-is.
//
// The mechanism (`Program.fs`):
//
//   * the doc subscription (`subs`) decodes against `currentModel` — a mutable holding the
//     model as of the last message Elmish PROCESSED — and dispatches `Set m` carrying that
//     already-decoded model;
//   * `update` for `Set m` ignores the `model` Elmish hands it and returns `m`.
//
// So any message dispatched but not yet processed when a remote doc update arrives is
// undone: `Set` is queued behind it and replaces the model with a snapshot taken before
// it ran. Elmish's ring buffer makes the window ordinary rather than exotic — anything
// dispatched from inside the dispatch loop (a `setState` callback, a subscription, an
// async continuation) queues.
//
// The fix is upstream and small: `update` already receives the live `model`, so `Set` can
// re-run `decodeCurrent` against IT instead of returning the payload the subscription
// decoded earlier. That is source-compatible — the carried model simply becomes vestigial
// — and it removes the `currentModel` mutable along with the race.
//
// What it costs us meanwhile: state that a re-read can rebuild (the conversation, the
// terminal projection, the read offset) is repaired by the mitigation; state that cannot
// (which draft the composer has open, whether the terminals column is open, a Claude
// sign-in flow's progress) is silently reverted. See docs/GAPS.md.

open Fable.Core
open Fable.Pyxpecto
open Elmish
open FSharp.Data.Adaptive
open Yjs
open Ylmish
open Ylmish.Codec

/// Synced: the title. Not synced: the count — it stands for every non-synced field a real
/// model carries (a projection folded from an event log, a panel's open/closed state, a
/// sign-in flow's progress).
type private Model = { Title : Text; Count : int }
type private Msg = Bump

type private Adaptive = { Title : cval<Text> }

let private create (m: Model) : Adaptive = { Title = cval m.Title }
let private update' (a: Adaptive) (m: Model) : unit = a.Title.Value <- m.Title
let private encode (a: Adaptive) : Encoded = Encode.object [ "title", Encode.text a.Title ]

let private decode : Decoder<Model, Model> =
    Decode.object {
        let! model = Decode.ask
        let! title = Decode.object.optional "title" Decode.text
        return { model with Title = defaultArg title Text.empty }
    }

/// A remote peer's update: a second doc that named the title, as a Yjs update payload.
let private remoteTitleUpdate () : JS.Uint8Array =
    let other = Y.Doc.Create ()
    (other.getText "title").insert (0, "from a peer")
    Y.encodeStateAsUpdate other

let tests =
    testList "Ylmish Set/dispatch race (upstream)" [
        testCase "PINNED DEFECT: a message dispatched before a remote update arrives is undone by the Set" <| fun () ->
            let doc = Y.Doc.Create ()
            let program =
                Program.mkProgram
                    (fun () -> { Title = Text.empty; Count = 0 }, Cmd.none)
                    (fun msg model ->
                        match msg with
                        | Bump -> { model with Count = model.Count + 1 }, Cmd.none)
                    (fun _ _ -> ())
                |> Ylmish.Program.withYlmish
                    { Doc = doc
                      Create = create
                      Update = update'
                      Encode = encode
                      Decode = decode
                      OnError = Ylmish.Program.OnError.log }

            let mutable latest = { Title = Text.empty; Count = 0 }
            let mutable send : Ylmish.Program.Message<Model, Msg> -> unit = ignore
            let mutable armed = true
            let setState (m: Model) (dispatch: Ylmish.Program.Message<Model, Msg> -> unit) =
                latest <- m
                send <- dispatch
                // Armed on the render caused by the FIRST bump, so the doc subscription is
                // already live (Elmish attaches subscriptions after init).
                if armed && m.Count = 1 then
                    armed <- false
                    // Inside the dispatch loop: this ENQUEUES (Elmish's ring buffer) —
                    // exactly what an async continuation folding an event page does.
                    dispatch (Ylmish.Program.Message.User Bump)
                    // ...and now a remote update lands. Yjs notifies synchronously, so the
                    // subscription decodes against `currentModel` — still the model as of
                    // the last PROCESSED message, because the second bump is only queued —
                    // and enqueues `Set` behind it.
                    Y.applyUpdate (doc, remoteTitleUpdate (), box "remote")

            Program.withSetState setState program |> Program.run
            send (Ylmish.Program.Message.User Bump)

            // Every message has been processed by now: bump, bump, then the stale Set.
            Expect.equal (Text.toString latest.Title) "from a peer" "the synced field arrived"
            // CORRECT would be 2. This asserts 1 — the defect — on purpose: when Ylmish
            // decodes at processing time this line fails, and that failure is the whole
            // point of the file. Do not "fix" it by changing the expectation.
            Expect.equal
                latest.Count
                1
                "the second bump is LOST: the `Set` carried a model decoded before Elmish \
                 processed it. Correct behaviour is 2 — if this now reads 2, Ylmish has been \
                 fixed: delete this file and the ReadPosition mitigation in App.fs"
    ]
