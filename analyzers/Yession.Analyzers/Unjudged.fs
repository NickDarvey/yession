module Yession.Analyzers.Unjudged

open FSharp.Analyzers.SDK
open FSharp.Compiler.Diagnostics

/// Whether what the rules read was the source anybody wrote.
///
/// Every rule here answers a question of the typed tree, and a declaration the compiler could
/// not build is not IN that tree — so no rule sees it, each correctly answers `[]`, and the
/// run ends in exactly the shape a clean one has. `lint` then reports the product clean and
/// exits 0 over source that does not compile.
///
/// It is not hypothetical, and the shape it takes is the one that hides best. A `let` written
/// at column 0 under a `namespace` — one stray indentation — is dropped along with the
/// `[<Emit>]` attribute sitting on it, so the emit rules pass over a macro that violates both
/// of them. Everything else in the file is still judged, which is why nothing looks wrong:
/// the run is not empty, it is short by exactly the declaration nobody could read.
///
/// The compiler already knows. `CheckFileResults` carries the diagnostics it produced getting
/// as far as it did, and they cost nothing extra — the type-check has happened by the time any
/// rule is called. So this reports the first error in the file and lets `lint` exit on its
/// own code, because "these rules did not read this" must not arrive looking like "these rules
/// found nothing".
///
/// Errors only. A warning is a complete tree with an opinion about it, and the rules read that
/// tree exactly as they read any other.

[<Literal>]
let Code = "YES000"

[<CliAnalyzer("Unjudged", "The rules read source the compiler could build", "")>]
let unjudged: Analyzer<CliContext> =
    fun ctx ->
        async {
            let errors =
                ctx.CheckFileResults.Diagnostics
                |> Array.filter (fun d -> d.Severity = FSharpDiagnosticSeverity.Error)

            return
                [ match Array.tryHead errors with
                  | Some first ->
                      yield
                          { Type = "Unjudged"
                            Message =
                              $"this file does not compile (%s{first.Message}), so whatever the "
                              + "compiler could not build is missing from the tree the rules read "
                              + "and their silence about it is not a verdict. Run `build`."
                            Code = Code
                            Severity = Severity.Error
                            Range = first.Range
                            Fixes = [] }
                  | None -> () ]
        }
