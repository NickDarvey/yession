module EnvReaderFixture.Boot

/// Read in one place, which is the whole rule: nothing else names it, so nothing is said.
let port = Access.setting "FIXTURE_PORT"

/// Read here and in `Elsewhere` both — directly on this side, through the wrapper on that one.
let mode = Access.read "FIXTURE_MODE" "off" // YES008

/// Through a `[<Literal>]`, whose value the compiler has already put here. Sharing the constant
/// is not sharing the read: two readers still carry two defaults, which is the fault.
let shared = Access.setting Access.Shared // YES008

/// A family rather than a variable. There is no name to count, and a rule that guessed one
/// would be counting a shape instead of a read.
let signature (channel: string) = Access.setting (sprintf "FIXTURE_SIGNATURE_%s" channel)

/// Writing is not reading, so this is not a second reader of `FIXTURE_MODE`.
let plant () = Access.write "FIXTURE_MODE" "on"
