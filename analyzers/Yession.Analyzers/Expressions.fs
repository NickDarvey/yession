module Yession.Analyzers.Expressions

open System.Collections.Concurrent
open System.IO
open FSharp.Analyzers.SDK
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Symbols
open FSharp.Compiler.Text

/// What a rule about USE reads: every call this project's own code makes, and the binding each
/// one is written inside.
///
/// `Population` is the other half of the same idea and answers a different question — what a
/// project could NAME, which is what the scoping rules ask. A rule about what the code DOES
/// needs the expressions, and those come only from the project's own implementation files: a
/// referenced assembly hands over its declarations, never its bodies. That is not a limitation
/// to work around, it is the shape of the question. A use is somewhere, and the somewhere is
/// always here.
///
/// Read once per project and kept, for the same reason the population is: the walk is over
/// every expression in every file, and the answer is the same for each of them.

/// One call, as the caller wrote it.
type Call =
    { Callee: FSharpMemberOrFunctionOrValue
      /// The arguments in order, curried groups flattened — the order the callee's own
      /// parameters are in, so a rule can name a slot and mean the same thing at both ends.
      Args: FSharpExpr list
      Where: range }

/// One binding of the project's own, and what it calls.
type Binding =
    { /// Absent for a module-level `do`, which makes calls but is not itself callable.
      Owner: FSharpMemberOrFunctionOrValue option
      /// Its own parameters, curried groups flattened, in the order a caller supplies them.
      Parameters: FSharpMemberOrFunctionOrValue list
      Calls: Call list }

/// Whether two symbols are the same one. `=` on an `FSharpSymbol` compares the wrapper rather
/// than what it wraps, so a parameter read in a body and the same parameter in the binding's
/// own list are not equal by it; FCS answers this itself, and throws for the symbols it cannot.
let same (a: FSharpSymbol) (b: FSharpSymbol) =
    try a.IsEffectivelySameAs b with _ -> false

let rec private within (e: FSharpExpr) =
    seq {
        match e with
        | FSharpExprPatterns.Call (_, callee, _, _, args) -> yield { Callee = callee; Args = args; Where = e.Range }
        | _ -> ()

        // Always, including a call's own arguments: `envOr (name ()) ""` is two calls.
        for sub in e.ImmediateSubExpressions do
            yield! within sub
    }

let rec private declared (ds: FSharpImplementationFileDeclaration list) =
    seq {
        for d in ds do
            match d with
            | FSharpImplementationFileDeclaration.Entity (_, inner) -> yield! declared inner
            | FSharpImplementationFileDeclaration.MemberOrFunctionOrValue (mfv, parameters, body) ->
                yield
                    { Owner = Some mfv
                      Parameters = List.concat parameters
                      Calls = List.ofSeq (within body) }
            | FSharpImplementationFileDeclaration.InitAction body ->
                yield { Owner = None; Parameters = []; Calls = List.ofSeq (within body) }
    }

let private walk (results: FSharpCheckProjectResults) =
    [ for file in results.AssemblyContents.ImplementationFiles do
          yield! declared file.Declarations ]

let private cache = ConcurrentDictionary<string, Binding list> ()

let of' (ctx: CliContext) =
    cache.GetOrAdd (ctx.ProjectOptions.ProjectFileName, fun _ -> walk ctx.CheckProjectResults)

/// The file something is written in, spelled the way a person here would say it: relative to
/// the repository, so a message can name two of them and be read at a glance.
let fileOf (ctx: CliContext) (where: range) =
    let path = where.FileName.Replace ('\\', '/')

    match Population.repositoryOf (Path.GetDirectoryName ctx.ProjectOptions.ProjectFileName) with
    | Some root ->
        let prefix = (Path.GetFullPath root).Replace ('\\', '/') + "/"
        if path.StartsWith prefix then path.Substring prefix.Length else path
    | None -> path
