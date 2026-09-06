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

    /// A variable's value is the one part of a grant that is arbitrary text, so it is the
    /// one part that can carry whatever a reader is splitting a list of grants on. Quoted
    /// exactly when it would otherwise be ambiguous, so the ordinary `env:CI=1` stays bare
    /// and only the line that needs the ceremony pays for it.
    let private quotedValue (value: string) : string =
        let awkward c = System.Char.IsWhiteSpace c || c = '"' || c = '\\' || c = ';'
        if value |> Seq.exists awkward then
            "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
        else value

    /// One grant, in the notation. Every leaf is `kind:rest`, and the kind is the first
    /// thing a reader — or a model — matches on, before it knows anything else about the
    /// line.
    ///
    /// It was a sentence, and the sentence was doing two jobs at once: naming this grant,
    /// and explaining what its kind means, on every line, forever. What a kind MEANS is said
    /// once, in the legend beside the surface (`GrantNotation`), which is also where the
    /// words only ever true of ONE kind now live — a volume's sharing is what a person
    /// weighs, and it is a fact about volumes rather than about this volume. What a line has
    /// to carry is which grant this is, and a list of forty of them is read down its kinds.
    ///
    /// `path:` covers a file and a directory alike, deliberately. Telling them apart needs a
    /// stat, this module has no IO by rule, and a cache directory a tool has yet to create is
    /// an ordinary grant — while the kernel does not make the distinction either. Two
    /// prefixes would be a claim about the filesystem that nothing here can back.
    let describe (leaf: ResourceLeaf) : string =
        match leaf with
        | Mount mount ->
            let mode =
                match mount.Mode with
                | ResourceMountMode.Read -> "ro"
                | ResourceMountMode.Write -> "rw"
                | ResourceMountMode.Overlay -> "ovl"
            // The mode is written even when it is the operator's default, because a reader
            // works out what a grant allows from the line rather than from what is missing
            // off the end of it.
            if mount.At = mount.From then sprintf "path:%s:%s" mount.From mode
            else sprintf "path:%s>%s:%s" mount.From mount.At mode
        | Socket path -> sprintf "sock:%s" path
        | Endpoint host -> sprintf "net:%s" host
        | Variable (name, value) -> sprintf "env:%s=%s" name (quotedValue value)
        | Exec path -> sprintf "exec:%s" path
        | Volume (name, at) -> sprintf "vol:%s>%s" name at

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
    ///
    /// A PREFIX, and it was a trailing `(sensitive)`. A grant can be followed by what this
    /// host made of it (`~>`, below), and a mark at the end of that lands on the host's
    /// answer rather than on the grant it is about — the stranding this function exists to
    /// prevent, arriving by the one author who is not a person. It also sorts: `!` precedes
    /// every character a grant can start with, so any list of these opens with the grants
    /// somebody has to decide about.
    let mark (sensitivity: Sensitivity) (line: string) : string =
        match sensitivity with
        | Sensitivity.Sensitive -> "!" + line
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
    /// What a coarsening SAYS is what the sandbox ends up holding, in the same notation the
    /// grant was written in and widened by `*` — `sock:*` is what a person weighs, and
    /// "seccomp-bpf cannot read a path out of user-space memory" is not. A withholding says
    /// a reason instead, because there is no grant left to name.
    let private scoping (leaf: ResourceLeaf) : (HostDistinction * LeafRealisation) option =
        match leaf with
        | Socket _ ->
            Some (HostDistinction.SocketsByPath, LeafRealisation.Coarsened "sock:*")
        | Endpoint _ ->
            Some (HostDistinction.EgressByHost, LeafRealisation.Coarsened "net:*")
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
    /// `!sock:/run/docker.sock ~> sock:*`, never a line with the mark stranded past what the
    /// host had to say.
    ///
    /// ONE operator for both outcomes, reading "so the sandbox gets": what follows it is
    /// either the wider grant, or `nothing` and why. Two operators would be two things to
    /// learn for one question — what does this host actually give — and the answer to it is
    /// always whatever is to the right of the arrow.
    let private line (described: string) (outcome: LeafRealisation) : string =
        match outcome with
        | LeafRealisation.AsAsked -> described
        | LeafRealisation.Coarsened got -> sprintf "%s ~> %s" described got
        | LeafRealisation.Withheld because -> sprintf "%s ~> nothing: %s" described because

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
    /// is a third author and the only one that can widen: a person shown `sock:/run/docker.sock`
    /// on a host that cannot scope one has consented to a scope that does not exist there,
    /// and the sandbox holds every socket. So the lines a person says yes to
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

