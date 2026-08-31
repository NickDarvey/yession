namespace Yession.Domain.Sandboxes

open Thoth.Json
open Yession.Domain

// What an operator writes, and how it becomes a vocabulary.
//
// The counterpart of `Config.fs`: that file is what a REPO writes and it may only select;
// this one is what a HOST writes and it is the only place a path or a hostname is named. The
// division is the whole point — a repo cannot learn where this machine keeps its package
// caches, and an operator does not have to know which repos want them.
//
// Parser-free, like `Config.fs` and for the same reason: this decodes an already-parsed JSON
// tree, so it runs in the cheap tier on both runtimes and the YAML front end is somebody
// else's problem.
//
// The shape has one rule worth stating before the code: an OBJECT is a leaf and an ARRAY is
// a composition. One namespace, so a repo selecting `nix` cannot tell which it got — and an
// operator can therefore split a leaf into three, or gather three into one name, without any
// repo's file changing. That is what makes the vocabulary genuinely theirs.

/// A whole profile: the vocabulary, and what every sandbox on this host gets without asking.
///
/// `Default` is the operator granting something to everything, which is a DIFFERENT act from
/// declaring that it exists — and keeping the two apart is the correction this model is built
/// around. `YESSION_SESSION_READ` was both at once, so an operator could not offer a path
/// without forcing it on every sandbox, and a repo asking for one could never obtain it.
///
/// Here `resources` is the menu and `default` is one selection from it. A name declared and
/// not defaulted is available and not granted — the state the old variable could not express.
type ProfileFile =
    { Resources : ResourceProfile
      Default : ResourceName list }

module ProfileFile =

    let empty : ProfileFile = { Resources = ResourceProfile.empty; Default = [] }

