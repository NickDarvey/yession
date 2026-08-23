namespace Yession.Domain

// What a repo asks a session for (`yession.yaml`, Plan 27).
//
// The whole file is SANDBOXES, and that is the design rather than a starting point. The
// constraint is a complete algebra: every key needs a defined answer for "two repos both
// said something", and a sandbox is the only scope where that answer is TOTAL — it is
// named, and the name is scoped to its repo (`SandboxScope`), so the union of two files is
// disjoint by construction. No precedence rule, nothing shadows anything.
//
// So the rule for every future key: a key belongs here only if its scope is a sandbox.
// Anything session-wide (approval gates, MCP servers, a dependency on another repo) has no
// honest tie-break between two repos that disagree, and stays the operator's.
//
// This module is PURE and parser-free. It decodes an already-parsed JSON tree, which is
// what lets the whole of it — every refusal included — run in the cheap tier on both
// runtimes from JSON literals. YAML is a superset of JSON, so the bridge that reads the
// file only has to hand this a tree; swapping the surface syntax later is a parser swap.

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// One sandbox as a repo declares it. A serialization of `EnvironmentSpec` plus the three
/// things `start_work_sandbox` already takes, so nothing here is a new concept — the file
/// says what the commands could already be told.
type SandboxDecl =
    { /// Docker backend only. A file naming one under `srt`/`host` is refused when it is
      /// applied, not here: what backend a session runs is the operator's, and this module
      /// has no way to know it.
      Image : ContainerImage option
      Build : ContainerBuildSpec option
      /// Relative to THIS repo's checkout.
      WorkingDirectory : string option
      /// `SecretRef` values name a secret; they never carry one, and the type cannot.
      EnvironmentVariables : Map<string, EnvironmentVariableRef>
      Mounts : ContainerMount list
      /// The sandbox's own process (compose's `command`). Docker only, same as `Image`.
      Command : string option
      /// Egress this sandbox may reach. Widening the operator's ceiling is a REQUEST — it
      /// goes through the gate when the fold authors `start_work_sandbox` — so this is what
      /// is asked for, never what is granted.
      Net : string list
      /// Extra host paths it may read. Same rule.
      Read : string list
      /// Credential NAMES to forward. Resolved for a human at spawn; a value never appears
      /// in a file, and could not: the type is a name.
      Forward : string list }

module SandboxDecl =

    let empty : SandboxDecl =
        { Image = None
          Build = None
          WorkingDirectory = None
          EnvironmentVariables = Map.empty
          Mounts = []
          Command = None
          Net = []
          Read = []
          Forward = [] }

/// One repo's whole file.
type ConfigFile =
    { /// Refused if it is not a version this build speaks. A file from the future says so
      /// rather than losing half its meaning to a decoder that skips what it cannot read.
      Version : int
      Sandboxes : Map<SandboxName, SandboxDecl> }

