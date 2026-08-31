namespace Yession.Domain.Sandboxes

open Yession.Domain

// The operator's vocabulary, and the algebra that flattens it.
//
// A sandbox is granted things. This module is about WHICH things, said in a way that has
// exactly two authors and no third: the operator NAMES what this host can offer, and a repo
// SELECTS from those names. Nothing here knows what npm is, or nix, or a toolchain — the
// five primitives below are the whole vocabulary, and a sixth ecosystem is a change to an
// operator's file rather than a release of this program.
//
// The point of separating it is that flattening a selection is where the interesting
// mistakes live — a cycle, a diamond, two grants that cannot both hold, a dangerous grant
// hidden inside a friendly name — and every one of them is decidable without touching a
// filesystem. So this module has no IO, no env, no paths it resolves, and no backend. It
// runs in the cheap tier on both runtimes, which is what lets the properties below be the
// thing that is believed rather than a later end-to-end run.
//
// What flattening PRODUCES is used three ways, and it matters that it is one set and not
// three: it is bound-checked against what the operator declared, shown to a human who is
// deciding whether to allow it, and handed to a backend to materialise. A prompt that shows
// one thing while a backend materialises another is the failure this shape exists to make
// unrepresentable.

/// Whether the operator marked a leaf as one a person should be told about.
///
/// A join semilattice with `Sensitive` on top, and that IS the masking defence: nothing in
/// this module lowers a value, so there is no operation an operator could reach for that
/// would quiet a leaf by wrapping it in something friendlier.
[<RequireQualifiedAccess>]
type Sensitivity =
    | Ordinary
    | Sensitive

/// How a mount is attached.
///
/// Deliberately NOT `MountMode`, which this namespace already owns (`Environment.fs`) for a
/// container's volumes. Those have no overlay, and a second type wearing the same case names
/// would silently re-point every unqualified `ReadOnly` in the namespace — the hazard
/// `NamespaceShadowing` exists to catch.
[<RequireQualifiedAccess>]
type ResourceMountMode =
    | Read
    | Write
    /// A writable layer over a read-only source. What a package cache actually needs: the
    /// host's copy stays untouched and the sandbox still gets somewhere to put things.
    | Overlay

/// `At` is total, though a surface syntax may let it default to `From`. The conflict rule
/// keys on the target, and a rule that has to first work out what the target WAS is a rule
/// with two answers.
type ResourceMount =
    { From : string
      At : string
      Mode : ResourceMountMode }

/// The six primitives, and there is no seventh.
type ResourceLeaf =
    | Mount of ResourceMount
    | Socket of path: string
    | Endpoint of host: string
    /// A named volume, mounted into the sandbox at `at`. The SHARING is the point and the
    /// hazard at once: a named volume outlives every sandbox and is the same volume in
    /// every container on this host that holds it, so one session's writes reach the next
    /// session's build. That is exactly the kind of grant only an operator may put on the
    /// table — a warm store on a machine whose sessions all answer to one person — and
    /// never something a repo's file may reach for on its own (`Config.fs` refuses it
    /// there). Only a container backend can hold one; everywhere else it is withheld.
    | Volume of name: string * at: string
    /// ONE variable. A declared `env` map is normalised to one leaf per entry before it
    /// reaches here, and that is load-bearing rather than tidy: a set of MAPS has no useful
    /// dedup — `{A=1}` and `{A=1,B=2}` are two elements that both grant `A` — and the
    /// conflict rule would need a special case for "same key, two values" instead of falling
    /// out of the one rule every other primitive uses.
    | Variable of name: string * value: string
    /// Something to put on PATH.
    | Exec of path: string

/// One resource as the operator declared it: either a thing, or a name for several things.
///
/// Sensitivity rides on the `Leaf` arm and nowhere else, so "a composite declared sensitive"
/// has no representation at all. That refusal is the compiler's — not a decoder case, and
/// not a test that could only prove it compiles.
[<RequireQualifiedAccess>]
type ResourceDecl =
    /// Primitives declared directly. A LIST, because the things that make one resource work
    /// are usually several — a cache is a mount and an endpoint and the variable that points
    /// a tool at it — and splitting those into three names an operator then has to compose
    /// would be three names for one idea, none of which means anything alone.
    ///
    /// Composition is for combining resources that DO mean something alone.
    | Leaf of leaves: ResourceLeaf list * sensitivity: Sensitivity
    | Composition of ResourceName list