module OperatorProfile =

    /// The name the file has, wherever an operator keeps it.
    [<Literal>]
    let FileName = "resources.yaml"

    /// The only version this build speaks. A file from the future says so rather than losing
    /// half its meaning to a decoder that skips what it cannot read.
    [<Literal>]
    let Version = 1

    let private fileKeys = [ "version"; "resources"; "default" ]
    let private leafKeys = [ "mount"; "socket"; "endpoint"; "env"; "exec"; "volume"; "sensitive" ]
    let private mountKeys = [ "from"; "at"; "mode" ]
    let private volumeKeys = [ "name"; "at" ]

    let private failIf (condition: bool) (message: string) (decoder: Decoder<'a>) : Decoder<'a> =
        if condition then Decode.fail message else decoder

    /// Refuse any key the schema does not define. A typo that decodes to "nothing was asked
    /// for" reads as configuration and behaves as none — the failure this file, like
    /// `Config.fs`, exists to avoid.
    let private noUnknownKeys (known: string list) : Decoder<unit> =
        Decode.keys
        |> Decode.andThen (fun keys ->
            match keys |> List.filter (fun key -> not (List.contains key known)) with
            | [] -> Decode.succeed ()
            | unknown ->
                Decode.fail (
                    sprintf
                        "unknown %s: %s (known: %s)"
                        (if List.length unknown = 1 then "key" else "keys")
                        (String.concat ", " (List.sort unknown))
                        (String.concat ", " known)))

    /// One string or several. Every list-shaped key takes both, so an operator never has to
    /// remember which — and a single-item list and a bare string mean the same thing.
    let private stringList : Decoder<string list> =
        Decode.oneOf [ Decode.list Decode.string; Decode.string |> Decode.map List.singleton ]

    let private mountMode : Decoder<ResourceMountMode> =
        Decode.string
        |> Decode.andThen (function
            | "read" -> Decode.succeed ResourceMountMode.Read
            | "write" -> Decode.succeed ResourceMountMode.Write
            | "overlay" -> Decode.succeed ResourceMountMode.Overlay
            | other ->
                Decode.fail (
                    sprintf "'%s' is not a mount mode — a mount is read, write or overlay" other))

    /// `from` is required; `at` defaults to it, because the common case is a path that means
    /// the same on both sides and writing it twice is how the two drift.
    let private mount : Decoder<ResourceMount> =
        noUnknownKeys mountKeys
        |> Decode.andThen (fun () ->
            Decode.object (fun get ->
                let from = get.Required.Field "from" Decode.string
                { From = from
                  At = get.Optional.Field "at" Decode.string |> Option.defaultValue from
                  Mode = get.Optional.Field "mode" mountMode |> Option.defaultValue ResourceMountMode.Read }))

    /// Both halves required: a volume with no `at` is a thing with nowhere to be, and the
    /// operator is the one author who knows where it belongs.
    let private volume : Decoder<ResourceLeaf> =
        noUnknownKeys volumeKeys
        |> Decode.andThen (fun () ->
            Decode.object (fun get ->
                Volume (get.Required.Field "name" Decode.string, get.Required.Field "at" Decode.string)))

    /// A leaf declares primitives directly, and may declare several: the things that make one
    /// resource work are usually more than one — a cache is a mount and an endpoint and the
    /// variable pointing a tool at it — and none of the three means anything alone.
    let private leaf : Decoder<ResourceDecl> =
        noUnknownKeys leafKeys
        |> Decode.andThen (fun () ->
            Decode.object (fun get ->
                let mounts = get.Optional.Field "mount" (Decode.oneOf [ Decode.list mount; mount |> Decode.map List.singleton ])
                let sockets = get.Optional.Field "socket" stringList |> Option.defaultValue []
                let endpoints = get.Optional.Field "endpoint" stringList |> Option.defaultValue []
                let execs = get.Optional.Field "exec" stringList |> Option.defaultValue []
                let volumes =
                    get.Optional.Field "volume" (Decode.oneOf [ Decode.list volume; volume |> Decode.map List.singleton ])
                    |> Option.defaultValue []
                // One `Variable` leaf per entry, which is what makes a variable dedup and
                // conflict like every other primitive instead of needing a rule of its own.
                let variables =
                    get.Optional.Field "env" (Decode.keyValuePairs Decode.string)
                    |> Option.defaultValue []
                let sensitivity =
                    if get.Optional.Field "sensitive" Decode.bool |> Option.defaultValue false then
                        Sensitivity.Sensitive
                    else Sensitivity.Ordinary
                let leaves =
                    (mounts |> Option.defaultValue [] |> List.map Mount)
                    @ (sockets |> List.map Socket)
                    @ (endpoints |> List.map Endpoint)
                    @ (variables |> List.map Variable)
                    @ (execs |> List.map Exec)
                    @ volumes
                leaves, sensitivity))
        |> Decode.andThen (fun (leaves, sensitivity) ->
            // A resource that grants nothing is a name that reads as configuration and is
            // none — the same failure an unknown key would be, arriving by a different route.
            failIf
                (List.isEmpty leaves)
                "this resource grants nothing — name at least one of mount, socket, endpoint, env, exec or volume"
                (Decode.succeed (ResourceDecl.Leaf (leaves, sensitivity))))

    /// An ARRAY is a composition. Sensitivity is deliberately not a key here: a composite is
    /// sensitive exactly when something it reaches is, computed rather than declared, so that
    /// wrapping a dangerous leaf in a friendly name cannot quiet it.
    let private composition : Decoder<ResourceDecl> =
        Decode.list Decode.string
        |> Decode.andThen (fun names ->
            names
            |> List.fold
                (fun acc raw ->
                    acc
                    |> Result.bind (fun taken ->
                        ResourceName.create raw |> Result.map (fun name -> taken @ [ name ])))
                (Ok [])
            |> function
                | Ok names -> Decode.succeed (ResourceDecl.Composition names)
                | Error e -> Decode.fail e)

    let private resource : Decoder<ResourceDecl> = Decode.oneOf [ composition; leaf ]

    /// Decoded a field at a time rather than with `keyValuePairs`, for the PATH: that
    /// combinator decodes each value without putting its key on it, so every refusal inside
    /// any resource would come back at the same address whichever resource wrote it.
    let private resources : Decoder<(ResourceName * ResourceDecl) list> =
        Decode.keys
        |> Decode.andThen (fun raws ->
            raws
            |> List.map (fun raw ->
                Decode.field raw resource
                |> Decode.andThen (fun decl ->
                    match ResourceName.create raw with
                    | Ok name -> Decode.succeed (name, decl)
                    | Error e -> Decode.fail e))
            |> List.fold (fun acc one -> Decode.map2 (fun taken x -> taken @ [ x ]) acc one) (Decode.succeed []))

    let private names : Decoder<ResourceName list> =
        stringList
        |> Decode.andThen (fun raws ->
            raws
            |> List.fold
                (fun acc raw ->
                    acc |> Result.bind (fun taken -> ResourceName.create raw |> Result.map (fun n -> taken @ [ n ])))
                (Ok [])
            |> function
                | Ok names -> Decode.succeed names
                | Error e -> Decode.fail e)

    let decoder : Decoder<ProfileFile> =
        noUnknownKeys fileKeys
        |> Decode.andThen (fun () ->
            Decode.field "version" Decode.int
            |> Decode.andThen (fun version ->
                failIf
                    (version <> Version)
                    (sprintf "this build speaks %s version %d, not %d" FileName Version version)
                    (Decode.map2
                        (fun declared selection -> declared, selection)
                        (Decode.field "resources" resources)
                        (Decode.optional "default" names |> Decode.map (Option.defaultValue [])))))
        |> Decode.andThen (fun (declared, selection) ->
            // The algebra's own refusals — a cycle, a dangling name, a name declared twice, a
            // resource that contradicts itself — reached through `load` and NOT re-checked
            // here. A decoder with its own copy of those rules is the redundant spare that
            // rots: two mechanisms for one requirement, free to disagree.
            match ResourceProfile.load declared with
            | Error e -> Decode.fail e
            | Ok profile ->
                // The default must RESOLVE, and it is checked here rather than at the first
                // sandbox that would have used it. An operator granting something to every
                // sandbox on the host should learn it does not hold while they are looking at
                // the file, not when somebody else's session refuses to start.
                match ResourceProfile.resolve profile selection with
                | Error e -> Decode.fail (sprintf "the default selection cannot be granted: %s" e)
                | Ok _ -> Decode.succeed { Resources = profile; Default = selection })

    let parse (json: string) : Result<ProfileFile, string> = Decode.fromString decoder json