module ConfigFile =

    /// The name the file has at the root of a checkout.
    [<Literal>]
    let FileName = "yession.yaml"

    /// The only version this build speaks.
    [<Literal>]
    let Version = 1

    /// Variables a file may not set, by prefix.
    ///
    /// `YESSION_LAUNCH` carries a launch's control secret — custody of the session's
    /// secrets and the authority to register as an OIDC client — and `YESSION_BIN_*` names
    /// a binary this host will execute. Rather than enumerate which of them are dangerous
    /// and re-decide every time one is added, the whole prefix is refused: everything under
    /// it is either the operator's or nobody's.
    ///
    /// A REFUSAL and not a filter. Silently dropping the variable would leave a file that
    /// reads as if it had been applied.
    [<Literal>]
    let ReservedPrefix = "YESSION_"

    let private failIf (condition: bool) (message: string) (decoder: Decoder<'a>) : Decoder<'a> =
        if condition then Decode.fail message else decoder

    /// Refuse any key the schema does not define.
    ///
    /// A typo that decodes to "nothing was asked for" is the failure mode this whole file
    /// exists to avoid: it reads as configuration and behaves as none.
    let private noUnknownKeys (known: string list) : Decoder<unit> =
        Decode.keys
        |> Decode.andThen (fun keys ->
            match keys |> List.filter (fun k -> not (List.contains k known)) with
            | [] -> Decode.succeed ()
            | unknown ->
                Decode.fail (
                    sprintf
                        "unknown %s: %s (known: %s)"
                        (if List.length unknown = 1 then "key" else "keys")
                        (String.concat ", " (List.sort unknown))
                        (String.concat ", " known)))

    let private stringList : Decoder<string list> =
        Decode.oneOf [ Decode.list Decode.string; Decode.string |> Decode.map List.singleton ]

    /// `NAME: value` or `NAME: { secret: name }`. Two forms and no third — a plain string
    /// is a value, a mapping names a secret, and there is no interpolation syntax that
    /// could be either.
    let private envValue : Decoder<EnvironmentVariableRef> =
        Decode.oneOf
            [ Decode.string |> Decode.map PlainValue
              Decode.field "secret" Decode.string
              |> Decode.andThen (fun raw ->
                  match SecretName.create raw with
                  | Ok name -> Decode.succeed (SecretRef name)
                  | Error e -> Decode.fail e) ]

    let private environment : Decoder<Map<string, EnvironmentVariableRef>> =
        Decode.keyValuePairs envValue
        |> Decode.andThen (fun pairs ->
            match pairs |> List.map fst |> List.filter (fun k -> k.StartsWith ReservedPrefix) with
            | [] -> Decode.succeed (Map.ofList pairs)
            | reserved ->
                Decode.fail (
                    sprintf
                        "%s is reserved and a repo may not set it: %s"
                        ReservedPrefix
                        (String.concat ", " (List.sort reserved))))

    let private image : Decoder<ContainerImage> =
        Decode.string
        |> Decode.map (fun raw ->
            match raw.Split ':' with
            | [| name; tag |] -> { Name = name; Tag = Some tag }
            | _ -> { Name = raw; Tag = None })

    let private build : Decoder<ContainerBuildSpec> =
        Decode.oneOf
            [ Decode.string |> Decode.map (fun path -> { ContextPath = path; DockerfilePath = None })
              Decode.object (fun get ->
                  { ContextPath = get.Required.Field "context" Decode.string
                    DockerfilePath = get.Optional.Field "dockerfile" Decode.string }) ]

    let private mount : Decoder<ContainerMount> =
        Decode.object (fun get ->
            { Source = get.Required.Field "source" Decode.string |> HostPath
              Target = get.Required.Field "target" Decode.string
              Mode =
                match get.Optional.Field "mode" Decode.string with
                | Some "ro" -> ReadOnly
                | _ -> ReadWrite })

    let private sandboxKeys =
        [ "image"; "build"; "workdir"; "env"; "volumes"; "cmd"; "net"; "read"; "forward" ]

    let private sandbox : Decoder<SandboxDecl> =
        noUnknownKeys sandboxKeys
        |> Decode.andThen (fun () ->
            Decode.object (fun get ->
                { Image = get.Optional.Field "image" image
                  Build = get.Optional.Field "build" build
                  WorkingDirectory = get.Optional.Field "workdir" Decode.string
                  EnvironmentVariables =
                    get.Optional.Field "env" environment |> Option.defaultValue Map.empty
                  Mounts = get.Optional.Field "volumes" (Decode.list mount) |> Option.defaultValue []
                  Command = get.Optional.Field "cmd" Decode.string
                  Net = get.Optional.Field "net" stringList |> Option.defaultValue []
                  Read = get.Optional.Field "read" stringList |> Option.defaultValue []
                  Forward = get.Optional.Field "forward" stringList |> Option.defaultValue [] }))

    /// Sandbox names, refusing a clash INSIDE one file.
    ///
    /// This is the only place a clash can happen — across files the scope keeps them apart —
    /// and it is refused here, where the person who wrote both is standing and can pick
    /// another name, rather than resolved at read time by a precedence rule.
    let private sandboxes : Decoder<Map<SandboxName, SandboxDecl>> =
        Decode.keyValuePairs sandbox
        |> Decode.andThen (fun pairs ->
            let named =
                pairs
                |> List.map (fun (raw, decl) -> SandboxName.create raw |> Result.map (fun n -> n, decl))
            match named |> List.choose (function Error e -> Some e | Ok _ -> None) with
            | e :: _ -> Decode.fail e
            | [] ->
                let entries = named |> List.choose (function Ok v -> Some v | Error _ -> None)
                let names = entries |> List.map fst
                match names |> List.countBy id |> List.filter (fun (_, n) -> n > 1) with
                | [] -> Decode.succeed (Map.ofList entries)
                | dupes ->
                    Decode.fail (
                        sprintf
                            "declared twice: %s"
                            (dupes |> List.map (fst >> SandboxName.value) |> List.sort |> String.concat ", ")))

    let private fileKeys = [ "version"; "sandboxes" ]

    let decoder : Decoder<ConfigFile> =
        noUnknownKeys fileKeys
        |> Decode.andThen (fun () ->
            Decode.field "version" Decode.int
            |> Decode.andThen (fun version ->
                failIf
                    (version <> Version)
                    (sprintf "this build speaks %s version %d, not %d" FileName Version version)
                    (Decode.object (fun get ->
                        { Version = version
                          Sandboxes =
                            get.Optional.Field "sandboxes" sandboxes |> Option.defaultValue Map.empty }))))

    /// Decode one repo's file from already-parsed JSON text.
    let parse (json: string) : Result<ConfigFile, string> =
        Decode.fromString decoder json

    /// What ONE repo's file contributes to the session, keyed so it cannot collide with
    /// another repo's.
    let scoped (repo: RepoRef) (file: ConfigFile) : Map<SandboxRef, SandboxDecl> =
        file.Sandboxes
        |> Map.toList
        |> List.map (fun (name, decl) -> SandboxRef.inScope repo name, decl)
        |> Map.ofList

    /// Every declared sandbox in the session, from every repo that carries a file.
    ///
    /// TOTAL, and that is the whole point: the keys are (repo, name) pairs and the repos
    /// are disjoint, so this is a union that cannot lose an entry and has no order
    /// dependence. There is deliberately no merge rule, because there is nothing to merge.
    let union (files: (RepoRef * ConfigFile) list) : Map<SandboxRef, SandboxDecl> =
        files
        |> List.collect (fun (repo, file) -> scoped repo file |> Map.toList)
        |> Map.ofList