/// What two leaves can collide ON. Same target, different leaf, is not a resource.
type ResourceTarget =
    | MountTarget of at: string
    | SocketTarget of path: string
    | EndpointTarget of host: string
    | VariableTarget of name: string
    | ExecTarget of path: string

module ResourceLeaf =

    /// What this leaf occupies. Two leaves conflict exactly when they share one of these and
    /// are not equal — which is the whole rule, for every primitive, with no per-kind
    /// special case.
    let target (leaf: ResourceLeaf) : ResourceTarget =
        match leaf with
        | Mount mount -> MountTarget mount.At
        | Socket path -> SocketTarget path
        | Endpoint host -> EndpointTarget host
        | Variable (name, _) -> VariableTarget name
        | Exec path -> ExecTarget path
        // A volume occupies the same axis a mount does — a container path — so a volume
        // and a mount at one target collide like two mounts would.
        | Volume (_, at) -> MountTarget at

    /// One line a person reads. The approval prompt is built from these, so it says what
    /// will be materialised rather than a summary of it.
    let describe (leaf: ResourceLeaf) : string =
        match leaf with
        | Mount mount ->
            let how =
                match mount.Mode with
                | ResourceMountMode.Read -> "read-only"
                | ResourceMountMode.Write -> "writable"
                | ResourceMountMode.Overlay -> "writable over a read-only copy"
            if mount.At = mount.From then sprintf "%s, %s" mount.From how
            else sprintf "%s at %s, %s" mount.From mount.At how
        | Socket path -> sprintf "the socket at %s" path
        | Endpoint host -> sprintf "reaches %s" host
        | Variable (name, value) -> sprintf "%s=%s" name value
        | Exec path -> sprintf "runs %s" path
        // The sharing is what a person weighing this needs to hear, not the mechanism.
        | Volume (name, at) ->
            sprintf "the volume '%s' at %s — persistent, and shared with every sandbox on this host that holds it" name at

    /// Every colliding pair in a set of leaves.
    ///
    /// REPORTS, and never refuses. The sentence belongs to whoever knows whose mistake it
    /// was — an operator writing one resource, or a repo selecting two — and that is why
    /// this returns pairs instead of a `Result`.
    let conflicts (leaves: Set<ResourceLeaf>) : (ResourceLeaf * ResourceLeaf) list =
        leaves
        |> Set.toList
        |> List.groupBy target
        |> List.collect (fun (_, sharing) ->
            match sharing with
            | [] | [ _ ] -> []
            // Sorted by `Set.toList` already, so the pair is the same on both runtimes and
            // the sentence built from it does not depend on iteration order.
            | first :: rest -> rest |> List.map (fun other -> first, other))

/// What a selection resolves to.
///
/// A map keyed by LEAF rather than a set of (leaf, sensitivity) pairs, and the difference is
/// a security property: the same leaf reached through a sensitive name and an ordinary one
/// must come out sensitive. Storing pairs would let one grant appear twice wearing two
/// marks, and a prompt rendering the set would show the quiet one.
type ResourceClosure =
    private
        { Grants : Map<ResourceLeaf, Sensitivity>
          Reached : Set<ResourceName> }

