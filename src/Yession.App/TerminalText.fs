namespace Yession.App

open Fable.Core
open Yjs
open Yession.Domain
open Yession.Domain.Collab

/// Reading and editing a plain collaborative command line (Plan 13).
///
/// A terminal composer is an `<input>` bound to a `Y.Text` root, and the whole difficulty
/// is in one function: turning "the input's value is now this string" back into CRDT
/// operations. Replacing the text wholesale would work on screen and be wrong in every
/// other way — it would delete and re-insert every character on each keystroke, so a
/// collaborator's concurrent edit would be clobbered rather than merged, and their caret
/// would jump to the end.
///
/// `setTo` therefore writes the MINIMUM edit: keep the common prefix, keep the common
/// suffix, and replace only what actually changed. For ordinary typing that is a
/// one-character insert, which is exactly what merges cleanly.
module TerminalText =

    [<Emit("$0.toString()")>]
    let private textString (text: Y.Text) : string = jsNative

    [<Emit("$0.length")>]
    let private textLength (text: Y.Text) : int = jsNative

    [<Emit("$0.insert($1, $2)")>]
    let private textInsert (text: Y.Text) (index: int) (value: string) : unit = jsNative

    [<Emit("$0.delete($1, $2)")>]
    let private textDelete (text: Y.Text) (index: int) (length: int) : unit = jsNative

    [<Emit("$0.transact($1, $2)")>]
    let private transact (doc: Y.Doc) (body: unit -> unit) (origin: obj) : unit = jsNative

    /// The current value of a command line.
    let read (registry: TextRegistry) (key: string) : string = textString (registry.Text key)

    /// The length of the common prefix of two strings.
    let private commonPrefix (a: string) (b: string) : int =
        let limit = min a.Length b.Length
        let mutable i = 0
        while i < limit && a.[i] = b.[i] do
            i <- i + 1
        i

    /// The length of the common suffix, not overlapping a prefix of `skip` characters.
    let private commonSuffix (a: string) (b: string) (skip: int) : int =
        let limit = min (a.Length - skip) (b.Length - skip)
        let mutable i = 0
        while i < limit && a.[a.Length - 1 - i] = b.[b.Length - 1 - i] do
            i <- i + 1
        i

    /// Bring a command line to `value` with the smallest edit that gets there. A no-op when
    /// it already says that — which matters, because the input's `@input` handler fires for
    /// changes this client did not make (a re-render after a remote edit), and re-writing
    /// the same text would be an update that echoes forever.
    let setTo (registry: TextRegistry) (key: string) (value: string) : unit =
        let text = registry.Text key
        let current = textString text
        if current <> value then
            let prefix = commonPrefix current value
            let suffix = commonSuffix current value prefix
            let removed = current.Length - prefix - suffix
            let inserted = value.Substring (prefix, value.Length - prefix - suffix)
            if removed > 0 then textDelete text prefix removed
            if inserted <> "" then textInsert text prefix inserted

    /// Empty a command line.
    let clear (registry: TextRegistry) (key: string) : unit =
        let text = registry.Text key
        let length = textLength text
        if length > 0 then textDelete text 0 length

    /// Copy a command line into another root and clear the source, in ONE transaction.
    ///
    /// One transaction because this is a send: the Session Process drains on the queue
    /// entry's arrival, so an entry that arrived without its command would be snapshotted
    /// as an empty one — the same atomicity the message send needs, for the same reason.
    /// Copy-then-clear rather than a move because shared types cannot be re-parented.
    let moveInto (doc: Y.Doc) (registry: TextRegistry) (fromKey: string) (toKey: string) (alsoInTransaction: unit -> unit) : unit =
        let value = read registry fromKey
        transact
            doc
            (fun () ->
                if value <> "" then setTo registry toKey value
                alsoInTransaction ()
                clear registry fromKey)
            null
