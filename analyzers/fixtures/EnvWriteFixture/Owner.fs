module EnvWriteFixture.Owner

/// The place this fixture's writes would live if there were one place. There is not —
/// `Elsewhere` writes too — so both are reported, and that is the rule: it says the writes are
/// spread, never which file was right.

let take (name: string) (value: string) =
    Access.set name value // YES007
    Access.clear name // YES007

/// Reads, of the same variable through the same module. Neither is a write.
let peek (name: string) =
    Access.read name "", Access.holds name "on"