module ResourceClosure =

    let empty : ResourceClosure = { Grants = Map.empty; Reached = Set.empty }

    let leaves (closure: ResourceClosure) : Set<ResourceLeaf> =
        closure.Grants |> Map.toList |> List.map fst |> Set.ofList

    let names (closure: ResourceClosure) : Set<ResourceName> = closure.Reached

    let sensitiveLeaves (closure: ResourceClosure) : Set<ResourceLeaf> =
        closure.Grants
        |> Map.toList
        |> List.filter (fun (_, sensitivity) -> sensitivity = Sensitivity.Sensitive)
        |> List.map fst
        |> Set.ofList

    let isSensitive (closure: ResourceClosure) : bool =
        closure.Grants |> Map.exists (fun _ sensitivity -> sensitivity = Sensitivity.Sensitive)

    /// The join: leaves union, sensitivity max. The ONLY combiner in this module, which is
    /// what leaves masking no path to take — there is nowhere for a composition to express
    /// "and quieter than that".
    let union (left: ResourceClosure) (right: ResourceClosure) : ResourceClosure =
        let grants =
            right.Grants
            |> Map.fold
                (fun acc leaf sensitivity ->
                    match Map.tryFind leaf acc with
                    | Some Sensitivity.Sensitive -> acc
                    | _ when sensitivity = Sensitivity.Sensitive -> Map.add leaf sensitivity acc
                    | Some _ -> acc
                    | None -> Map.add leaf sensitivity acc)
                left.Grants
        { Grants = grants; Reached = Set.union left.Reached right.Reached }

    let private ofLeaves (name: ResourceName) (leaves: ResourceLeaf list) (sensitivity: Sensitivity) : ResourceClosure =
        { Grants = leaves |> List.map (fun leaf -> leaf, sensitivity) |> Map.ofList
          Reached = Set.singleton name }

    let internal single = ofLeaves

    /// How a sensitive grant is marked, wherever one is shown to a person.
    ///
    /// One function rather than the two `sprintf`s it replaced, because a surface that
    /// renders a leaf without the mark is a surface that under-states — and the second
    /// renderer (what a host makes of the same leaf, below) is where that would have
    /// happened. The mark belongs to the leaf, not to the sentence around it.
    let mark (sensitivity: Sensitivity) (line: string) : string =
        match sensitivity with
        | Sensitivity.Sensitive -> sprintf "%s (sensitive)" line
        | Sensitivity.Ordinary -> line

    /// What a selection NAMES, one line per leaf, sensitive ones marked.
    ///
    /// Here rather than in a view, because "a leaf that materialises is a leaf that was
    /// shown" is the invariant this module exists for, and a view cannot be reached by the
    /// cheap tier. Sorted, so two runs of one closure read identically.
    ///
    /// The naming is the whole of what this says. A host is the third author of a grant and
    /// the only one that can WIDEN it, so a person consenting reads
    /// `RealisedClosure.describeOn` instead — this is the right answer for an operator
    /// reading their own vocabulary and the wrong one to consent against.
    let describe (closure: ResourceClosure) : string list =
        closure.Grants
        |> Map.toList
        |> List.map (fun (leaf, sensitivity) -> mark sensitivity (ResourceLeaf.describe leaf))
        |> List.sort

/// A distinction a host is able to MAKE about a grant.
///
/// Not a list of platform quirks, which is what a per-backend special case would become. A
/// grant is scoped by some property — which socket, which host, which layering — and a
/// backend either can express that property or cannot. When it cannot, it does not refuse the
/// grant: it gives the coarsest thing it has, and the difference is what somebody has to be
/// told.
///
/// Measured, and the reason this exists: srt scopes a unix socket by path on macOS
/// (`network-outbound` on the path) and cannot on Linux, where the filter is seccomp-bpf and
/// cannot read a socket path out of user-space memory. So one `Socket` leaf is a grant on one
/// host and, on the other, either every socket or none. Nothing said so.
[<RequireQualifiedAccess>]
type HostDistinction =
    /// Which unix socket, rather than merely whether any.
    | SocketsByPath
    /// Which host may be reached, rather than merely whether anything may.
    | EgressByHost
    /// A writable layer over a read-only source, rather than one or the other.
    | OverlayMounts
    /// A named volume as a thing the backend HAS, not a bound it enforces. The one
    /// distinction on this list that is a provision rather than a confinement — which is
    /// why the unconfining host backend, whose "everything is allowed" answers every
    /// confinement question, still does not claim it: allowing everything does not
    /// conjure a volume.
    | NamedVolumes

