module Yession.Analyzers.EmptyDefault

open FSharp.Compiler.Symbols
open FSharp.Analyzers.SDK
open Yession.Analyzers.Expressions

/// A missing value is not turned into an empty string.
///
/// `Option.defaultValue "" opt` and `defaultArg opt ""` both say the same thing: when the
/// value is absent, pretend it was present and equal to `""`. That is the fault the whole
/// codebase's no-value discipline exists to prevent — the absence and a real empty value
/// become one thing the instant they meet, and every reader downstream inherits a plausible
/// wrong answer it can no longer question. `Control.fs` proved a control secret present at one
/// gate and then wrote `Option.defaultValue "" secret` at eight more, so a drift in that gate
/// would have fed an empty launch key straight to the privileged handlers; a JSON decoder that
/// reads an optional field and defaults it to `""` mints a domain value out of a field that
/// was never sent. In neither case is `""` a value anyone chose — it is the hole, spelled as
/// though it were not one.
///
/// So the fix is never a different literal. It is to answer the no-value case where it arises
/// — refuse it, propagate the `option`, or supply a default that MEANS something at that site —
/// or to make the absence unrepresentable, so there is no `None` left to default. A decoder
/// whose field is genuinely optional carries that fact into its domain type as an `option`
/// rather than erasing it at the edge; one whose field is required decodes with `Required` and
/// fails loudly when it is missing.
///
/// The one shape left alone is render-into-string: `opt |> Option.map f |> Option.defaultValue
/// ""`, where the present value has already been transformed into a string and the empty one is
/// the empty render, not a stand-in for a value. There the `Option.map` is the tell — the
/// default sits on the mapped option, never on a raw one — and its absence is what marks every
/// other site as one with a real no-value case still to answer.

[<Literal>]
let Code = "YES009"

/// The compiled home and name of a core function, read the safe way — `TryFullName` throws for
/// some entities, and a callee that cannot answer is simply not the one being matched.
let private isFunction (moduleFullName: string) (logicalName: string) (mfv: FSharpMemberOrFunctionOrValue) =
    mfv.LogicalName = logicalName
    && (try mfv.DeclaringEntity |> Option.bind (fun e -> e.TryFullName) with _ -> None) = Some moduleFullName

// `module Option` compiles to `OptionModule` so it does not clash with the `Option<_>` type;
// `defaultArg` and the pipe operators are operators, so they live in `Operators`.
let private isOptionDefaultValue = isFunction "Microsoft.FSharp.Core.OptionModule" "defaultValue"
let private isDefaultArg = isFunction "Microsoft.FSharp.Core.Operators" "defaultArg"
let private isOptionMap = isFunction "Microsoft.FSharp.Core.OptionModule" "map"
let private isPipeRight = isFunction "Microsoft.FSharp.Core.Operators" "op_PipeRight"
let private isPipeLeft = isFunction "Microsoft.FSharp.Core.Operators" "op_PipeLeft"

/// The library function an expression ultimately applies, seen through the wrappers FCS builds
/// around a piped or partially applied one. `x |> f` is an explicit `op_PipeRight x f`, and the
/// pipe's function side is itself lowered — `Option.defaultValue ""` becomes `let tmp = "" in
/// fun opt -> Option.defaultValue tmp opt`, a let over a lambda over the real call. Walk down to
/// that call, so `headFunction` of every spelling of `… Option.defaultValue …` is `defaultValue`
/// and of every spelling of `… Option.map …` is `map`.
let rec private headFunction (e: FSharpExpr) : FSharpMemberOrFunctionOrValue option =
    match e with
    | FSharpExprPatterns.Call (_, callee, _, _, args) ->
        if isPipeRight callee then args |> List.tryItem 1 |> Option.bind headFunction
        elif isPipeLeft callee then args |> List.tryHead |> Option.bind headFunction
        else Some callee
    | FSharpExprPatterns.Let (_, body) -> headFunction body
    | FSharpExprPatterns.Lambda (_, body) -> headFunction body
    | FSharpExprPatterns.Application (f, _, _) -> headFunction f
    | _ -> None

/// An empty-string literal.
let private (|EmptyString|_|) (e: FSharpExpr) =
    match e with
    | FSharpExprPatterns.Const ((:? string as s), _) when s = "" -> Some ()
    | _ -> None

/// A value produced by `Option.map` — the render-into-string shape this rule leaves alone.
let private isMapped (opt: FSharpExpr) =
    match headFunction opt with
    | Some f -> isOptionMap f
    | None -> false

/// The `""` that a piped default hoists out: `x |> Option.defaultValue ""` puts the default in a
/// `let` binding directly under the pipe's function side, so an empty-string literal sitting as
/// an immediate child of that partial is the empty default.
let private hoistsEmptyDefault (rhs: FSharpExpr) =
    rhs.ImmediateSubExpressions
    |> Seq.exists (function
        | EmptyString -> true
        | _ -> false)

let private isDefaultFunction (f: FSharpMemberOrFunctionOrValue) = isOptionDefaultValue f || isDefaultArg f

/// The default of `""` on a raw option, in every spelling. Directly: `Option.defaultValue`
/// takes the default first and the option second, `defaultArg` the other way round. Piped:
/// `optExpr |> Option.defaultValue ""`, where the pipe carries the raw option on its left and
/// the hoisted `""` on its right. In both, the render-into-string shape (`opt` is an
/// `Option.map` result) is left alone.
let private isEmptyDefault (call: Call) =
    if isOptionDefaultValue call.Callee then
        match call.Args with
        | [ EmptyString; opt ] -> not (isMapped opt)
        | _ -> false
    elif isDefaultArg call.Callee then
        match call.Args with
        | [ opt; EmptyString ] -> not (isMapped opt)
        | _ -> false
    elif isPipeRight call.Callee then
        match call.Args with
        | [ opt; rhs ] ->
            (headFunction rhs |> Option.exists isDefaultFunction) && hoistsEmptyDefault rhs && not (isMapped opt)
        | _ -> false
    elif isPipeLeft call.Callee then
        match call.Args with
        | [ rhs; opt ] ->
            (headFunction rhs |> Option.exists isDefaultFunction) && hoistsEmptyDefault rhs && not (isMapped opt)
        | _ -> false
    else
        false

let private message =
    "a missing value is being defaulted to an empty string. `\"\"` is not a value the caller "
    + "asked for — it is the absence, spelled as though it were present, and every reader "
    + "downstream now cannot tell the two apart. Answer the no-value case here (refuse it, "
    + "propagate the `option`, or default to something that MEANS something), or make the "
    + "absence unrepresentable so there is no `None` to default — for a decoder, carry an "
    + "optional field into the domain as an `option`, or decode a required one with `Required` "
    + "so it fails when it is missing. If the absent value truly renders as nothing, map the "
    + "present one with `Option.map` first, and the `\"\"` becomes the empty render."

[<CliAnalyzer("EmptyDefault", "A missing value is not defaulted to an empty string", "")>]
let emptyDefault: Analyzer<CliContext> =
    fun ctx ->
        async {
            if not (Population.reportsHere ctx) then
                return []
            else
                let offenders =
                    [ for binding in Expressions.of' ctx do
                          for call in binding.Calls do
                              if isEmptyDefault call then
                                  yield call.Where ]
                    |> List.distinct

                return
                    [ for where in offenders ->
                        { Type = "EmptyDefault"
                          Message = message
                          Code = Code
                          Severity = Severity.Error
                          Range = where
                          Fixes = [] } ]
        }
