module Yession.Analyzers.EmitMacro

open System.Text.RegularExpressions
open FSharp.Analyzers.SDK
open FSharp.Compiler.Symbols

/// What an `[<Emit>]` macro's `$0`, `$1`, ... are allowed to name.
///
/// A binding is a string on one line and a signature on the next, and nothing in either
/// language reads both:
///
///     [<Emit("$0.style.setProperty($1, $2)")>]
///     let setStyleProperty (el: HTMLElement) (name: string) (value: string) : unit = jsNative
///
/// Fable substitutes each `$N` with the Nth argument and emits whatever falls out. Name a
/// slot that does not exist and the JS reads `undefined`; leave a slot unnamed and that
/// argument's expression is never emitted at all, so whatever it was going to do does not
/// happen. Both are the same slip — a parameter added, removed, or reordered on one line and
/// not the other — and neither line is wrong on its own.
///
/// Which is why nothing catches them. F# type-checks the signature and treats the string as
/// an opaque literal; the JS that comes out is syntactically fine, and `undefined` is a
/// perfectly good value there until something reads a property off it. A test catches it
/// only by running that exact binding on the platform it targets, and an interop binding
/// typically has one call site: `$3` where there are three arguments is a crash in
/// production and a green suite everywhere else.
///
/// So this reads the string against the symbol the attribute is attached to, which is the
/// one place both halves are known at once.
///
/// It reads the ATTRIBUTE form only. `emitJsExpr (a, b) "$0($1)"` carries the same hazard,
/// but there the macro and its arguments are one expression a reader takes in at once — the
/// slip this rule is about is the one where they are two lines free to drift apart.

/// Fable's placeholder syntax: `$N`, plus `$N...` to spread the rest from N onwards. Both
/// name a slot, which is all this asks about — the spread's trailing dots, and the `{{ }}`
/// conditional blocks Fable also understands, change what is EMITTED, never which arguments
/// exist to be emitted.
///
/// A `$` not followed by a digit is not a placeholder, and `app/Ssr.fs` is full of them:
/// `/="?$/` is a regex end anchor, and `_$litType$` is the property name Lit marks its own
/// values with.
let private placeholders = Regex @"\$(\d+)"

let private isUnit (t: FSharpType) =
    let t = t.StripAbbreviations ()
    t.HasTypeDefinition && t.TypeDefinition.TryFullName = Some "Microsoft.FSharp.Core.Unit"

/// The slots Fable will substitute, in the order it numbers them: `Some name` for a
/// declared parameter, `None` for the receiver.
///
/// An instance member's `$0` is the receiver, so its declared parameters start at 1. A
/// constructor's do not — there is no receiver to pass. And a `unit` parameter is not an
/// argument at all: `let isSupported () : bool` compiles to a call with none, so its macro
/// has nothing to name and naming nothing is correct.
let private argumentSlots (mfv: FSharpMemberOrFunctionOrValue) =
    [ if mfv.IsInstanceMember && not mfv.IsConstructor then
          yield None
      for p in Seq.concat mfv.CurriedParameterGroups do
          if not (isUnit p.Type) then
              yield Some (defaultArg p.Name "") ]

/// A parameter the binding keeps for a reason other than being emitted — the shape a call
/// site has to write, a type that has to be inferred — says so the way F# says it anywhere
/// else, with a leading underscore. `app/browser/EditorHarness.fs` has them: bindings taking
/// a `_terminal` their macro has no use for, because the hook each installs is per-window and
/// the argument is there only to make the call sites read alike.
///
/// That is the whole suppression story, and it is deliberate that it is the language's own
/// convention rather than a comment this rule would have to define and police.
let private deliberatelyUnused (name: string) = name.StartsWith "_"

/// The macro this binding carries, and where it is written.
///
/// Only the raw `[<Emit>]` form. The named variants — EmitMethod, EmitConstructor,
/// EmitIndexer, EmitProperty — take a NAME and let Fable write the call around it, so there
/// is no `$N` in one to be wrong.
let private emitMacroOn (mfv: FSharpMemberOrFunctionOrValue) =
    mfv.Attributes
    |> Seq.tryPick (fun a ->
        if a.AttributeType.TryFullName = Some "Fable.Core.EmitAttribute" then
            match Seq.tryHead a.ConstructorArguments with
            | Some (_, (:? string as m)) -> Some (a.Range, m)
            | _ -> None
        else
            None)

/// Every fault in one binding, as a sentence each. A member with two of them is two
/// diagnostics on one range rather than one diagnostic naming both: they are independent
/// slips and each is fixed on its own.
let private faults (mfv: FSharpMemberOrFunctionOrValue) (m: string) =
    let named = set [ for x in placeholders.Matches m -> int x.Groups.[1].Value ]
    let slots = argumentSlots mfv
    let count = List.length slots
    let args = if count = 1 then "1 argument" else $"%d{count} arguments"

    [ for i in Set.toList named do
          if i >= count then
              yield
                  $"this emit macro reads $%d{i}, but the binding it is on takes %s{args}. "
                  + "Fable substitutes nothing there, so the emitted JavaScript reads undefined."
      for i, slot in List.indexed slots do
          match slot with
          | Some name when not (named.Contains i) && not (deliberatelyUnused name) ->
              yield
                  $"this emit macro never reads $%d{i}, so Fable emits nothing for `%s{name}` and it is "
                  + "not evaluated. Name it in the macro, or prefix it with _ to say it is not emitted."
          | _ -> () ]

let rec private members (ds: FSharpImplementationFileDeclaration list) =
    seq {
        for d in ds do
            match d with
            | FSharpImplementationFileDeclaration.Entity (e, nested) ->
                // Abstract members carry the attribute but have no implementation, so they
                // reach this only through the entity that declares them.
                yield! e.MembersFunctionsAndValues
                yield! members nested
            | FSharpImplementationFileDeclaration.MemberOrFunctionOrValue (mfv, _, _) -> yield mfv
            | FSharpImplementationFileDeclaration.InitAction _ -> ()
    }

[<Literal>]
let Code = "YES002"

[<CliAnalyzer("EmitMacro", "An [<Emit>] macro names exactly the arguments its binding takes", "")>]
let emitMacro: Analyzer<CliContext> =
    fun ctx ->
        async {
            let offenders =
                match ctx.TypedTree with
                | Some tree ->
                    [ for mfv in members tree.Declarations do
                          match emitMacroOn mfv with
                          | Some (range, m) ->
                              for fault in faults mfv m -> range, fault
                          | None -> () ]
                    // A module-level `let` arrives twice — once as a declaration of its own,
                    // once as a member of the module that holds it. The attribute's range is
                    // the binding's identity here; asking the symbol for its own location is
                    // not, because FCS refuses that outright for some of them.
                    |> List.distinct
                | None -> []

            return
                [ for (range, message) in offenders ->
                    { Type = "EmitMacro"
                      Message = message
                      Code = Code
                      Severity = Severity.Error
                      Range = range
                      Fixes = [] } ]
        }
