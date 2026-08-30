module EnvReaderFixture.Elsewhere

/// The second reader of each, in the other form: through the wrapper where `Boot` read
/// directly, and directly where `Boot` went through the wrapper.

let mode = Access.setting "FIXTURE_MODE" // YES008

let shared = Access.read Access.Shared "" // YES008
