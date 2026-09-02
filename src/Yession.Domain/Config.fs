namespace Yession.Domain.Sandboxes

open Yession.Domain

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
      /// The operator's resources this sandbox selects, by name.
      ///
      /// A repo can never write a host path or a hostname. It does not know this machine's
      /// layout, and the same file has to work on a laptop, in CI, and on a host that keeps
      /// its caches somewhere else — so it names what the operator declared, and the
      /// operator owns what those names come to.
      ///
      /// This replaced `net:` and `read:`, which were the opposite arrangement: a repo
      /// naming a hostname and a host path directly, bounded by an environment variable that
      /// was a ceiling AND an unconditional grant at once. A repo's `read:` could therefore
      /// never obtain anything, and an operator could not offer a path without forcing it on
      /// every sandbox.
      Uses : ResourceName list
      /// Resources selected IF the host offers them — `EnvironmentSpec.Wants` carries the
      /// posture: an optimisation the same file can name everywhere, warm where the
      /// operator made it so and silently absent where not. A misspelled want is
      /// therefore never caught, which is the cost of the posture; a thing the sandbox
      /// NEEDS goes in `uses`, where a missing name refuses.
      Wants : ResourceName list
      /// Files to write into the sandbox's own home before anything runs in it.
      ///
      /// The one thing here a repo may write freely that is not a name, and it is not an
      /// exception to the rule above — it is the rule's other side. A path, a hostname and
      /// an executable all reach out of the sandbox and so must be the operator's to offer.
      /// A file in a home this session made for this sandbox reaches nothing, so there is
      /// nothing for an operator to bound and no reason to make somebody else write it.
      ///
      /// `HomePath` is what keeps that true: it cannot be absolute and cannot contain `..`,
      /// so nothing declared here lands outside the home.
      Files : Map<HomePath, string>
      /// Credential NAMES to forward. Resolved for a human at spawn; a value never appears
      /// in a file, and could not: the type is a name.
      Forward : string list }

