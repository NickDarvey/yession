module Yession.Analyzers.TemplateHoles

open FSharp.Analyzers.SDK
open FSharp.Compiler.Symbols

/// What a Lit template hole is allowed to render.
///
/// `html $"""<b>{x}</b>"""` types as `FormattableString -> TemplateResult`, so every hole
/// is boxed before Lit sees it and NOTHING downstream can say what went in. Put a record
/// in one and Lit prints its ToString; put a union in one and Fable prints the case name.
/// Both compile, both render, and the page is wrong in a way no test of the model can see.
///
/// The compiler cannot object — the box is an explicit `box`, not a conversion the
/// implicit-conversion warnings model — and `%s` cannot help either: F# rejects a format
/// specifier in a `FormattableString` outright (FS3376), which is exactly the type Lit's
/// tag takes. So the hole is the one place in a template where the type is known to the
/// compiler and to nobody else.
///
/// The typed tree still has it. A template compiles to
/// `FormattableStringFactory.Create(fmt, [| box<'T> hole; ... |])`, and `'T` there is the
/// type whose stringification a reader of the page actually gets. This asks what each one
/// is, and admits only the six things Lit renders on purpose.
module private Renderable =

    // Lit binds a hole in an `@event=` position to a listener. Nothing else in a template
    // is a function, so the shape is unambiguous and the argument type is free to grow
    // (an Event today, a KeyboardEvent tomorrow) without this rule having an opinion.
    let private isListener (t: FSharpType) = t.IsFunctionType

    let rec private isTemplateSequence (t: FSharpType) =
        let element =
            if t.IsGenericParameter then None
            elif t.HasTypeDefinition then
                match t.TypeDefinition.TryFullName with
                | Some ("Microsoft.FSharp.Collections.FSharpList`1"
                       | "System.Collections.Generic.IEnumerable`1"
                       | "Microsoft.FSharp.Collections.seq`1") -> Seq.tryHead t.GenericArguments
                | _ -> None
            else None

        match element with
        | Some e -> isRenderable e
        | None -> false

    and isRenderable (t: FSharpType) =
        let t = t.StripAbbreviations ()

        if isListener t then true
        elif isTemplateSequence t then true
        elif t.HasTypeDefinition then
            match t.TypeDefinition.TryFullName with
            // A string is the whole point: whatever the value was, someone decided how it
            // reads before it reached the template.
            | Some "System.String" -> true
            // A nested template, and a number or a flag, which have one obvious rendering
            // each (a count; a boolean attribute's `?disabled=${b}`).
            | Some "Lit.TemplateResult" -> true
            | Some "System.Int32" -> true
            | Some "System.Boolean" -> true
            | _ -> false
        else false

let private hole (e: FSharpExpr) =
    match e with
    | FSharpExprPatterns.Call (_, mfv, _, [ held ], [ arg ]) when mfv.DisplayName = "box" -> Some (arg.Range, held)
    | _ -> None

let private isTemplateFormat (mfv: FSharpMemberOrFunctionOrValue) =
    mfv.CompiledName = "Create"
    && mfv.DeclaringEntity
       |> Option.bind (fun e -> e.TryFullName)
       |> Option.contains "System.Runtime.CompilerServices.FormattableStringFactory"

let rec private walk report (e: FSharpExpr) =
    match e with
    | FSharpExprPatterns.Call (_, mfv, _, _, [ _; FSharpExprPatterns.NewArray (_, holes) ]) when isTemplateFormat mfv ->
        holes |> List.iter (hole >> Option.iter report)
    | _ -> ()

    for sub in e.ImmediateSubExpressions do
        walk report sub

let rec private declarations report (ds: FSharpImplementationFileDeclaration list) =
    for d in ds do
        match d with
        | FSharpImplementationFileDeclaration.Entity (_, nested) -> declarations report nested
        | FSharpImplementationFileDeclaration.MemberOrFunctionOrValue (_, _, body) -> walk report body
        | FSharpImplementationFileDeclaration.InitAction e -> walk report e

[<Literal>]
let Code = "YES001"

[<CliAnalyzer("TemplateHole", "A Lit template hole renders something Lit can render", "")>]
let templateHole: Analyzer<CliContext> =
    fun ctx ->
        async {
            let offenders = ResizeArray ()

            match ctx.TypedTree with
            | Some tree ->
                declarations
                    (fun (range, held) ->
                        if not (Renderable.isRenderable held) then
                            offenders.Add (range, held))
                    tree.Declarations
            | None -> ()

            return
                [ for (range, held: FSharpType) in offenders ->
                    { Type = "TemplateHole"
                      Message =
                        $"this template hole renders %s{held.Format FSharpDisplayContext.Empty}, which Lit stringifies "
                        + "however it happens to stringify. Render it to a string here."
                      Code = Code
                      Severity = Severity.Error
                      Range = range
                      Fixes = [] } ]
        }