/// What this host can express. The THIRD author of a grant, after the operator who named it
/// and the repo that selected it, and the only one that is not a person.
///
/// Private for the reason `ResourceProfile` is: a caller that could build one could claim a
/// distinction the backend underneath does not have, and the whole point is that this is the
/// backend's own statement about itself.
type HostLimits = private HostLimits of Set<HostDistinction>

module HostLimits =

    /// A host that can express everything. What the algebra's properties compare against.
    /// NOT what the unconfining host backend claims any more: allowing everything answers
    /// every confinement distinction, but `NamedVolumes` is a provision, and the host
    /// backend has no volumes to provide — its limits are named in `Sandboxes.limitsFor`.
    let unlimited : HostLimits =
        HostLimits (
            Set.ofList
                [ HostDistinction.SocketsByPath
                  HostDistinction.EgressByHost
                  HostDistinction.OverlayMounts
                  HostDistinction.NamedVolumes ])

    let of' (distinctions: HostDistinction list) : HostLimits = HostLimits (Set.ofList distinctions)

    let can (distinction: HostDistinction) (HostLimits distinctions) : bool =
        Set.contains distinction distinctions

/// What a host actually does with a leaf it was asked for.
///
/// Three outcomes and no fourth, because there are only three things a host can do with a
/// grant it cannot express exactly: give it, give something wider, or give nothing.
[<RequireQualifiedAccess>]
type LeafRealisation =
    /// The host can express this exactly.
    | AsAsked
    /// The host cannot scope it this finely, so the sandbox holds something WIDER than the
    /// resource named. A widening, and the direction that matters most: nobody is stopped,
    /// and what they hold is more than what they were shown.
    | Coarsened of got: string
    /// The host cannot provide it at all. Narrower than asked, so nothing is over-granted —
    /// but a tool that needed it will fail, and whoever reads this is the one who can say
    /// whether that is fatal.
    | Withheld of because: string

/// What one host makes of a set of leaves: what is actually held, and every place that
/// differs from what was asked.
///
/// LEAVES and not a closure, which applying this is what settled. Sensitivity is not
/// something a host can change, whoever resolved the selection still holds it, and the copy
/// that used to live here was a second answer to "what was asked for" that nothing kept equal
/// to the first. A policy builder has only leaves in hand anyway.
type RealisedClosure =
    private { Outcomes : Map<ResourceLeaf, LeafRealisation> }

