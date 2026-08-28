module TemplateHoleFixture.Holes

open Lit

/// One hole of every shape the rule has an opinion about. A line marked `// YES001`
/// MUST be reported; every other hole here MUST NOT be. `lint` reads those markers and
/// compares them to what the analyzer actually said, in both directions — an allow-list
/// that has started rejecting good holes is as broken as a rule that has gone blind.
type Title = Title of string

type Person = { Name: string; Age: int }

// Admitted: the six things Lit renders on purpose.

let aString (s: string) = html $"""<b>{s}</b>"""

let anInt (n: int) = html $"""<b>{n}</b>"""

let aBool (b: bool) = html $"""<input ?disabled={b} />"""

let aTemplate (t: TemplateResult) = html $"""<b>{t}</b>"""

let templates (ts: TemplateResult list) = html $"""<ul>{ts}</ul>"""

let aListener (f: string -> unit) = html $"""<button @click={f}>go</button>"""

// Rejected: anything whose rendering is an accident of how it happens to stringify.

let aUnion (t: Title) = html $"""<b>{t}</b>""" // YES001

let aRecord (p: Person) = html $"""<b>{p}</b>""" // YES001

let anOption (s: string option) = html $"""<b>{s}</b>""" // YES001

let aFloat (x: float) = html $"""<b>{x}</b>""" // YES001

// The rule reads holes, not templates: a good hole beside a bad one is still good.

let mixed (s: string) (p: Person) = html $"""<b>{s}</b><i>{p}</i>""" // YES001

// A hole already typed `obj` is not a hole the rule cannot see — the compiler still emits the
// box, so it arrives here like any other and is rejected like any other. Which is the answer to
// the obvious way around a rule that reads `box<'T>`.

let anObj (o: obj) = html $"""<b>{o}</b>""" // YES001
