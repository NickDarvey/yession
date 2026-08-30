module Yession.Analyzers.EnvReaders

open FSharp.Analyzers.SDK
open Yession.Analyzers.Expressions

/// A variable is read from the environment in one place, and everywhere else is handed the
/// value.
///
/// The rule exists because the other shape failed silently, in the direction that costs the
/// most. `YESSION_SESSION_AGENT_BACKEND` had two readers: `SessionMain` parsed it at boot,
/// defaulting to `srt`, and used that to decide the session was confined — it even validated
/// srt's tools on the strength of it, fail-closed, so a box without bubblewrap refused to
/// start. `Agent.fs` then read the SAME variable again where the CLI is actually spawned,
/// defaulting to `host`. Both defaults were written to be right; neither author saw the other.
/// A deployment that set nothing therefore ran the agent CLI unconfined while every statement
/// the session made about itself — its boot checks, its logs, docs/GAPS.md — said srt. Nothing
/// was wrong at either site. The fault was that there were two.
///
/// So this is not a style rule about env access. A second reader is a second DEFAULT, and two
/// defaults that disagree resolve into the weaker one without anybody choosing it. One reader
/// cannot disagree with itself.
///
/// It replaced a suite that read F# source with regular expressions, and the difference is
/// what the population can be. That suite could only ask which files NAMED a variable, which
/// is a question with false answers in both directions — a comment naming it, a list of names
/// to forward into a child, a `Map.tryFind` over an env somebody else read — so it had to be
/// given the two variables it already knew about and the one file each was allowed in. Here
/// the question is which files READ it, the tree answers that outright, and the population is
/// every variable this repository names: thirty-three of them, two of which were being read
/// twice with a default apiece while the suite watched the two it had been told.
///
/// Per ASSEMBLY, because that is the unit that becomes a process. The suite and the Manager
/// read `ANTHROPIC_API_KEY` for their own reasons and neither is the other's second default.

[<Literal>]
let Code = "YES008"

let private describe (name: string) (files: string list) =
    let named = files |> List.distinct |> List.sort |> String.concat " and "

    $"`%s{name}` is read in %s{named}. A second reader is a second default, and two defaults "
    + "that disagree resolve into whichever the deployment happens to reach without anybody "
    + "choosing it — `YESSION_SESSION_AGENT_BACKEND` had two, written by authors who could not "
    + "see each other, and the weaker one won: the agent CLI ran unconfined while every "
    + "statement the session made about itself said srt. Read it once and hand the value down."

let private offenders (ctx: CliContext) =
    [ for (name, sites) in
          Environs.reads (Expressions.of' ctx)
          |> List.map (fun (name, where) -> name, (fileOf ctx where, where))
          |> List.groupBy fst do
          match sites |> List.map (fun (_, (file, _)) -> file) |> List.distinct with
          | _ :: _ :: _ as files ->
              // Anchored at every read, because none of them is the one that is wrong: which
              // of them should be the one reader is a decision, and each site is somewhere it
              // could be taken.
              for (_, (_, where)) in sites -> where, describe name files
          | _ -> () ]

[<CliAnalyzer("EnvReaders", "An environment variable is read in one place", "")>]
let envReaders: Analyzer<CliContext> =
    fun ctx ->
        async {
            if not (Population.reportsHere ctx) then
                return []
            else
                return
                    [ for (where, message) in List.distinct (offenders ctx) ->
                        { Type = "EnvReaders"
                          Message = message
                          Code = Code
                          Severity = Severity.Error
                          Range = where
                          Fixes = [] } ]
        }
