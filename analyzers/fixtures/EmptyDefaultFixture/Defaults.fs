module EmptyDefaultFixture.Defaults

/// The forms YES009 is about. A line ending `// YES009` must be reported; every other line
/// here must not be. One file is enough: the rule is per call site, so one site can both break
/// it and prove it still sees a break.

// A default of `""` on a raw option — the absence spelled as an empty value — in every spelling.
let direct (x: string option) = Option.defaultValue "" x // YES009
let arg (x: string option) = defaultArg x "" // YES009
let piped (x: string option) = x |> Option.defaultValue "" // YES009

// Null-normalize: the option is minted by `Option.ofObj`, but it is still a raw option and the
// `""` is still the absent case wearing a value, so it is still flagged.
let normalized (raw: string) = (defaultArg (Option.ofObj raw) "").Trim () // YES009

// Render-into-string is left alone: the default sits on a MAPPED option, so `""` is the empty
// render of nothing, not a stand-in for a value.
let rendered (x: string option) = x |> Option.map (fun s -> s + "!") |> Option.defaultValue ""
let renderedPrefix (x: int option) = Option.defaultValue "" (x |> Option.map string)

// A default that is not empty is a real choice, not a hole.
let fallbackValue (x: string option) = Option.defaultValue "fallback" x
let fallbackArg (x: string option) = defaultArg x "fallback"

// A default that is not a string is not this rule's business.
let zero (x: int option) = Option.defaultValue 0 x
let emptyList (x: int list option) = defaultArg x []
