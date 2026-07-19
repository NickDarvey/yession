# Plan — general presence cursors (title + bodies + beyond)

> **Status: delivered.** (`b13d2bf` #14 Design B, `c0e2e25` #15 debounce; builds on the title-only cursors from #8)

Replace the title-only cursor with ONE presence system that shows every collaborator's
caret AND selection wherever they are — the session title, any draft composer, any queued
message body — extensible to more places later. One peer has one focus at a time.

Decided (this repo already made the calls): **Design B** — generalize the existing typed
`Presence` frame (peer-to-peer relay by the Session Process, PeerId identity, NOT Yjs
awareness). A cursor is a **Yjs relative position** over the field's shared type, so it
survives concurrent edits. **Selection ranges from the start. rAF-throttled reporting.**

## Data model (replaces `RemoteCursor`/`TitleCursor`)

```
type FocusField =            // the extension point for "many places"
    | Title
    | DraftBody of PeerId
    | QueueBody of QueueId
type CursorPos = { Anchor : string; Head : string }   // base64 Yjs RelativePositions
type Focus     = { Field : FocusField; Pos : CursorPos }
```

- `PresencePayload` → `{ PeerId; DisplayName; Focus : Focus option }` (None = caret left
  everything / peer gone).
- Model: `Presence : Map<PeerId, RemotePresence>`, `RemotePresence = { DisplayName; Focus }`.
  Colour is derived, not carried: one shared `PeerColour.of : PeerId -> string` (the existing
  HSL hash) so a peer is the same colour in title and bodies.
- Wire is uniform (base64 relative position); only *report* and *decode-to-render* branch by
  field type: **Title** = Yjs-native over the title `Y.Text`; **body** = y-prosemirror-mapped
  over the `Y.XmlFragment` (`getRelativeSelection` / `relativePositionToAbsolutePosition`).

## Report (rAF-throttled, one focus per peer)

- **Title input** (`View.header`): on select/keyup/click/focus → `selectionStart/End` →
  relative positions over `doc.getText "title"` → report `Some {Title; …}`; on blur → `None`.
- **Body editor:** a pure-F# `presenceReportPlugin` observes selection-changing transactions →
  `getRelativeSelection(binding, state)` → report `{DraftBody|QueueBody; …}`. The editor
  reports only a `CursorPos option`; the Browser tags it with the field from the body-mount key.
- Only editable editors report. rAF coalesces blur→focus field switches into the latest state.

## Relay
Unchanged: the `Presence` frame + `broadcastPresenceExcept`; disconnect already clears a peer.
Only the payload shape widens.

## Render
- **Title:** the view renders one empty marker per Title-focused peer (peer id + colour);
  Browser post-render decodes each peer's relative anchor/head against the title `Y.Text` →
  indices → measures caret `left` + selection width (existing measurement Emit, generalised).
- **Bodies:** a pure-F# `presenceDecorationsPlugin` per editor. Browser pushes the remote
  presences targeting THIS body into the editor (`EditorHandle.PushPresences`), which decodes
  each to absolute PM positions via its own ySync binding and builds a `DecorationSet` — a
  caret widget at `head`, a name label, and an inline selection decoration `min..max`, coloured
  per peer. Rebuilt on presence change; ProseMirror remaps through local edits. Positions that
  no longer resolve (content deleted) are skipped.

## File-by-file
- `src/Yession.Domain/Transport.fs`, `Serialization.fs` — widen `PresencePayload`; codec + round-trip.
- `src/Yession.App/Model.fs` — `RemotePresence`/`Focus`/`FocusField`; fold add/update/clear; drop old types.
- `src/Yession.App/PeerColour.fs` (new) — shared HSL-from-PeerId.
- `src/Yession.App/ProseMirror.fs` — bindings: `Decoration.widget/inline`, `DecorationSet`,
  `PluginKey`, plugin `state`/`apply`, `tr.setMeta`/`view.dispatch`, `ySyncPluginKey`,
  `getRelativeSelection`, `relativePositionToAbsolutePosition`; + Yjs relpos primitives
  (`encode/decodeRelativePosition`, `create*` for the title) and lib0 base64.
- `src/Yession.App/Editor.fs` — add the two plugins; `mountEditor` takes `reportFocus` and
  returns an `EditorHandle { Dispose; PushPresences }`.
- `src/Yession.App/View.fs` — title reports via presence; render a title marker per Title peer.
- `src/Yession.App/App.fs` — `Connection.ReportPresence : Focus option -> unit` (replaces `ReportCursor`).
- `app/browser/Browser.fs` — wire reporting (rAF throttle), push per-body presences into editors,
  generalise the title-cursor positioning.
- `app/Host.fs`, `src/Yession.SessionProcess/Transport.fs` — relay unchanged (widened payload flows).

## Verification
- **Cheap tier:** presence-fold reducer (add/update/clear); `PresencePayload` codec round-trip;
  a relative-position encode→decode round-trip (Editor cheap test); extend the InMemory presence
  relay test to a BODY field, not just title.
- **Browser (host-free):** the editor harness mounts two editors on two synced docs; inject a
  remote presence → assert a caret + selection decoration render at the mapped position.
- **Verify tier:** two real browser peers — one moves its caret/selection in the title AND in a
  draft body; the other sees both, correctly coloured, surviving concurrent edits.

## Alternatives (recorded)
- **Design A (Yjs awareness + `yCursorPlugin`)** — less body-cursor code, but reverses the
  no-awareness decision and adds a binary awareness relay + clientID↔PeerId mapping. Rejected.
- **Raw indices instead of relative positions** — stale under concurrent edits. Rejected.
