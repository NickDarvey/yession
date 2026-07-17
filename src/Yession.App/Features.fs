namespace Yession.App

/// Client feature flags. Set once at startup (the browser reads the query string) and read by
/// the pure view. Default OFF, so the server bootstrap, the tests, and the shipped experience
/// all see today's behaviour — each flag is opt-in and its new component sits SIDE BY SIDE with
/// the one it can replace, so we can flip between them without removing anything.
module Features =

    /// Linear-style rich-text composer (docs/plans/03-rich-text-editing.md): when on (`?rich=1`),
    /// the local draft composer is a ProseMirror editor instead of a textarea. The synced body
    /// stays `Ylmish.Text` markdown either way — the editor round-trips through markdown — so
    /// sync, drain, and every existing test are untouched. Off by default.
    let mutable richText = false
