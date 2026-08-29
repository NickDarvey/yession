module Yession.Analyzers.Emits

open System.Text.RegularExpressions
open FSharp.Compiler.Symbols

/// Finding an `[<Emit>]` macro, for the rules that have something to say about one.
///
/// Two of them do, and they ask different questions of the same string: whether its `$N`
/// correspond to the arguments the binding takes (`EmitMacro`), and whether the JavaScript
/// around them survives being pasted into a caller's scope (`EmitBody`). What they share is
/// how the string is found at all, and that belongs in one place — a rule that reads emits
/// slightly differently from its sibling is a rule with its own blind spot.

/// Fable's placeholder syntax: `$N`, plus `$N...` to spread the rest from N onwards. Both
/// name a slot, which is all either rule asks about — the spread's trailing dots, and the
/// `{{ }}` conditional blocks Fable also understands, change what is EMITTED, never which
/// arguments exist to be emitted.
///
/// A `$` not followed by a digit is not a placeholder, and `app/Ssr.fs` is full of them:
/// `/="?$/` is a regex end anchor, and `_$litType$` is the property name Lit marks its own
/// values with.
let private placeholder = Regex @"\$(\d+)"

/// Every slot the macro names, in the order written and with repeats kept — which of the two
/// readings a rule wants is its own business: one asks WHICH slots are named, the other how
/// OFTEN each is.
let substitutions (macro: string) =
    [ for m in placeholder.Matches macro -> int m.Groups.[1].Value ]

/// The macro this binding carries, and where it is written.
///
/// Only the raw `[<Emit>]` form. The named variants — EmitMethod, EmitConstructor,
/// EmitIndexer, EmitProperty — take a NAME and let Fable write the call around it, so there
/// is no macro in one to be wrong.
///
/// The attribute's range is also the identity of the binding for these rules. Asking the
/// symbol for its own location is not: FCS refuses `DeclarationLocation` outright for some of
/// them, and a module-level `let` reaches a walk twice anyway.
let macroOn (mfv: FSharpMemberOrFunctionOrValue) =
    mfv.Attributes
    |> Seq.tryPick (fun a ->
        if a.AttributeType.TryFullName = Some "Fable.Core.EmitAttribute" then
            match Seq.tryHead a.ConstructorArguments with
            | Some (_, (:? string as macro)) -> Some (a.Range, macro)
            | _ -> None
        else
            None)

/// Every binding in a file that could be carrying one.
let rec members (ds: FSharpImplementationFileDeclaration list) =
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

/// Every macro in a file, with the range to report it on.
let macros (tree: FSharpImplementationFileContents option) =
    match tree with
    | Some tree ->
        [ for mfv in members tree.Declarations do
              match macroOn mfv with
              | Some (range, macro) -> yield mfv, range, macro
              | None -> () ]
    | None -> []
