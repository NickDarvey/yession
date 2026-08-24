namespace Yession.Domain.Collab

open Yession.Domain

// Rich-text bodies. A draft/queue body is a structured ProseMirror document held as a Yjs
// `XmlFragment`. It is a top-level named root on the doc, keyed by `BodyKey`, NOT nested
// inside the `drafts`/`queue` maps and NOT declared in the Ylmish schema. Two reasons the
// body stays out of the Ylmish-encoded state:
//   1. a custom nested in a keyed `Encode.map` cannot round-trip Ylmish's structural decode
//      (it never yields `ElCustom`), so the value would never decode anyway; and
//   2. Ylmish's structural reader walks a `Y.XmlFragment` as a plain object and recurses into
//      its cyclic internals — so an XmlFragment reachable anywhere in the decoded tree crashes
//      the decode. Keeping bodies as sibling roots (never in the decoded tree) avoids both.
// The body is therefore a CRDT the app co-manages on the doc — synced by the same update
// transport as everything else, and read by the Session Process straight from the doc (it has
// no Ylmish binding). `getXmlFragment key` is idempotent and merges by name, so every replica
// binds the same fragment with no creation race.
//
// A body is structured because the syntax is meant to disappear as you type it. Two cheaper
// shapes were rejected for the same reason: keeping markdown SOURCE in a `Y.Text` and
// decorating it live is boundary-pure but leaves the syntax on screen, and running ProseMirror
// over a `Y.Text` by re-serializing the document to markdown on every keystroke replaces a
// structural merge with a text merge — an impedance mismatch that degrades exactly the
// concurrent editing the CRDT is here for. Markdown remains the interchange format, produced
// only at the edges (`Markdown.ofFragment` at the drain, on paste, and for the read-only
// timeline).

open Yjs

/// Binds body ids to their live top-level `Y.XmlFragment` on a given doc. Thin by design: the
/// doc's root registry already guarantees one stable, race-free fragment per key, so this just
/// names the anchor. The editor binds the returned fragment to `y-prosemirror`; the codec never
/// sees it.
type BodyRegistry (doc: Y.Doc) =
    /// The live fragment for this body id — created on first access, stable thereafter.
    member _.Fragment (id: string) : Y.XmlFragment = doc.getXmlFragment id

/// The same arrangement for PLAIN collaborative text: a top-level `Y.Text` root per key,
/// co-managed by the app and never part of the Ylmish-encoded tree. Terminal command
/// drafts and queued commands live here (`BodyKey.terminalDraft`/`terminalQueued`).
///
/// Plain text rather than a rich body, because a command line is characters: a shell glob,
/// a quoted argument, and a backslash all have to survive verbatim, and round-tripping
/// them through Markdown would not let them. Still a CRDT, so two people editing one
/// command line merge per character exactly as they do in a message.
type TextRegistry (doc: Y.Doc) =
    /// The live text for this key — created on first access, stable thereafter.
    member _.Text (key: string) : Y.Text = doc.getText key
