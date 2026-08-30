module Yession.Analyzers.Environs

open System.Text.RegularExpressions
open FSharp.Compiler.Symbols
open FSharp.Compiler.Text
open Yession.Analyzers.Expressions

/// Finding an access to the process environment, for the two rules that have something to say
/// about one.
///
/// They ask different questions of it — which variable is read where (`EnvReaders`), and where
/// the environment is written at all (`EnvWrites`) — and share the answer to "is this call an
/// access, and which of its arguments carries the name". A rule that classified an access
/// slightly differently from its sibling would be a rule with its own blind spot.
///
/// Two forms reach it. On the CLR it is `System.Environment`'s pair, named outright. Under
/// Fable it is an `[<Emit>]` macro subscripting `process.env`, so the macro's own text says
/// which of the two it is — and which argument the name arrives in, because the `$0` in
/// `process.env[$0]` is exactly that. Reading the JavaScript is not reading F# source: the
/// macro is a value hanging off the typed tree, `Emits` already hands it to two other rules,
/// and what is asked of it here is where a placeholder sits, which is what `Emits` reads too.

/// What a call does to the environment.
type Access =
    /// Reads the variable named by the argument in this slot.
    | Reads of slot: int
    /// Writes the environment. No slot, because the rule about writes does not ask which
    /// variable — it asks where — and a write's name is as often a parameter as a literal.
    | Writes

/// `process.env[$N]`, and whether what surrounds it makes the access a write: `delete` in
/// front of it, or an assignment behind. The exclusion is what keeps a comparison out: `==`
/// and `===` are reads of it, and `=>` opens a lambda.
let private subscript = Regex @"(delete\s+)?process\.env\[\$(\d+)\]\s*(=[^=>])?"

/// A macro that only SPREADS the environment into a child's — `{ ...process.env, X: $1 }`, the
/// shape half the process-spawning fixtures use — names no variable of its own and subscripts
/// nothing, so it matches neither and is not an access. That is the discrimination the source
/// scan this replaced could not make: to a line reader, spreading, comparing and assigning are
/// all the characters `process.env`.
let private inMacro (macro: string) =
    let found = [ for m in subscript.Matches macro -> m ]

    if found |> List.exists (fun m -> m.Groups.[1].Success || m.Groups.[3].Success) then
        Some Writes
    else
        found |> List.tryHead |> Option.map (fun m -> Reads (int m.Groups.[2].Value))

let private inBcl (mfv: FSharpMemberOrFunctionOrValue) =
    let declaring =
        try mfv.DeclaringEntity |> Option.bind (fun e -> e.TryFullName) with _ -> None

    match declaring with
    | Some "System.Environment" ->
        match mfv.LogicalName with
        | "GetEnvironmentVariable" -> Some (Reads 0)
        | "SetEnvironmentVariable" -> Some Writes
        | _ -> None
    | _ -> None

/// What a call does, reading the callee alone.
let direct (mfv: FSharpMemberOrFunctionOrValue) =
    match inBcl mfv with
    | Some access -> Some access
    | None -> Emits.macroOn mfv |> Option.bind (fun (_, macro) -> inMacro macro)

/// Two symbols are one key when they are the same binding. Overloads collapse together, which
/// costs nothing here: the pair this cares about is not overloaded on what it does to the
/// environment.
let private key (mfv: FSharpMemberOrFunctionOrValue) =
    try
        let owner =
            mfv.DeclaringEntity |> Option.bind (fun e -> e.TryFullName) |> Option.defaultValue ""

        Some (owner + "." + mfv.LogicalName)
    with _ ->
        None

let private readSlot (known: Map<string, int>) (call: Call) =
    match direct call.Callee with
    | Some (Reads slot) -> Some slot
    | Some Writes -> None
    | None -> key call.Callee |> Option.bind (fun k -> Map.tryFind k known)

/// Which of a binding's own parameters an argument is, if it is one of them unchanged.
let private passedThrough (parameters: FSharpMemberOrFunctionOrValue list) (arg: FSharpExpr) =
    match arg with
    | FSharpExprPatterns.Value v -> parameters |> List.tryFindIndex (fun p -> same p v)
    | _ -> None

/// Every reader a project can reach, its own included: a binding that hands one of its
/// parameters straight to a reader is a reader too, in the slot that parameter arrives in.
///
/// Without that step a wrapper hides the variable rather than the read. `Tags.getEnv` is four
/// lines choosing between the two forms above by runtime, and everything it is called with is
/// a literal — so the reads are all at ITS call sites, and a rule looking only for direct ones
/// would find `Tags` reading a variable it cannot name, three times.
///
/// It grows through bindings only, never through the lambdas inside one, and that is what
/// keeps `Support.withEnv` out: the verb that takes the environment and gives it back applies
/// its bindings through a local function, over names that arrive in a list rather than a
/// parameter. A step that reached into it would make every test that ever took a variable a
/// reader of it — which is true, and the opposite of what the rule is for.
let private readers (bindings: Binding list) =
    let rec settle (known: Map<string, int>) =
        let grown =
            (known, bindings)
            ||> List.fold (fun known binding ->
                match binding.Owner |> Option.bind key with
                | Some owner when not (known.ContainsKey owner) ->
                    let slot =
                        binding.Calls
                        |> List.tryPick (fun call ->
                            readSlot known call
                            |> Option.bind (fun slot -> List.tryItem slot call.Args)
                            |> Option.bind (passedThrough binding.Parameters))

                    match slot with
                    | Some slot -> Map.add owner slot known
                    | None -> known
                | _ -> known)

        if grown.Count = known.Count then known else settle grown

    settle Map.empty

/// Every read of a variable this project names outright, and where it is read.
///
/// A name that is not a literal is not one of these — `sprintf "YESSION_WEBHOOK_SIGNATURE_%s"`
/// reads a family rather than a variable, and there is nothing for a rule to count. A
/// `[<Literal>]` is: the compiler has already put its value in the caller, which is what makes
/// `Launch.Variable` a read of `YESSION_LAUNCH` at the one place that reads it.
let reads (bindings: Binding list) : (string * range) list =
    let known = readers bindings

    [ for binding in bindings do
          for call in binding.Calls do
              match readSlot known call |> Option.bind (fun slot -> List.tryItem slot call.Args) with
              | Some (FSharpExprPatterns.Const ((:? string as name), _)) -> yield name, call.Where
              | _ -> () ]

/// Every write of the environment, and where it is made.
///
/// Direct calls only, and deliberately: a binding that wraps a write IS the one place the
/// environment is written from, and everything routing through it is going through that place.
/// Growing through wrappers the way `reads` does would say the opposite — it would report every
/// caller of `Support.withEnv`, which is the verb whose whole job is to make a write safe.
let writes (bindings: Binding list) : range list =
    [ for binding in bindings do
          for call in binding.Calls do
              if direct call.Callee = Some Writes then
                  yield call.Where ]