module RealisedClosure =

    /// What scopes a leaf, and what a host that cannot make that distinction gives instead.
    ///
    /// ONE table, returning both halves together, because they are two halves of one fact and
    /// a version of this with two tables had a hole in exactly the shape this layer exists to
    /// remove: a leaf could name a distinction in one and be missing from the other, and the
    /// answer was silently `AsAsked` — an exact grant claimed on a host that cannot express
    /// it. The regression that should have caught it stayed green, because there was nothing
    /// to be red about. Paired, that state has no representation.
    ///
    /// `None` is a leaf that needs no distinction: a path to read, a variable, a binary on
    /// PATH are the same grant on every backend, and this is the whole platform knowledge in
    /// the module.
    ///
    /// The words are what the SANDBOX ends up holding, not the mechanism that could not hold
    /// it — "any unix socket on this host" is what a person weighs, and "seccomp-bpf cannot
    /// read a path out of user-space memory" is not.
    let private scoping (leaf: ResourceLeaf) : (HostDistinction * LeafRealisation) option =
        match leaf with
        | Socket _ ->
            Some (HostDistinction.SocketsByPath, LeafRealisation.Coarsened "any unix socket on this host")
        | Endpoint _ ->
            Some (HostDistinction.EgressByHost, LeafRealisation.Coarsened "anywhere on the network")
        | Mount mount ->
            match mount.Mode with
            | ResourceMountMode.Overlay ->
                Some (
                    HostDistinction.OverlayMounts,
                    LeafRealisation.Withheld
                        "no backend on this host has a union mount, so declare it read or write rather than let it silently become one")
            | ResourceMountMode.Read
            | ResourceMountMode.Write -> None
        | Volume _ ->
            Some (
                HostDistinction.NamedVolumes,
                LeafRealisation.Withheld
                    "only a container backend has named volumes — grant this where the sandbox is a container, not here")
        | Variable _
        | Exec _ -> None

    /// Put a selection through a host. The third narrowing, after the operator's vocabulary
    /// and the repo's selection — except that it is the one narrowing that can also WIDEN,
    /// which is exactly why it has to be said out loud rather than folded into the closure.
    let of' (limits: HostLimits) (asked: Set<ResourceLeaf>) : RealisedClosure =
        { Outcomes =
            asked
            |> Set.toList
            |> List.map (fun leaf ->
                match scoping leaf with
                | None -> leaf, LeafRealisation.AsAsked
                | Some (distinction, _) when HostLimits.can distinction limits -> leaf, LeafRealisation.AsAsked
                | Some (_, otherwise) -> leaf, otherwise)
            |> Map.ofList }

    /// The leaves this host will actually put in a policy: everything it did not withhold.
    ///
    /// A coarsened leaf is still HERE, because the sandbox does still get it — wider than
    /// asked, and a materialiser that dropped it would give the sandbox less than the person
    /// approved rather than more.
    let held (realised: RealisedClosure) : Set<ResourceLeaf> =
        realised.Outcomes
        |> Map.toList
        |> List.filter (fun (_, outcome) ->
            match outcome with
            | LeafRealisation.Withheld _ -> false
            | LeafRealisation.AsAsked
            | LeafRealisation.Coarsened _ -> true)
        |> List.map fst
        |> Set.ofList

    /// Every leaf whose grant is not what was asked for, with what happened to it. Empty when
    /// this host can express the whole selection, which is the case worth having: a session on
    /// a host that can do everything says nothing, rather than saying "no degradations".
    let differences (realised: RealisedClosure) : (ResourceLeaf * LeafRealisation) list =
        realised.Outcomes
        |> Map.toList
        |> List.filter (fun (_, outcome) -> outcome <> LeafRealisation.AsAsked)
        |> List.sortBy (fun (leaf, _) -> ResourceLeaf.describe leaf)

    /// One line for one leaf, saying what the sandbox ACTUALLY ends up holding.
    ///
    /// Private, and the two surfaces below are the whole reason: the differences alone and
    /// the whole selection are two questions, and a coarsened socket that read one way in a
    /// consent prompt and another in the timeline would be two answers to the one that
    /// matters.
    /// Takes the leaf ALREADY described, so that a mark put on the grant stays on the grant:
    /// "the socket at /run/docker.sock (sensitive) — this host cannot scope that ...", never a
    /// sentence with the mark stranded at the end of it.
    let private line (described: string) (outcome: LeafRealisation) : string =
        match outcome with
        | LeafRealisation.AsAsked -> described
        | LeafRealisation.Coarsened got ->
            sprintf "%s — this host cannot scope that, so the sandbox gets %s" described got
        | LeafRealisation.Withheld because ->
            sprintf "%s — not granted: %s" described because

    /// One difference, said. Public because a policy carries its differences as pairs — the
    /// backend has to act on them, not read them — and whoever reports one is holding the
    /// pair rather than the closure it came out of.
    let describeDifference (leaf: ResourceLeaf, outcome: LeafRealisation) : string =
        line (ResourceLeaf.describe leaf) outcome

    /// One line per difference, for a person. The same rendering wherever it is shown, for the
    /// reason `ResourceClosure.describe` is here rather than in a view.
    let describeDifferences (realised: RealisedClosure) : string list =
        differences realised |> List.map describeDifference

    /// What a human approves, one line per leaf, as THIS host will actually grant it.
    ///
    /// `ResourceClosure.describe` renders what the selection NAMED, which is what the
    /// operator wrote and the repo picked. Consent is a different question, because the host
    /// is a third author and the only one that can widen: a person shown `the socket at
    /// /run/docker.sock` on a host that cannot scope one has consented to a scope that does
    /// not exist there, and the sandbox holds every socket. So the lines a person says yes to
    /// are these, and what they bind to changes when the host does.
    ///
    /// The mark rides through unchanged. A coarsened sensitive leaf is MORE worth marking
    /// than it was, never less, and losing it here would be the masking this module refuses
    /// everywhere else — arriving by the one author who is not a person.
    let describeOn (limits: HostLimits) (closure: ResourceClosure) : string list =
        let realised = of' limits (ResourceClosure.leaves closure)
        let sensitive = ResourceClosure.sensitiveLeaves closure
        realised.Outcomes
        |> Map.toList
        |> List.map (fun (leaf, outcome) ->
            let named =
                ResourceClosure.mark
                    (if Set.contains leaf sensitive then Sensitivity.Sensitive else Sensitivity.Ordinary)
                    (ResourceLeaf.describe leaf)
            line named outcome)
        |> List.sort

