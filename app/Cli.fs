module Yession.Host.Cli

// What each bin accepts on its command line, declared as data and parsed by Node's own
// `parseArgs` (`node:util`) — a real parser, with no dependency to add, because these bins
// already run on Node.
//
// It replaces hand-rolled `process.argv` scanning, which got two things wrong that matter
// more than the parsing itself:
//
//   - An unknown option was SILENTLY IGNORED. `yession-manager --auht localhost` ran with
//     no strategy at all, which is deny-everything — a typo that looks like a hang. Under
//     `strict`, `parseArgs` refuses instead.
//   - A bad value threw at module init, inside Fable's async, and surfaced as
//     `UnhandledPromiseRejection ... "[object Object]"` — technically a refused boot, but
//     nothing an operator could act on. `parseOrExit` prints the reason and the usage.
//
// Argu would be the F# answer on .NET and cannot be the answer here: Fable compiles from
// F# SOURCE, so a package has to ship its source files to be usable at all, and Argu ships
// IL. Its DU-plus-attributes model leans on reflection besides.
//
// The typed-access property Argu is loved for survives, by a different route: an `Opt` is a
// VALUE, and reading a parse back takes the same value that declared the option. There is
// no name to mistype at the call site, and an option cannot be read unless it was declared.

open Fable.Core
open Fable.Core.JsInterop

/// One option a bin accepts. Private, so every option is built by `flag` or `value` and its
/// declared shape cannot disagree with how it is read back.
type Opt =
    private
        { Long : string
          Short : string option
          /// None = a boolean switch; Some placeholder = takes a value.
          Placeholder : string option
          Help : string }

/// Everything a bin accepts, in one value — the thing `--help` prints and the parser is
/// built from, so they cannot drift.
type Spec =
    private
        { Bin : string
          Options : Opt list }

/// A successful parse. Opaque: read it with `isSet`/`valueOf`.
type Parsed =
    private
        { Present : Set<string>
          Values : Map<string, string> }

// --- the Node parser ---------------------------------------------------------------------

[<Import("parseArgs", "node:util")>]
let private parseArgs (config: obj) : obj = jsNative

[<Emit("({})")>]
let private newObject () : obj = jsNative

[<Emit("$0[$1] = $2")>]
let private setField (target: obj) (name: string) (value: obj) : unit = jsNative

[<Emit("$0[$1]")>]
let private field (source: obj) (name: string) : obj = jsNative

[<Emit("$0 == null")>]
let private absent (value: obj) : bool = jsNative

/// The argv a bin was started with, its own executable and script dropped.
[<Emit("process.argv.slice(2)")>]
let private argv () : string array = jsNative

/// Say what happened and stop. Typed as returning anything because it returns nothing —
/// `process.exit` does not come back, and pretending otherwise is what forced the old
/// `failwith`-at-module-init that produced the unreadable rejection.
[<Emit("(console.error($0), process.exit(2))")>]
let abort (message: string) : 'a = jsNative

// --- declaring a command line ------------------------------------------------------------

/// A boolean switch: present or not.
let flag (long: string) (short: string option) (help: string) : Opt =
    { Long = long; Short = short; Placeholder = None; Help = help }

/// An option that takes a value. `placeholder` is what `--help` shows in the angle brackets.
let value (long: string) (placeholder: string) (help: string) : Opt =
    { Long = long; Short = None; Placeholder = Some placeholder; Help = help }

/// Every bin answers these two identically, so they belong to what a spec IS rather than to
/// what each bin remembers to declare.
let version : Opt = flag "version" (Some "v") "print the version and exit"
let help : Opt = flag "help" (Some "h") "show this message and exit"

let spec (bin: string) (options: Opt list) : Spec =
    { Bin = bin; Options = options @ [ version; help ] }

// --- reading a parse ---------------------------------------------------------------------

/// Was this option given? Takes the `Opt` that declared it, so there is no name to mistype.
let isSet (opt: Opt) (parsed: Parsed) : bool = Set.contains opt.Long parsed.Present

/// The value given for this option, or None when it was not given.
let valueOf (opt: Opt) (parsed: Parsed) : string option = Map.tryFind opt.Long parsed.Values

// --- usage --------------------------------------------------------------------------------

let usage (spec: Spec) : string =
    let line (opt: Opt) =
        let names =
            match opt.Short with
            | Some short -> sprintf "-%s, --%s" short opt.Long
            | None -> sprintf "    --%s" opt.Long
        let names =
            match opt.Placeholder with
            | Some placeholder -> sprintf "%s <%s>" names placeholder
            | None -> names
        sprintf "  %-26s %s" names opt.Help
    let options = spec.Options |> List.map line |> String.concat "\n"
    sprintf "usage: %s [options]\n\noptions:\n%s" spec.Bin options

// --- parsing --------------------------------------------------------------------------------

/// The `parseArgs` config this spec describes. Built from the SAME list `usage` prints.
let private configFor (spec: Spec) (args: string array) : obj =
    let options = newObject ()
    for opt in spec.Options do
        let entry = newObject ()
        setField entry "type" (box (if opt.Placeholder.IsSome then "string" else "boolean"))
        opt.Short |> Option.iter (fun short -> setField entry "short" (box short))
        setField options opt.Long entry
    createObj
        [ "args", box args
          "options", options
          // Refuse an unknown option and a missing value rather than carrying on without
          // them, and refuse bare words too: no bin here takes a positional argument, so
          // one is always a mistake.
          "strict", box true
          "allowPositionals", box false ]

/// How every command-line complaint reads: which bin, what was wrong, then the usage.
let private complaint (spec: Spec) (message: string) : string =
    sprintf "%s: %s\n\n%s" spec.Bin message (usage spec)

/// Reject a VALUE the parser accepted but the domain refused — `--auth banana`. The shape
/// was fine, so `parseArgs` had nothing to say; this is where the option's own vocabulary
/// gets to. Reported identically to a parse failure, so an operator hears one voice for one
/// class of mistake.
let rejectValue (spec: Spec) (message: string) : 'a = abort (complaint spec message)

/// Parse `args` against `spec`. Total — the failure is a message an operator can act on,
/// carrying the parser's own complaint and the usage under it.
let parse (spec: Spec) (args: string array) : Result<Parsed, string> =
    try
        let values = field (parseArgs (configFor spec args)) "values"
        // Walk the DECLARATION, not the result: every name here is one this spec knows, so
        // nothing can arrive that `isSet`/`valueOf` could not name.
        let present, given =
            spec.Options
            |> List.fold
                (fun (present, given) opt ->
                    let raw = field values opt.Long
                    if absent raw then present, given
                    else
                        Set.add opt.Long present,
                        (match opt.Placeholder with
                         | Some _ -> Map.add opt.Long (unbox<string> raw) given
                         | None -> given))
                (Set.empty, Map.empty)
        Ok { Present = present; Values = given }
    with error ->
        Error (complaint spec error.Message)

/// The whole boundary, in one call: parse, answer `--version` and `--help` (every bin
/// answers them the same way), or report a bad command line and stop. Returns only when the
/// process should carry on.
let parseOrExit (spec: Spec) (currentVersion: string) : Parsed =
    match parse spec (argv ()) with
    | Error message -> abort message
    | Ok parsed ->
        if isSet version parsed then
            printfn "%s" currentVersion
            Interop.exit 0
        if isSet help parsed then
            printfn "%s" (usage spec)
            Interop.exit 0
        parsed
