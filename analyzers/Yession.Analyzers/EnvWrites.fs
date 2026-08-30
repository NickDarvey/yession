module Yession.Analyzers.EnvWrites

open FSharp.Analyzers.SDK
open Yession.Analyzers.Expressions

/// A process has one environment, and one place that writes it.
///
/// The rule exists because breaking it is invisible. A test suite is ONE process, so a case
/// that writes `process.env` writes it for every case compiled after it — and the half
/// everyone forgets is putting it back. `Phase2`'s credential-leak regression planted
/// `ANTHROPIC_API_KEY` and DELETED it on the way out, which is not a restore, it is a clear:
/// every `LiveAgent` suite after it ran with no credential, `SessionMain` answers no
/// credential by starting NO AGENT, and the live clone case got a session that accepted a
/// message and never replied. No error. Nothing anywhere saying why. Four attempts to find it.
///
/// `Support.withEnv` made take-and-give-back available; this is what makes it the only way.
/// The guard is on the WRITE rather than on its consequence for the reason the two halves
/// happen in different places: the mutation is in the cheap tier and only its consequence
/// needs `LiveAgent`, so a guard on the consequence fires on a tier almost nobody runs,
/// months later. A guard on the source fires on the pull request that writes it.
///
/// It replaced a suite that read F# source with regular expressions and could not tell a
/// write from the mention of one. Its patterns matched an `[<Emit>]` macro's TEXT, so
/// declaring the JavaScript that assigns counted the same as running it — which is why it had
/// to be scoped by hand to one directory, and why the product's own `Interop.setEnv` was
/// invisible to it either way. Here a declaration is a declaration and a call is a call: the
/// sites are the calls, the macro says whether a call writes, and no directory list is needed
/// to keep the two apart.
///
/// One file per ASSEMBLY, because that is the unit that becomes a process. The test suite and
/// the Manager write their own environments for their own reasons and neither is the other's
/// business.

[<Literal>]
let Code = "YES007"

let private describe (files: string list) =
    let named = files |> List.distinct |> List.sort |> String.concat " and "

    $"the process environment is written from %s{named}. A process has one environment, so a "
    + "write here is a write for everything that runs after it — and the half that is easy to "
    + "forget is putting back what was there, absence included. Keep the writes in one place "
    + "and let that place own the restore: in the test suite `Support.withEnv` is that verb, "
    + "and it gives back exactly what it found on the exceptional path too."

let private offenders (ctx: CliContext) =
    let sites = Environs.writes (Expressions.of' ctx) |> List.map (fun where -> fileOf ctx where, where)

    match sites |> List.map fst |> List.distinct with
    | _ :: _ :: _ as files ->
        // Anchored at every write, because none of them is the one that is wrong: which place
        // the environment should be written from is a decision, and each site is somewhere it
        // could be taken.
        [ for (_, where) in sites -> where, describe files ]
    | _ -> []

[<CliAnalyzer("EnvWrites", "The process environment is written from one place", "")>]
let envWrites: Analyzer<CliContext> =
    fun ctx ->
        async {
            if not (Population.reportsHere ctx) then
                return []
            else
                return
                    [ for (where, message) in List.distinct (offenders ctx) ->
                        { Type = "EnvWrites"
                          Message = message
                          Code = Code
                          Severity = Severity.Error
                          Range = where
                          Fixes = [] } ]
        }