/// An operator's whole vocabulary, AFTER validation.
///
/// Private, so `load` is the only way to hold one. A caller that could build the map itself
/// would be a caller that could skip the cycle, dangling-name and self-conflict checks — and
/// an invariant that holds only because everybody remembered to call something first is a
/// convention, not an invariant.
type ResourceProfile =
    private ResourceProfile of Map<ResourceName, ResourceDecl>

module ResourceProfile =

    let private render (name: ResourceName) = ResourceName.value name

    let private listed (names: ResourceName seq) =
        names |> Seq.map render |> Seq.toList |> List.sort |> String.concat ", "

    /// A sentence about two leaves that cannot both hold, in the words of whichever author
    /// can fix it.
    let private conflictSentence (whose: string) (left: ResourceLeaf) (right: ResourceLeaf) =
        match left, right with
        | Mount a, Mount b when a.At = b.At ->
            sprintf
                "%s mounts %s two ways at once — %s and %s — so one target has one mode"
                whose
                a.At
                (ResourceLeaf.describe (Mount a))
                (ResourceLeaf.describe (Mount b))
        | Variable (name, a), Variable (_, b) ->
            sprintf "%s sets %s to '%s' and to '%s' at once, and a variable has one value" whose name a b
        | a, b -> sprintf "%s asks for %s and %s at once, and they cannot both hold" whose (ResourceLeaf.describe a) (ResourceLeaf.describe b)

    /// Depth-first walk yielding either every name reachable from `start`, or the path of a
    /// cycle. The path, not the fact: "there is a cycle" is a refusal nobody can act on by
    /// reading it.
    let private walk (declarations: Map<ResourceName, ResourceDecl>) (start: ResourceName) : Result<ResourceName list, string> =
        let rec go (path: ResourceName list) (seen: Set<ResourceName>) (name: ResourceName) =
            if List.contains name path then
                let cycle = (path |> List.rev |> List.skipWhile (fun step -> step <> name)) @ [ name ]
                Error (
                    sprintf
                        "the resource '%s' contains itself: %s — a resource may not compose a resource that composes it"
                        (render name)
                        (cycle |> List.map render |> String.concat " -> "))
            elif Set.contains name seen then Ok seen
            else
                match Map.tryFind name declarations with
                | None ->
                    Error (
                        sprintf
                            "the resource '%s' composes '%s', which this profile does not declare — a composition may only name resources declared beside it (declared: %s)"
                            (path |> List.tryHead |> Option.map render |> Option.defaultValue (render name))
                            (render name)
                            (listed (declarations |> Map.toList |> List.map fst)))
                | Some (ResourceDecl.Leaf _) -> Ok (Set.add name seen)
                | Some (ResourceDecl.Composition children) ->
                    children
                    |> List.fold
                        (fun acc child -> acc |> Result.bind (fun seen -> go (name :: path) seen child))
                        (Ok seen)
                    |> Result.map (Set.add name)
        go [] Set.empty start |> Result.map Set.toList

    /// The closure of one name, on a profile already known to be acyclic and closed.
    let rec private closureOf (declarations: Map<ResourceName, ResourceDecl>) (name: ResourceName) : ResourceClosure =
        match Map.tryFind name declarations with
        // Unreachable after `load`: every name was walked, so a missing one would already
        // have been refused. Total rather than an exception, because a partial function here
        // is a crash in a fold every client runs.
        | None -> ResourceClosure.empty
        | Some (ResourceDecl.Leaf (leaves, sensitivity)) -> ResourceClosure.single name leaves sensitivity
        | Some (ResourceDecl.Composition children) ->
            children
            |> List.fold
                (fun acc child -> ResourceClosure.union acc (closureOf declarations child))
                { ResourceClosure.empty with Reached = Set.singleton name }

    /// Validate a vocabulary.
    ///
    /// Three refusals, and each is a mistake the operator can fix while they are standing in
    /// the file that has it: a name declared twice, a composition naming something nobody
    /// declared, and a resource that contains itself. Then a fourth — a resource whose OWN
    /// closure holds two grants that cannot both hold, which is the operator contradicting
    /// themselves and must go red when their file is read rather than months later when some
    /// repo first selects it.
    ///
    /// What is deliberately NOT refused here: two resources that each load and cannot be
    /// selected together. An operator may declare `nix-ro` and `nix-rw` as alternatives, and
    /// a vocabulary that could not hold both would be a vocabulary that cannot express a
    /// choice. That conflict is a REPO's, and it appears in `resolve`.
    let load (declarations: (ResourceName * ResourceDecl) list) : Result<ResourceProfile, string> =
        let duplicates =
            declarations
            |> List.countBy fst
            |> List.filter (fun (_, n) -> n > 1)
            |> List.map fst
        if not (List.isEmpty duplicates) then
            Error (sprintf "these resources are declared twice: %s — a name has one meaning in a profile" (listed duplicates))
        else

        let byName = Map.ofList declarations
        let walked =
            declarations
            |> List.fold
                (fun acc (name, _) -> acc |> Result.bind (fun () -> walk byName name |> Result.map ignore))
                (Ok ())

        walked
        |> Result.bind (fun () ->
            declarations
            |> List.fold
                (fun acc (name, _) ->
                    acc
                    |> Result.bind (fun () ->
                        match ResourceLeaf.conflicts (ResourceClosure.leaves (closureOf byName name)) with
                        | [] -> Ok ()
                        | (left, right) :: _ ->
                            Error (conflictSentence (sprintf "the resource '%s'" (render name)) left right)))
                (Ok ()))
        |> Result.map (fun () -> ResourceProfile byName)

    let empty : ResourceProfile = ResourceProfile Map.empty

    let declared (ResourceProfile declarations) : Set<ResourceName> =
        declarations |> Map.toList |> List.map fst |> Set.ofList

    /// Every leaf this vocabulary can grant — the bound every selection is under, by
    /// construction rather than by comparison.
    ///
    /// A leaf SET and deliberately not a closure: the whole vocabulary may hold alternatives
    /// that cannot be materialised together, so it is a ceiling that is not itself a policy
    /// and is never realised.
    let ceiling (ResourceProfile declarations as profile) : Set<ResourceLeaf> =
        declared profile
        |> Set.toList
        |> List.map (fun name -> ResourceClosure.leaves (closureOf declarations name))
        |> List.fold Set.union Set.empty

    /// A repo's selection, flattened.
    ///
    /// Total after `load` but for the two things load could not see: a name the repo
    /// invented, and a pair of names that each hold and cannot hold together.
    let resolve (ResourceProfile declarations) (selection: ResourceName list) : Result<ResourceClosure, string> =
        let unknown = selection |> List.filter (fun name -> not (Map.containsKey name declarations))
        match unknown with
        | missing :: _ ->
            Error (
                sprintf
                    "this asks for the resource '%s', which this session's operator does not declare — the resources are: %s"
                    (render missing)
                    (listed (declarations |> Map.toList |> List.map fst)))
        | [] ->
            let closure =
                selection
                |> List.fold (fun acc name -> ResourceClosure.union acc (closureOf declarations name)) ResourceClosure.empty
            match ResourceLeaf.conflicts (ResourceClosure.leaves closure) with
            | [] -> Ok closure
            | (left, right) :: _ -> Error (conflictSentence "this selection" left right)
