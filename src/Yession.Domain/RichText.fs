namespace Yession.Domain

// Rich-text bodies (docs/plans/03-rich-text-editing.md). A draft/queue body is a
// structured ProseMirror document synced as a Yjs `XmlFragment`. It is declared in the
// Ylmish schema via `Encode.custom` so the fragment is anchored race-free at
// `drafts.<id>.body` / `queue.<id>.body` — the sync boundary stays honest — but its VALUE
// is never decoded: a custom nested in a keyed `Encode.map` cannot round-trip through
// Ylmish's structural map read (it never yields `ElCustom`). The app instead resolves the
// live fragment from the `BodyRegistry` by id and hands it to the `y-prosemirror` editor.

open System.Collections.Generic
open Yjs
open Ylmish.Codec

/// A rich-text body backed by a live `Y.XmlFragment`. As a `CustomElement` it exists only
/// to (a) anchor the fragment when Ylmish attaches the schema (Connect grabs the live,
/// integrated fragment via `ctx.GetXmlFragment ()` — the capability added in Ylmish
/// beta0219) and (b) expose that fragment to the editor. `Value` is never read because the
/// body is not decoded.
type RichBody () =
    let mutable fragment : Y.XmlFragment = Unchecked.defaultof<Y.XmlFragment>
    let mutable connected = false
    /// The live, integrated `Y.XmlFragment` — valid only once `Connected`. Handed to
    /// `y-prosemirror` (`ySyncPlugin`) so editor edits flow straight into the CRDT.
    member _.Fragment = fragment
    /// True once Ylmish has attached the schema and `Connect` has run. Until then the
    /// fragment is not yet materialized and the editor mount must wait one render.
    member _.Connected = connected
    interface CustomElement with
        member _.Connect ctx =
            fragment <- ctx.GetXmlFragment ()
            connected <- true
            { new System.IDisposable with member _.Dispose () = () }
        member _.Value = box ()

/// One stable `RichBody` per body id, shared by the codec (which anchors the fragment via
/// `Encode.custom`) and the view (which binds the editor to the same live fragment). The
/// instance must be stable across re-encodes so Ylmish's attach never re-integrates the
/// fragment (U5); the registry guarantees that by key.
type BodyRegistry () =
    let bodies = Dictionary<string, RichBody> ()
    /// The `RichBody` for this id, created on first use and reused thereafter.
    member _.GetOrCreate (id: string) : RichBody =
        match bodies.TryGetValue id with
        | true, body -> body
        | _ ->
            let body = RichBody ()
            bodies.[id] <- body
            body
    /// The live fragment for this id, if a `RichBody` exists and Ylmish has connected it.
    member _.TryFragment (id: string) : Y.XmlFragment option =
        match bodies.TryGetValue id with
        | true, body when body.Connected -> Some body.Fragment
        | _ -> None
    /// Forget a body id (a draft consumed/deleted). The fragment itself is owned by the
    /// doc; this only drops the registry's handle so the map does not grow unbounded.
    member _.Forget (id: string) : unit = bodies.Remove id |> ignore
