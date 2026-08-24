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
    { /// What this sandbox runs IN, when the repo asked for a container at all.
      ///
      /// `None` is not "confined" — it is the repo saying nothing, and what that means is the
      /// BACKEND's answer, not this file's. Nesting the container's own keys under one
      /// optional block is what makes `cmd` unwritable on a sandbox that has no container to
      /// run it: there is no flat `cmd` to mistype.
      Container : ContainerSpec option
      /// Relative to THIS repo's checkout.
      WorkingDirectory : string option
      /// `SecretRef` values name a secret; they never carry one, and the type cannot.
      EnvironmentVariables : Map<string, EnvironmentVariableRef>
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
        { Container = None
          WorkingDirectory = None
          EnvironmentVariables = Map.empty
          Net = []
          Read = []
          Forward = [] }

    /// What a declaration ASKS the session for, given where this repo's checkout is.
    ///
    /// A rename rather than a translation, and that is the design: the file is a
    /// serialization of a vocabulary `start_work_sandbox` already spoke, so there is no
    /// second policy engine here and nothing this can express that a command could not.
    ///
    /// The checkout is the one thing a file cannot know — a path in it is relative to a
    /// directory the SESSION chose — so it is supplied. `workdir` is already guaranteed to
    /// be inside the checkout by the decoder, which is where a path a person can fix is
    /// refused; joining is all that is left.
    ///
    /// `Container = None` becomes `Confinement`, and that is not the file saying "confine
    /// me". It is the file saying nothing, and what nothing means is the BACKEND's answer:
    /// docker starts its defaults, srt and host confine. `Sandboxes.forBackend` is where the
    /// two authors meet.
    let toRequest (checkout: string) (decl: SandboxDecl) : SandboxRequest =
        // Resolved rather than concatenated, and CLAMPED at the checkout. The decoder
        // already refuses a path that climbs out — that is the upstream fix, made where a
        // person can correct their file — and this is the downstream guard: a declaration
        // reaching here some other way still cannot name a directory above the checkout,
        // because there is no arithmetic here that could produce one.
        let under (dir: string) =
            let resolved =
                dir.Split ([| '/'; '\\' |])
                |> Array.fold
                    (fun acc segment ->
                        match segment with
                        | "" | "." -> acc
                        | ".." -> (match acc with [] -> [] | _ :: rest -> rest)
                        | segment -> segment :: acc)
                    []
                |> List.rev
            match resolved with
            | [] -> checkout.TrimEnd '/'
            | segments -> checkout.TrimEnd '/' + "/" + String.concat "/" segments
        { Spec =
            { WorkingDirectory = decl.WorkingDirectory |> Option.map under
              EnvironmentVariables = decl.EnvironmentVariables
              Net = decl.Net
              Read = decl.Read
              Runtime =
                match decl.Container with
                | Some container -> Container container
                | None -> Confinement }
          Forward = decl.Forward }

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

    /// A path INSIDE the checkout, and the only kind of path a file may write.
    ///
    /// A `yession.yaml` is authored by whoever can push to the repo, so an absolute path is
    /// that author naming a place on somebody else's machine, and a `..` segment is the same
    /// thing spelled relatively. Both are refused where they are WRITTEN — the person who
    /// can fix it is standing here — rather than resolved later against a checkout, which is
    /// how `workdir: /etc` becomes a sandbox that starts in /etc.
    let private inCheckout (what: string) : Decoder<string> =
        Decode.string
        |> Decode.andThen (fun raw ->
            let path = raw.Trim ()
            let segments = path.Split ([| '/'; '\\' |]) |> List.ofArray
            if path = "" then Decode.fail (sprintf "%s cannot be blank" what)
            elif path.StartsWith "/" || path.StartsWith "\\" then
                Decode.fail (sprintf "%s must be inside the checkout, and '%s' is absolute" what path)
            elif segments |> List.contains ".." then
                Decode.fail (sprintf "%s must be inside the checkout, and '%s' climbs out of it" what path)
            else Decode.succeed path)

    /// What a file may mount, and it is deliberately not a host path.
    ///
    /// `HostPath` exists and the SESSION uses it — that is how the repos directory reaches a
    /// container — but a source a repo could name is arbitrary read/write access to the
    /// machine running the session, which is the same authority `YESSION_BIN_*` carries and
    /// is refused for the same reason. A file gets its own checkout (`workspace`) and named
    /// volumes, which are the session's to create and nobody else's to reach into.
    let private mountSource : Decoder<MountSource> =
        Decode.string
        |> Decode.andThen (fun raw ->
            match raw.Trim () with
            | "workspace" -> Decode.succeed SessionWorkspace
            | source when source.StartsWith "/" || source.StartsWith "." || source.Contains "/" ->
                Decode.fail (
                    sprintf
                        "'%s' is a host path, and a %s may not name one — say 'workspace', or a named volume"
                        source
                        FileName)
            | "" -> Decode.fail "a volume needs a source"
            | name -> Decode.succeed (NamedVolume name))

    let private mount : Decoder<ContainerMount> =
        Decode.object (fun get ->
            { Source = get.Required.Field "source" mountSource
              Target = get.Required.Field "target" Decode.string
              Mode =
                match get.Optional.Field "mode" Decode.string with
                | Some "ro" -> ReadOnly
                | _ -> ReadWrite })

    let private containerKeys = [ "image"; "build"; "volumes"; "cmd" ]

    /// The container block. `cmd` lives HERE and nowhere else, which is the whole reason the
    /// block exists: a sandbox with no container has no place to write one.
    let private container : Decoder<ContainerSpec> =
        noUnknownKeys containerKeys
        |> Decode.andThen (fun () ->
            Decode.object (fun get ->
                { Image = get.Optional.Field "image" image
                  Build = get.Optional.Field "build" build
                  Mounts = get.Optional.Field "volumes" (Decode.list mount) |> Option.defaultValue []
                  Command = get.Optional.Field "cmd" Decode.string }))

    let private sandboxKeys = [ "container"; "workdir"; "env"; "net"; "read"; "forward" ]

    let private sandbox : Decoder<SandboxDecl> =
        noUnknownKeys sandboxKeys
        |> Decode.andThen (fun () ->
            Decode.object (fun get ->
                { Container = get.Optional.Field "container" container
                  WorkingDirectory = get.Optional.Field "workdir" (inCheckout "workdir")
                  EnvironmentVariables =
                    get.Optional.Field "env" environment |> Option.defaultValue Map.empty
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