/// The legend for the notation the three renderers above write: what a kind is, said once,
/// wherever grants are shown.
///
/// A token can be short because the words a sentence used to spend on its kind are HERE
/// instead — including the ones that were only ever true of one kind and were being paid for
/// on every line of every other. The volume is what decides it: "persistent, and shared with
/// every sandbox on this host that holds it" is exactly what somebody weighing that grant
/// needs, and it is a fact about volumes, so it belongs beside the list and not inside the
/// forty lines that are not volumes.
///
/// Exhaustive by construction: an entry is a `match` on a leaf of that kind, so a seventh
/// primitive does not compile until somebody has written what its token means (warnings are
/// errors, repo-wide). A legend a kind can go quietly missing from is a decoder ring with a
/// hole in it — and the hole would be in the surface a person consents on.
module GrantNotation =

    /// One kind's shape and meaning. `[..]` is optional, `A|B` is a choice, and the capitals
    /// are what the operator wrote.
    let private kind (leaf: ResourceLeaf) : string * string =
        match leaf with
        | Mount _ ->
            "path:PATH[>AT]:ro|rw|ovl",
            "a file or directory, read-only, writable, or writable over a read-only copy, \
             with >AT the place the sandbox sees it"
        | Socket _ -> "sock:PATH", "a unix socket"
        | Endpoint _ -> "net:HOST", "that host may be reached over the network"
        | Volume _ ->
            "vol:NAME>AT",
            "a named volume at AT, which persists and is shared with every sandbox on this \
             host that holds it"
        | Variable _ ->
            "env:NAME=VALUE",
            "an environment variable, its value quoted where it carries anything that could \
             read as the end of the grant"
        | Exec _ -> "exec:PATH", "an executable, on PATH"

    /// One leaf per kind. What makes `kind` a total match over the vocabulary rather than a
    /// list somebody keeps in step with it.
    let private kinds : ResourceLeaf list =
        [ Mount { From = "/nix"; At = "/nix"; Mode = ResourceMountMode.Read }
          Socket "/run/docker.sock"
          Endpoint "registry.npmjs.org"
          Volume ("yession-nix", "/nix")
          Variable ("CI", "1")
          Exec "/usr/bin/git" ]

    /// The two marks, which are not kinds: one is what the operator said about a grant, the
    /// other what this host made of it. Last, because that is the order a line is read in.
    let private marks : (string * string) list =
        [ "!GRANT", "sensitive, which is the operator saying this one is worth being told about"
          "GRANT ~> OTHER",
          "this host cannot scope GRANT, so the sandbox gets OTHER instead, where * is any of \
           that kind, and \"~> nothing\" is not granted here followed by why" ]

    /// Every entry, in reading order.
    let legend : (string * string) list = (kinds |> List.map kind) @ marks

    /// The legend as prose, for a tool description — where a model reads it and there is no
    /// table to draw. The same list, so the two audiences cannot be told different things.
    ///
    /// One SENTENCE per entry, ended by its full stop rather than joined by a separator: a
    /// meaning is free text and the day one of them contains the separator is the day the
    /// legend reads as an entry nobody wrote.
    let sentence : string =
        legend
        |> List.map (fun (shape, meaning) -> sprintf "%s — %s." shape meaning)
        |> String.concat " "
        |> sprintf "Grants are written in one notation. %s"

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

    /// What a sandbox's two-posture selection comes to on THIS profile: everything it
    /// USES — a name missing from the profile refuses in `resolve`, and that refusal is
    /// the ceiling — plus whatever it WANTS that the profile declares. A want is an
    /// optimisation by declaration: selected where offered, silently absent where not,
    /// which is what lets a repo name a warm cache without breaking on every host that
    /// offers none. Written HERE because two callers resolve selections (the policy and
    /// the approval prompt), and a filter they each wrote is a filter that drifts.
    let selected (ResourceProfile declarations) (uses: ResourceName list) (wants: ResourceName list) : ResourceName list =
        uses @ (wants |> List.filter (fun name -> Map.containsKey name declarations))
        |> List.distinct

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

    /// The whole of one sandbox's grant: the operator's `defaults`, plus what the sandbox
    /// selected — resolved as ONE closure, so a conflict between any two of them refuses
    /// exactly as it always has. Beside the leaves comes the subset held through `wants:`
    /// ALONE, which the policy may drop where the HOST cannot realise it: a want's promise
    /// is "warm where the host makes it so and silently absent where not", `selected`
    /// honours the first half of "not" (a name nobody declared selects nothing), and this
    /// set is what lets the realisation honour the second (a declared name this host
    /// cannot express). A leaf also reachable through `uses:` or the defaults is not in
    /// the set — something NEEDS it, so withholding it still refuses.
    ///
    /// Written HERE rather than at the composition root because it computes: two resolves
    /// and a set difference are exactly the arithmetic a root cannot have tested cheaply.
    let grants
        (profile: ResourceProfile)
        (defaults: ResourceLeaf list)
        (uses: ResourceName list)
        (wants: ResourceName list)
        : Result<ResourceLeaf list * Set<ResourceLeaf>, string> =
        match selected profile uses wants with
        | [] -> Ok (defaults, Set.empty)
        | selection ->
            match resolve profile selection with
            | Error e -> Error e
            | Ok closure ->
                let all = (ResourceClosure.leaves closure |> Set.toList) @ defaults |> List.distinct
                match resolve profile (List.distinct uses) with
                | Error e -> Error e
                | Ok required ->
                    let held = Set.union (ResourceClosure.leaves required) (Set.ofList defaults)
                    Ok (all, Set.difference (Set.ofList all) held)
