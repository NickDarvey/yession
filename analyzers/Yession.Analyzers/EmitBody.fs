module Yession.Analyzers.EmitBody

open System.Text.RegularExpressions
open FSharp.Analyzers.SDK

/// What an `[<Emit>]` macro's JavaScript is allowed to assume about its substitutions.
///
/// Fable substitutes `$0`, `$1`, ... with the caller's argument TEXT, not a value, and pastes
/// the result into the caller's scope. Two things follow that nobody expects while writing JS
/// inside a string:
///
///   * a declaration can COLLIDE with the caller's own variable. `const pc = $0` at a call
///     site that passes a variable named `pc` emits `const pc = pc` — a temporal dead zone
///     ReferenceError. It took the whole shell down and reported as eight browser cases timing
///     out with nothing in common.
///   * a repeated placeholder EVALUATES its argument again. `$0` written three times evaluated
///     a fresh peer-id mint three times, so a first visit stored one id and returned another,
///     and every peer-scoped call was denied for the life of that launch.
///
/// Both were written down as comments beside the emits that caused them. That is what we had
/// instead of a check, and it did not work: the emit in `examples/serial/src/Ws.fs` broke the
/// rule its own comment stated, twice, and one of those was a live error.
///
/// The rule makes both unrepresentable rather than describing them. When the substitutions
/// arrive as real function parameters, a parameter binds INSIDE the function while the
/// argument is evaluated OUTSIDE it, exactly once — so nothing inside can collide with a
/// caller's identifier however it is spelled, and nothing is evaluated twice:
///
///     (() => { const peer = $0; ... })()     ->     (function (pc) { ... })($0)
///
/// It applies only to a macro that actually substitutes something. With no `$n` there is no
/// caller text to collide with and nothing to evaluate twice, so a body that takes no
/// arguments is free to declare whatever it likes.
///
/// This was a test suite that found emits by matching
/// `[<Emit(...)>]` in F# SOURCE, with a hand-assembled pattern for the triple-quoted and
/// escaped-string forms and a hand-kept list of directories to walk. That scan could not
/// write its own fixtures — a violating macro quoted literally in it would BE a violating
/// macro in a scanned file — so it assembled them from fragments, and it needed a case
/// asserting it had matched at least 300 emits, because a pattern that has stopped seeing
/// them and a codebase that obeys the rule read identically in a green run. Reading the
/// attribute's VALUE off the typed tree costs none of that: the string arrives already
/// parsed, the population is every project in the solution, and the fixture beside this rule
/// says in `// YES003` markers exactly which of its macros must be reported.

/// A JS binding form. `function`/`class` are here because a named function declaration
/// shadows exactly as a `const` does.
let private declaration = Regex @"\b(?:const|let|var|function|class)\s+[A-Za-z_$][\w$]*"

/// The safe shape: the substitutions arrive as parameters of a real function.
let private parameterised = Regex @"\(\s*(?:async\s+)?function\s*\([^)]*[A-Za-z_$]"

/// JS comments are prose, and prose about this rule necessarily contains examples of breaking
/// it. Strip them before looking for declarations — without this the rule goes red on the very
/// comment that warns about the thing.
///
/// Comments are NOT stripped before counting substitutions, and that is deliberate: a `$n` in
/// a comment is substituted like any other text, so one written twice really is two
/// evaluations.
let private withoutComments (js: string) =
    let noBlocks = Regex.Replace (js, @"/\*.*?\*/", "", RegexOptions.Singleline)
    Regex.Replace (noBlocks, @"//[^\n]*", "")

let private remedy =
    "Take the substitutions as parameters of a real function, so a parameter binds inside it "
    + "while the argument is evaluated outside it, once: (function (peer, onDead) { ... })($0, $1)."

/// Every fault in one macro, as a sentence each — the two are independent and each is fixed
/// on its own, so a macro with both is two diagnostics rather than one naming both.
let private faults (macro: string) =
    let substituted = Emits.substitutions macro

    if List.isEmpty substituted || parameterised.IsMatch macro then
        []
    else
        [ if declaration.IsMatch (withoutComments macro) then
              yield
                  "this emit macro declares a JavaScript binding while substituting the caller's "
                  + "text, so the declaration can collide with a variable the caller happens to have "
                  + $"named the same and emit `const x = x`. %s{remedy}"

          for slot, uses in List.countBy id substituted do
              if uses > 1 then
                  yield
                      $"this emit macro reads $%d{slot} %d{uses} times, so the caller's argument "
                      + $"expression is evaluated %d{uses} times rather than once. %s{remedy}" ]

[<Literal>]
let Code = "YES003"

[<CliAnalyzer("EmitBody", "An [<Emit>] macro that substitutes takes its substitutions as parameters", "")>]
let emitBody: Analyzer<CliContext> =
    fun ctx ->
        async {
            let offenders =
                [ for _, range, macro in Emits.macros ctx.TypedTree do
                      for fault in faults macro -> range, fault ]
                // As in `EmitMacro`: a module-level `let` reaches the walk twice, and the
                // attribute's range is the binding's identity here.
                |> List.distinct

            return
                [ for (range, message) in offenders ->
                    { Type = "EmitBody"
                      Message = message
                      Code = Code
                      Severity = Severity.Error
                      Range = range
                      Fixes = [] } ]
        }