module SandboxDecl =

    let empty : SandboxDecl =
        { Container = None
          WorkingDirectory = None
          EnvironmentVariables = Map.empty
          Uses = []
          Wants = []
          Files = Map.empty
          Forward = [] }

    /// One declaration, written back as the file would have written it.
    ///
    /// Everything a set of declarations selects, each list deduplicated — the ONE assembly
    /// of "what these sandboxes ask for", however many declarations a repo carries. It was
    /// hand-collected at three sites, which is the missing-abstraction smell: a third
    /// selection posture beside `uses`/`wants` would have had to find every copy, and the
    /// copy it missed would have silently asked for less. Now a new posture changes this
    /// function and the type it returns, and the compiler walks to the consumers.
    let selectionOf (decls: SandboxDecl list) : ResourceName list * ResourceName list =
        decls |> List.collect (fun decl -> decl.Uses) |> List.distinct,
        decls |> List.collect (fun decl -> decl.Wants) |> List.distinct

    /// Exists so the command gate can carry a declaration (see `ConfigFile.parseSandbox`).
    /// The round trip through `parseSandbox` is what makes that safe: anything this writes,
    /// the file's own schema must be willing to read, so a gated call cannot smuggle a shape
    /// a `yession.yaml` would have been refused for.
    let encode (decl: SandboxDecl) : string =
        let container =
            decl.Container
            |> Option.map (fun container ->
                Encode.object
                    [ if container.Image.IsSome then
                        "image", Encode.string (ContainerImage.render container.Image.Value)
                      if container.Build.IsSome then
                        let build = container.Build.Value
                        "build",
                        Encode.object
                            [ "context", Encode.string build.ContextPath
                              if build.DockerfilePath.IsSome then
                                "dockerfile", Encode.string build.DockerfilePath.Value ]
                      if not (List.isEmpty container.Mounts) then
                        "volumes",
                        Encode.list (
                            container.Mounts
                            |> List.map (fun mount ->
                                Encode.object
                                    [ "source",
                                      Encode.string (
                                          match mount.Source with
                                          | SessionWorkspace -> "workspace"
                                          | NamedVolume name -> name
                                          // Written as it stands, and then REFUSED on the
                                          // way back in: the schema is the one place a host
                                          // path is rejected, so a second refusal here
                                          // would be a spare that could disagree with it.
                                          // The consequence is the intended one — a
                                          // host-path mount cannot cross the gate.
                                          | HostPath path -> path)
                                      "target", Encode.string mount.Target
                                      "mode",
                                      Encode.string (match mount.Mode with ReadOnly -> "ro" | ReadWrite -> "rw") ]))
                      if container.Command.IsSome then "cmd", Encode.string container.Command.Value ])
        let env =
            decl.EnvironmentVariables
            |> Map.toList
            |> List.map (fun (name, value) ->
                name,
                match value with
                | PlainValue plain -> Encode.string plain
                | SecretRef secret -> Encode.object [ "secret", Encode.string (SecretName.value secret) ])
        let strings (names: string list) = Encode.list (names |> List.map Encode.string)
        Encode.toString 0 (
            Encode.object
                [ if container.IsSome then "container", container.Value
                  if decl.WorkingDirectory.IsSome then "workdir", Encode.string decl.WorkingDirectory.Value
                  if not (Map.isEmpty decl.EnvironmentVariables) then "env", Encode.object env
                  if not (List.isEmpty decl.Uses) then "uses", strings (decl.Uses |> List.map ResourceName.value)
                  if not (List.isEmpty decl.Wants) then "wants", strings (decl.Wants |> List.map ResourceName.value)
                  if not (Map.isEmpty decl.Files) then
                    "files",
                    Encode.object (
                        decl.Files
                        |> Map.toList
                        |> List.map (fun (path, content) -> HomePath.value path, Encode.string content))
                  if not (List.isEmpty decl.Forward) then "forward", strings decl.Forward ])

    /// What a declaration ASKS the session for, given where this repo's checkout is.
    ///
    /// A rename rather than a translation, and that is the design: the file is a
    /// serialization of a vocabulary `start_work_sandbox` already spoke, so there is no
    /// second policy engine here and nothing this can express that a command could not.
    ///
    /// The checkout is the one thing a file cannot know — a path in it is relative to a
    /// directory the SESSION chose — so it is supplied, in BOTH its views (`CheckoutViews`):
    /// `workdir` resolves against the sandbox's own view, because it is where the sandbox
    /// starts; a `build:` context resolves against the host's, because the daemon client
    /// reads it from this filesystem before the container exists. Each is already
    /// guaranteed inside the checkout by the decoder, which is where a path a person can
    /// fix is refused; resolving is all that is left.
    ///
    /// `None` is a sandbox NOBODY'S repo declared: the session's own, which has no checkout
    /// for a relative path to be relative to. A `workdir` there is refused rather than
    /// resolved against something invented, and the refusal says which verb does move where
    /// a session sandbox's terminals start; a `build:` there is refused for the same reason.
    ///
    /// `Container = None` becomes `Confinement`, and that is not the file saying "confine
    /// me". It is the file saying nothing, and what nothing means is the BACKEND's answer:
    /// docker starts its defaults, srt and host confine. `Sandboxes.forBackend` is where the
    /// two authors meet.
    let toRequest (checkout: CheckoutViews option) (decl: SandboxDecl) : Result<SandboxRequest, string> =
        // Resolved rather than concatenated, and CLAMPED at the checkout. The decoder
        // already refuses a path that climbs out — that is the upstream fix, made where a
        // person can correct their file — and this is the downstream guard: a declaration
        // reaching here some other way still cannot name a directory above the checkout,
        // because there is no arithmetic here that could produce one.
        let under (checkout: string) (dir: string) =
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
        let workingDirectory =
            match decl.WorkingDirectory, checkout with
            | None, _ -> Ok None
            | Some dir, Some views -> Ok (Some (under views.InSandbox dir))
            | Some dir, None ->
                Error (
                    sprintf
                        "'%s' is relative to a checkout, and this sandbox is the session's own rather than a repo's                          — set_shell_profile moves where its terminals start"
                        dir)
        // The context leaves here HOST-absolute — the address the daemon client will
        // actually read — under the same clamp as `workdir`: nothing this arithmetic can
        // produce sits above the checkout, whatever reached it.
        let runtime =
            match decl.Container with
            | None -> Ok Confinement
            | Some container ->
                match container.Build, checkout with
                | None, _ -> Ok (Container container)
                | Some build, Some views ->
                    Ok (
                        Container
                            { container with
                                Build = Some { build with ContextPath = under views.OnHost build.ContextPath } })
                | Some build, None ->
                    Error (
                        sprintf
                            "a build context ('%s') is relative to a checkout, and this sandbox is the session's own rather than a repo's"
                            build.ContextPath)
        match workingDirectory, runtime with
        | Error e, _ -> Error e
        | _, Error e -> Error e
        | Ok workingDirectory, Ok runtime ->
            Ok
                { Spec =
                    { WorkingDirectory = workingDirectory
                      EnvironmentVariables = decl.EnvironmentVariables
                      Uses = decl.Uses
                      Wants = decl.Wants
                      Files = decl.Files
                      Runtime = runtime }
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
    let Version = 2

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

    /// Resource names, refused where they are written rather than at the sandbox that would
    /// have used them.
    let private resourceNames : Decoder<ResourceName list> =
        Decode.oneOf [ Decode.list Decode.string; Decode.string |> Decode.map List.singleton ]
        |> Decode.andThen (fun raws ->
            raws
            |> List.fold
                (fun acc raw ->
                    acc |> Result.bind (fun taken -> ResourceName.create raw |> Result.map (fun n -> taken @ [ n ])))
                (Ok [])
            |> function
                | Ok names -> Decode.succeed names
                | Error e -> Decode.fail e)

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

    /// `build:` — a context directory, and optionally a dockerfile within it.
    ///
    /// Both paths go through `inCheckout` for the same reason `workdir` does: the context
    /// is read on the machine running the session — the daemon client streams it from
    /// this filesystem — so a context a repo could point anywhere is arbitrary host-file
    /// read lifted into an image the repo's own sandbox then opens. This decoder was the
    /// one path-carrying field that read a bare string, three lines below the comment
    /// explaining why nothing may.
    let private build : Decoder<ContainerBuildSpec> =
        Decode.oneOf
            [ inCheckout "a build context" |> Decode.map (fun path -> { ContextPath = path; DockerfilePath = None })
              Decode.object (fun get ->
                  { ContextPath = get.Required.Field "context" (inCheckout "a build context")
                    DockerfilePath = get.Optional.Field "dockerfile" (inCheckout "a build dockerfile") }) ]

    /// What a file may mount, and it is deliberately only the sandbox's own workspace.
    ///
    /// `HostPath` exists and the SESSION uses it — that is how the repos directory reaches a
    /// container — but a source a repo could name is arbitrary read/write access to the
    /// machine running the session, which is the same authority `YESSION_BIN_*` carries and
    /// is refused for the same reason.
    ///
    /// `NamedVolume` exists and the OPERATOR grants it (a `volume:` resource, selected by
    /// `uses:`). A docker volume is host-global — the same name is the same volume in every
    /// session's containers, persistent across all of them — so a file that could name one
    /// could read and seed another session's state. This used to say "a named volume is the
    /// session's to create", which was the workspace volume's property wrongly generalised:
    /// nothing scoped an arbitrary name to a session.
    let private mountSource : Decoder<MountSource> =
        Decode.string
        |> Decode.andThen (fun raw ->
            match raw.Trim () with
            | "workspace" -> Decode.succeed SessionWorkspace
            | "" -> Decode.fail "a volume needs a source"
            | source ->
                Decode.fail (
                    sprintf
                        "'%s' is not a source a %s may name — say 'workspace'; a shared named volume is the operator's to offer as a `volume:` resource, selected with `uses:`"
                        source
                        FileName))

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

    let private sandboxKeys = [ "container"; "workdir"; "env"; "uses"; "wants"; "files"; "forward" ]

    /// `files:` — a path inside the sandbox's home to the content written there.
    ///
    /// The path is decoded through `HomePath`, so a file that would land outside the home
    /// is refused HERE, naming the path and what is wrong with it, rather than being
    /// normalised into something the writer did not ask for.
    let private seededFiles : Decoder<Map<HomePath, string>> =
        Decode.keyValuePairs Decode.string
        |> Decode.andThen (fun pairs ->
            let rec fold acc remaining =
                match remaining with
                | [] -> Decode.succeed (Map.ofList (List.rev acc))
                | (raw, content) :: rest ->
                    match HomePath.create raw with
                    | Ok path -> fold ((path, content) :: acc) rest
                    | Error reason -> Decode.fail reason
            fold [] pairs)

    let private sandbox : Decoder<SandboxDecl> =
        noUnknownKeys sandboxKeys
        |> Decode.andThen (fun () ->
            Decode.object (fun get ->
                { Container = get.Optional.Field "container" container
                  WorkingDirectory = get.Optional.Field "workdir" (inCheckout "workdir")
                  EnvironmentVariables =
                    get.Optional.Field "env" environment |> Option.defaultValue Map.empty
                  Uses = get.Optional.Field "uses" resourceNames |> Option.defaultValue []
                  Wants = get.Optional.Field "wants" resourceNames |> Option.defaultValue []
                  Files = get.Optional.Field "files" seededFiles |> Option.defaultValue Map.empty
                  Forward = get.Optional.Field "forward" stringList |> Option.defaultValue [] }))

    /// Sandbox names, refusing a clash INSIDE one file.
    ///
    /// This is the only place a clash can happen — across files the scope keeps them apart —
    /// and it is refused here, where the person who wrote both is standing and can pick
    /// another name, rather than resolved at read time by a precedence rule.
    let private sandboxes : Decoder<Map<SandboxName, SandboxDecl>> =
        // Decoded a field at a time rather than with `keyValuePairs`, for the PATH. That
        // combinator decodes each value without putting its key on the path, so every
        // refusal inside any sandbox came back as `$.sandboxes.workdir` — the same address
        // whichever sandbox wrote it. Reading `Decode.field name` per key costs one fold and
        // makes the refusal say `$.sandboxes.dev.workdir`, which is the difference between
        // fixing a file with two sandboxes in it and fixing one with ten.
        Decode.keys
        |> Decode.andThen (fun raws ->
            raws
            |> List.map (fun raw -> Decode.field raw sandbox |> Decode.map (fun decl -> raw, decl))
            |> List.fold (fun acc one -> Decode.map2 (fun xs x -> xs @ [ x ]) acc one) (Decode.succeed []))
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

    /// ONE sandbox block, on its own.
    ///
    /// What crosses the command gate (`start_work_sandbox`) is a declaration, because a
    /// declaration is what both callers have: the agent's names some credentials, a file's
    /// names everything. One shape, so the declarative route and the interactive one cannot
    /// diverge — which is the reason the gate is a capability rather than a detail of the
    /// MCP adapter.
    ///
    /// And the round trip is a real property, not a convenience: whatever crosses the gate
    /// is expressible in a `yession.yaml`, so every refusal the file's own schema makes —
    /// the reserved prefix, a host-path volume, a workdir outside the checkout — applies to
    /// a gated call for free rather than needing a second copy.
    let parseSandbox (json: string) : Result<SandboxDecl, string> =
        Decode.fromString sandbox json

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
