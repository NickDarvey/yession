namespace UnjudgedFixture

/// A file that does not compile, which is the whole fixture. Every other rule here is checked
/// against source the compiler accepted; this one can only be checked against source it did
/// not, so this file is deliberately broken and deliberately not in `Yession.slnx`.
///
/// The break is the real one: a `let` written at column 0 under a `namespace`. One stray
/// indentation, and the binding — with any attribute sitting on it — is dropped from the typed
/// tree, so every other rule passes over it in silence. `lint` reads the `// YES000` marker
/// below and checks that the rule said so.
///
/// One error, deliberately, because the rule reports the first one and no more: what it is
/// saying is that this file was not fully read, not that the compiler has more to add.
module Values =

    let fine = 1

let stray = 2 // YES000
