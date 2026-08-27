module Yession.Tests.Resources

// The resources algebra, under Hedgehog.
//
// Flattening a selection is where the interesting mistakes live — a cycle, a diamond, two
// grants that cannot both hold, a dangerous grant hidden inside a friendly name — and every
// one of them is decidable without touching a filesystem. So this suite needs no capability
// and runs on both runtimes, which is the point: what is believed about the algebra is
// believed here, not inferred from a later end-to-end run.
//
// The generators CONSTRUCT the shapes they need rather than filtering for them. The vendored
// Hedgehog has no `Gen.filter`, no shrinking and no `Gen.choice`, so "draw graphs until one
// is acyclic" is not available — and it would be the wrong instinct anyway, because a
// generator that rejects most of its draws tests the rejection more than the subject.
//
// One thing deliberately has no test: that a COMPOSITE cannot be declared sensitive. It has
// no representation in `ResourceDecl`, so a case asserting it could only prove that the test
// file compiles.

open Fable.Pyxpecto
open Hedgehog
open Yession.Domain
open Yession.Domain.Sandboxes
open Yession.Domain.Tools
open Yession.Host

let private expect = function Ok v -> v | Error e -> failwithf "%A" e

/// A small fixed pool, small ON PURPOSE: collisions are what produce diamonds, and a diamond
/// is what the set-semantics property is about. Eight random 8-character names would generate
/// a forest and prove nothing.
let private pool =
    [| 0 .. 7 |] |> Array.map (fun i -> ResourceName.create (sprintf "r%d" i) |> expect)

let private ghost = ResourceName.create "ghost" |> expect

/// A leaf whose target belongs to resource `i` alone.
///
/// Unique by construction, so a graph built from these never conflicts with itself — which
/// is what lets the load and set-semantics properties be about cycles and diamonds rather
/// than about collisions. Conflicts get their own cases, with the colliding pair written
/// down where the reader can see it.
let private genLeafFor (i: int) : Gen<ResourceLeaf> =
    gen {
        let! kind = Gen.int32 (Range.linear 0 4)
        let! mode = Gen.int32 (Range.linear 0 2)
        return
            match kind with
            | 0 ->
                Mount
                    { From = sprintf "/from/%d" i
                      At = sprintf "/at/%d" i
                      Mode =
                        match mode with
                        | 0 -> ResourceMountMode.Read
                        | 1 -> ResourceMountMode.Write
                        | _ -> ResourceMountMode.Overlay }
            | 1 -> Socket (sprintf "/run/%d.sock" i)
            | 2 -> Endpoint (sprintf "h%d.example.com" i)
            | 3 -> Variable (sprintf "V%d" i, sprintf "value-%d" i)
            | _ -> Exec (sprintf "/bin/tool%d" i)
    }

/// A host that can express some arbitrary subset of the distinctions.
let private genLimits : Gen<HostLimits> =
    gen {
        let! sockets = Gen.int32 (Range.linear 0 1)
        let! egress = Gen.int32 (Range.linear 0 1)
        let! overlay = Gen.int32 (Range.linear 0 1)
        return
            HostLimits.of' (
                [ if sockets = 1 then HostDistinction.SocketsByPath
                  if egress = 1 then HostDistinction.EgressByHost
                  if overlay = 1 then HostDistinction.OverlayMounts ])
    }

let private genSensitivity : Gen<Sensitivity> =
    Gen.int32 (Range.linear 0 3)
    |> Gen.map (fun n -> if n = 0 then Sensitivity.Sensitive else Sensitivity.Ordinary)

/// A DAG, by construction: `r i` is either a leaf or a composition drawn ONLY from
/// { r0 … r(i-1) }, so the index IS topological rank and a back edge is not expressible.
/// `r0` is forced to a leaf because it has no predecessors. Children are drawn with
/// repetition and independently per sibling, which makes "a name reached by two paths" the
/// common case rather than a corner.
let rec private buildDag (size: int) (i: int) (acc: (ResourceName * ResourceDecl) list) =
    if i >= size then Gen.constant (List.rev acc)
    else
        gen {
            let! decl =
                if i = 0 then
                    gen {
                        let! leaf = genLeafFor 0
                        let! sensitivity = genSensitivity
                        return ResourceDecl.Leaf ([ leaf ], sensitivity)
                    }
                else
                    gen {
                        let! shape = Gen.int32 (Range.linear 0 2)
                        if shape = 0 then
                            let! leaf = genLeafFor i
                            let! sensitivity = genSensitivity
                            return ResourceDecl.Leaf ([ leaf ], sensitivity)
                        else
                            let! picks = Gen.list (Range.linear 1 3) (Gen.int32 (Range.linear 0 (i - 1)))
                            return ResourceDecl.Composition (picks |> List.map (fun p -> pool.[p]))
                    }
            return! buildDag size (i + 1) ((pool.[i], decl) :: acc)
        }

let private genDag : Gen<(ResourceName * ResourceDecl) list> =
    gen {
        let! size = Gen.int32 (Range.linear 2 8)
        return! buildDag size 0 []
    }

/// A cycle, also by construction — never by adding a back edge to a DAG and hoping. An
/// injected edge from `rj` to `ri` only closes a cycle if a path from `ri` to `rj` happens to
/// exist, so the closing edges are written explicitly: pick k distinct indices and rewrite
/// each to compose the next, with the last composing the first. k = 1 gives the self-loop
/// `a = [a]`, which is exactly the degenerate case a naive visited-set flattener passes.
let private genCyclic : Gen<(ResourceName * ResourceDecl) list * ResourceName list> =
    gen {
        let! declarations = genDag
        let size = List.length declarations
        let! k = Gen.int32 (Range.linear 1 (min 4 size))
        let! start = Gen.int32 (Range.linear 0 (size - k))
        let ring = [ start .. start + k - 1 ] |> List.map (fun i -> pool.[i])
        let rewritten =
            declarations
            |> List.map (fun (name, decl) ->
                match ring |> List.tryFindIndex (fun step -> step = name) with
                | Some position -> name, ResourceDecl.Composition [ ring.[(position + 1) % List.length ring] ]
                | None -> name, decl)
        return rewritten, ring
    }

let private genDangling : Gen<(ResourceName * ResourceDecl) list> =
    gen {
        let! declarations = genDag
        let! victim = Gen.int32 (Range.linear 0 (List.length declarations - 1))
        return
            declarations
            |> List.mapi (fun i (name, decl) ->
                if i = victim then name, ResourceDecl.Composition [ ghost ] else name, decl)
    }

let private genSelection (declarations: (ResourceName * ResourceDecl) list) : Gen<ResourceName list> =
    Gen.list (Range.linear 0 4) (Gen.int32 (Range.linear 0 (List.length declarations - 1)))
    |> Gen.map (List.map (fun i -> fst declarations.[i]))

/// The oracle: a second, dumber implementation that never calls the module under test.
/// Naive expansion into a LIST with duplicates left in — the duplicates are the proof that a
/// diamond was there for dedup to remove.
let rec private expand (declarations: (ResourceName * ResourceDecl) list) (name: ResourceName) =
    match declarations |> List.tryFind (fun (declared, _) -> declared = name) with
    | None -> []
    | Some (_, ResourceDecl.Leaf (leaves, sensitivity)) -> leaves |> List.map (fun leaf -> leaf, sensitivity)
    | Some (_, ResourceDecl.Composition children) -> children |> List.collect (expand declarations)

let private check (name: string) (body: unit -> Property<unit>) = testCase name <| fun () -> Property.check (body ())

/// Two resources that each load and cannot hold together: one target, two modes.
let private alternatives =
    let at = { From = "/nix"; At = "/nix"; Mode = ResourceMountMode.Read }
    [ ResourceName.create "nix-ro" |> expect, ResourceDecl.Leaf ([ Mount at ], Sensitivity.Ordinary)
      ResourceName.create "nix-rw" |> expect,
        ResourceDecl.Leaf ([ Mount { at with Mode = ResourceMountMode.Write } ], Sensitivity.Ordinary) ]

let tests =
    testList "the resources algebra" [

        // --- loading a vocabulary -------------------------------------------------------

        check "a vocabulary built acyclically loads" <| fun () ->
            property {
                let! declarations = genDag
                match ResourceProfile.load declarations with
                | Ok _ -> ()
                | Error e -> failwithf "expected a profile, got: %s" e
            }

        // The red here also means "flattening did not terminate": a traversal that diverges
        // never returns to be asserted on.
        check "a vocabulary whose resources contain themselves is refused, naming one on the cycle" <| fun () ->
            property {
                let! declarations, ring = genCyclic
                match ResourceProfile.load declarations with
                | Ok _ -> failwith "expected a refusal"
                | Error e ->
                    let named = ring |> List.exists (fun name -> e.Contains (ResourceName.value name))
                    Expect.isTrue named (sprintf "the refusal names a resource on the cycle, said: %s" e)
            }

        check "a composition naming a resource nobody declares is refused, naming it" <| fun () ->
            property {
                let! declarations = genDangling
                match ResourceProfile.load declarations with
                | Ok _ -> failwith "expected a refusal"
                | Error e -> Expect.isTrue (e.Contains "ghost") (sprintf "the refusal names the missing resource, said: %s" e)
            }

        check "a name declared twice is refused rather than silently losing one declaration" <| fun () ->
            property {
                let! declarations = genDag
                let (name, decl) = List.head declarations
                match ResourceProfile.load (declarations @ [ name, decl ]) with
                | Ok _ -> failwith "expected a refusal"
                | Error e ->
                    Expect.isTrue
                        (e.Contains (ResourceName.value name))
                        (sprintf "the refusal names the duplicated resource, said: %s" e)
            }

        // --- flattening ------------------------------------------------------------------

        check "the closure is the naive expansion of the selection, deduplicated" <| fun () ->
            property {
                let! declarations = genDag
                let! selection = genSelection declarations
                let profile = ResourceProfile.load declarations |> expect
                let closure = ResourceProfile.resolve profile selection |> expect
                let oracle = selection |> List.collect (expand declarations) |> List.map fst |> Set.ofList
                Expect.equal (ResourceClosure.leaves closure) oracle "every leaf the naive walk reaches, once each"
            }

        check "a resource selected twice resolves to exactly the closure it does once" <| fun () ->
            property {
                let! declarations = genDag
                let! selection = genSelection declarations
                let profile = ResourceProfile.load declarations |> expect
                Expect.equal
                    (ResourceProfile.resolve profile (selection @ selection) |> expect |> ResourceClosure.leaves)
                    (ResourceProfile.resolve profile selection |> expect |> ResourceClosure.leaves)
                    "asking twice is asking once"
            }

        check "the order of a selection does not change what it grants" <| fun () ->
            property {
                let! declarations = genDag
                let! selection = genSelection declarations
                let profile = ResourceProfile.load declarations |> expect
                Expect.equal
                    (ResourceProfile.resolve profile (List.rev selection) |> expect |> ResourceClosure.leaves)
                    (ResourceProfile.resolve profile selection |> expect |> ResourceClosure.leaves)
                    "a set, not a sequence"
            }

        // --- attenuation -----------------------------------------------------------------

        check "the closure of any selection is inside the whole vocabulary" <| fun () ->
            property {
                let! declarations = genDag
                let! selection = genSelection declarations
                let profile = ResourceProfile.load declarations |> expect
                let closure = ResourceProfile.resolve profile selection |> expect
                Expect.isTrue
                    (Set.isSubset (ResourceClosure.leaves closure) (ResourceProfile.ceiling profile))
                    "nothing a selection reaches is outside what the operator declared"
            }

        check "asking for a resource the operator did not declare is refused, listing what is" <| fun () ->
            property {
                let! declarations = genDag
                let! selection = genSelection declarations
                let profile = ResourceProfile.load declarations |> expect
                match ResourceProfile.resolve profile (selection @ [ ghost ]) with
                | Ok _ -> failwith "expected a refusal"
                | Error e ->
                    Expect.isTrue (e.Contains "ghost") (sprintf "the refusal names what was asked for, said: %s" e)
                    Expect.isTrue
                        (e.Contains (ResourceName.value (fst (List.head declarations))))
                        (sprintf "and lists what there is instead, said: %s" e)
            }

        // --- monotonicity ----------------------------------------------------------------

        check "adding a resource to a selection never removes a leaf" <| fun () ->
            property {
                let! declarations = genDag
                let! selection = genSelection declarations
                let! extra = Gen.int32 (Range.linear 0 (List.length declarations - 1))
                let profile = ResourceProfile.load declarations |> expect
                let before = ResourceProfile.resolve profile selection |> expect
                let after = ResourceProfile.resolve profile (selection @ [ fst declarations.[extra] ]) |> expect
                Expect.isTrue
                    (Set.isSubset (ResourceClosure.leaves before) (ResourceClosure.leaves after))
                    "a longer ask grants at least as much"
            }

        // Without this, the case above could be vacuous — it only asserts anything when both
        // resolves succeed, and a `resolve` that refused everything would satisfy it.
        check "adding a resource to a refused selection never rescues it" <| fun () ->
            property {
                let! declarations = genDag
                let! selection = genSelection declarations
                let! extra = Gen.int32 (Range.linear 0 (List.length declarations - 1))
                let profile = ResourceProfile.load declarations |> expect
                match ResourceProfile.resolve profile (ghost :: selection) with
                | Ok _ -> failwith "a selection naming an undeclared resource must not resolve"
                | Error _ ->
                    match ResourceProfile.resolve profile (ghost :: selection @ [ fst declarations.[extra] ]) with
                    | Ok _ -> failwith "adding a declared resource must not rescue an undeclared one"
                    | Error _ -> ()
            }

        // --- conflict ---------------------------------------------------------------------

        check "no closure ever holds two grants on one target" <| fun () ->
            property {
                let! declarations = genDag
                let! selection = genSelection declarations
                let profile = ResourceProfile.load declarations |> expect
                let closure = ResourceProfile.resolve profile selection |> expect
                Expect.equal
                    (ResourceLeaf.conflicts (ResourceClosure.leaves closure))
                    []
                    "a resolved closure is materialisable"
            }

        // The operator contradicting themselves, refused where they are standing.
        check "a resource whose own closure mounts one target two ways is refused at load" <| fun () ->
            property {
                let! declarations = genDag
                let both = ResourceName.create "both" |> expect
                let clashing = alternatives @ [ both, ResourceDecl.Composition (alternatives |> List.map fst) ]
                match ResourceProfile.load (declarations @ clashing) with
                | Ok _ -> failwith "expected a refusal"
                | Error e ->
                    Expect.isTrue (e.Contains "both") (sprintf "the refusal names the resource, said: %s" e)
                    Expect.isTrue (e.Contains "/nix") (sprintf "and the target they collide on, said: %s" e)
            }

        check "a resource whose own closure sets one variable two ways is refused at load" <| fun () ->
            property {
                let! declarations = genDag
                let a = ResourceName.create "cc-clang" |> expect
                let b = ResourceName.create "cc-gcc" |> expect
                let both = ResourceName.create "cc-both" |> expect
                let clashing =
                    [ a, ResourceDecl.Leaf ([ Variable ("CC", "clang") ], Sensitivity.Ordinary)
                      b, ResourceDecl.Leaf ([ Variable ("CC", "gcc") ], Sensitivity.Ordinary)
                      both, ResourceDecl.Composition [ a; b ] ]
                match ResourceProfile.load (declarations @ clashing) with
                | Ok _ -> failwith "expected a refusal"
                | Error e -> Expect.isTrue (e.Contains "CC") (sprintf "the refusal names the variable, said: %s" e)
            }

        // The REPO's mistake, and it cannot exist until a selection does — which is why the
        // operator's vocabulary is allowed to hold both.
        check "two resources that cannot hold together load, and are refused at selection" <| fun () ->
            property {
                let! declarations = genDag
                let profile = ResourceProfile.load (declarations @ alternatives) |> expect
                match ResourceProfile.resolve profile (alternatives |> List.map fst) with
                | Ok _ -> failwith "expected a refusal"
                | Error e -> Expect.isTrue (e.Contains "/nix") (sprintf "the refusal names the target, said: %s" e)
            }

        // --- sensitivity ------------------------------------------------------------------

        check "a closure is sensitive exactly when some leaf in it is" <| fun () ->
            property {
                let! declarations = genDag
                let! selection = genSelection declarations
                let profile = ResourceProfile.load declarations |> expect
                let closure = ResourceProfile.resolve profile selection |> expect
                let oracle =
                    selection
                    |> List.collect (expand declarations)
                    |> List.exists (fun (_, sensitivity) -> sensitivity = Sensitivity.Sensitive)
                Expect.equal (ResourceClosure.isSensitive closure) oracle "the naive walk and the closure agree"
            }

        // The masking attack, stated directly: wrap a sensitive leaf in friendlier and
        // friendlier names and it must stay sensitive, or an approval prompt goes quiet
        // about the thing it exists to be loud about.
        check "wrapping a sensitive resource in compositions never makes it ordinary" <| fun () ->
            property {
                let! depth = Gen.int32 (Range.linear 1 5)
                let danger = ResourceName.create "open-web" |> expect
                let wrappers = [ 0 .. depth - 1 ] |> List.map (fun i -> ResourceName.create (sprintf "friendly%d" i) |> expect)
                let declarations =
                    [ danger, ResourceDecl.Leaf ([ Endpoint "anywhere.example.com" ], Sensitivity.Sensitive) ]
                    @ (wrappers
                       |> List.mapi (fun i name ->
                            let inner = if i = 0 then danger else wrappers.[i - 1]
                            name, ResourceDecl.Composition [ inner ]))
                let profile = ResourceProfile.load declarations |> expect
                let closure = ResourceProfile.resolve profile [ List.last wrappers ] |> expect
                Expect.isTrue (ResourceClosure.isSensitive closure) "however deep it is buried, it is still sensitive"
            }

        // The join, which the wrapping case does not reach: one leaf, two routes.
        check "a leaf reached both sensitively and ordinarily comes out sensitive" <| fun () ->
            property {
                let shared = Endpoint "shared.example.com"
                let loud = ResourceName.create "loud" |> expect
                let quiet = ResourceName.create "quiet" |> expect
                let both = ResourceName.create "both" |> expect
                let declarations =
                    [ loud, ResourceDecl.Leaf ([ shared ], Sensitivity.Sensitive)
                      quiet, ResourceDecl.Leaf ([ shared ], Sensitivity.Ordinary)
                      both, ResourceDecl.Composition [ quiet; loud ] ]
                let profile = ResourceProfile.load declarations |> expect
                Expect.isTrue
                    (ResourceProfile.resolve profile [ both ] |> expect |> ResourceClosure.isSensitive)
                    "the louder mark wins, whichever order the routes are folded in"
            }

        // --- the syntax an operator writes ---------------------------------------------------
        //
        // From JSON literals, like `Config.fs`'s own cases: what a YAML front end does with a
        // document is its business, and pinning the SCHEMA does not need one.

        testCase "an object is a leaf and an array is a composition" <| fun () ->
            let profile =
                OperatorProfile.parse """
                    { "version": 1,
                      "resources": { "store": { "mount": { "from": "/nix" } },
                                     "daemon": { "socket": "/nix/var/nix/daemon-socket" },
                                     "nix": [ "store", "daemon" ] } }"""
                |> expect
            let nix = ResourceName.create "nix" |> expect
            let closure = ResourceProfile.resolve profile.Resources [ nix ] |> expect
            Expect.equal
                (ResourceClosure.leaves closure)
                (Set.ofList
                    [ Mount { From = "/nix"; At = "/nix"; Mode = ResourceMountMode.Read }
                      Socket "/nix/var/nix/daemon-socket" ])
                "the composition flattens to both leaves, and neither is a name any more"

        // A cache is a mount and an endpoint and the variable pointing a tool at it. Three
        // names an operator then composes would be three names for one idea, none of which
        // means anything alone.
        testCase "one leaf may declare several primitives" <| fun () ->
            let profile =
                OperatorProfile.parse """
                    { "version": 1,
                      "resources": { "npm": { "mount": { "from": "/h/.npm", "mode": "overlay" },
                                              "endpoint": "registry.npmjs.org",
                                              "env": { "npm_config_cache": "/h/.npm" } } } }"""
                |> expect
            let closure = ResourceProfile.resolve profile.Resources [ ResourceName.create "npm" |> expect ] |> expect
            Expect.equal (Set.count (ResourceClosure.leaves closure)) 3 "a mount, an endpoint and a variable"

        testCase "at defaults to from, so a path that means the same on both sides is written once" <| fun () ->
            let profile =
                OperatorProfile.parse """{ "version": 1, "resources": { "s": { "mount": { "from": "/nix" } } } }"""
                |> expect
            match ResourceProfile.resolve profile.Resources [ ResourceName.create "s" |> expect ] |> expect |> ResourceClosure.leaves |> Set.toList with
            | [ Mount mount ] -> Expect.equal mount.At "/nix" "the target is the source unless it is named"
            | other -> failwithf "expected one mount, got %A" other

        testCase "a resource that grants nothing is refused, not decoded as an empty one" <| fun () ->
            // A name that reads as configuration and is none — the same failure an unknown
            // key would be, arriving by another route.
            Expect.isError
                (OperatorProfile.parse """{ "version": 1, "resources": { "hollow": { } } }""")
                "an empty leaf is not a resource"

        testCase "an unknown key inside a resource is refused, not skipped" <| fun () ->
            // The resource grants something REAL as well, so the only thing that can refuse
            // this is the unknown key. The first draft named only the typo, which left the
            // "grants nothing" refusal catching it and this case green with the key check
            // deleted — a test passing for a reason that is not its name.
            Expect.isError
                (OperatorProfile.parse """
                    { "version": 1, "resources": { "s": { "mount": { "from": "/nix" }, "mounts": "/x" } } }""")
                "a misspelled key fails the file even when the rest of the resource is fine"

        testCase "a mount mode this build does not know is refused, naming the three there are" <| fun () ->
            match OperatorProfile.parse """{ "version": 1, "resources": { "s": { "mount": { "from": "/n", "mode": "rw" } } } }""" with
            | Ok _ -> failwith "expected a refusal"
            | Error e -> Expect.isTrue (e.Contains "overlay") (sprintf "the refusal lists the modes, said: %s" e)

        testCase "a version this build does not speak is refused" <| fun () ->
            Expect.isError (OperatorProfile.parse """{ "version": 2, "resources": {} }""") "not read as a lossy version 1"

        // The algebra's refusals arrive through `load` rather than being re-implemented here.
        // A decoder with its own copy of them is a second mechanism free to disagree with the
        // first — so this asserts they REACH the operator, not that they exist twice.
        testCase "a cycle in a profile is refused by the algebra, through the decoder" <| fun () ->
            match OperatorProfile.parse """{ "version": 1, "resources": { "a": [ "b" ], "b": [ "a" ] } }""" with
            | Ok _ -> failwith "expected a refusal"
            | Error e -> Expect.isTrue (e.Contains "->") (sprintf "and the refusal carries the path, said: %s" e)

        testCase "a composition naming an undeclared resource is refused, through the decoder" <| fun () ->
            Expect.isError
                (OperatorProfile.parse """{ "version": 1, "resources": { "a": [ "nope" ] } }""")
                "the algebra's dangling-name refusal reaches the operator"

        // Sensitivity is declarable on a leaf and NOT on a composition — there is no key for
        // it there — so the only way a composite becomes sensitive is by reaching one.
        testCase "a composition cannot declare itself ordinary around a sensitive leaf" <| fun () ->
            let profile =
                OperatorProfile.parse """
                    { "version": 1,
                      "resources": { "danger": { "endpoint": "anywhere", "sensitive": true },
                                     "friendly": [ "danger" ] } }"""
                |> expect
            Expect.isTrue
                (ResourceProfile.resolve profile.Resources [ ResourceName.create "friendly" |> expect ]
                 |> expect
                 |> ResourceClosure.isSensitive)
                "the friendly name is sensitive because of what it reaches"

        // The refusal that REPLACED the ceiling. There is no list-versus-list comparison any
        // more: a repo cannot exceed what an operator offered because it can only name what
        // an operator offered, and a name it was not offered does not resolve. That is a
        // stronger arrangement than comparing two lists and hoping the comparison is right.
        testCase "a repo selecting something this host does not declare is refused, and told what there is" <| fun () ->
            let profile =
                OperatorProfile.parse """
                    { "version": 1, "resources": { "nix": { "mount": { "from": "/nix" } } } }"""
                |> expect
            match ResourceProfile.resolve profile.Resources [ ResourceName.create "npm" |> expect ] with
            | Ok _ -> failwith "expected a refusal"
            | Error e ->
                Expect.isTrue (e.Contains "npm") (sprintf "the refusal names what was asked for, said: %s" e)
                Expect.isTrue (e.Contains "nix") (sprintf "and lists what this host has, said: %s" e)

        // --- what the operator is shown ------------------------------------------------------

        // The row an operator reads is the CLOSURE, not the line they wrote. A composite whose
        // whole content is one other name has to say what that name comes to, or the surface
        // answers a question nobody asked.
        testCase "a resource's row says what selecting it finally grants" <| fun () ->
            let profile =
                OperatorProfile.parse """
                    { "version": 1,
                      "resources": { "store": { "mount": { "from": "/nix" } },
                                     "daemon": { "socket": "/nix/var/nix/daemon-socket" },
                                     "nix": [ "store", "daemon" ] } }"""
                |> expect
            let cellOf name key =
                OperatorResources.rows profile
                |> List.find (fun row -> row |> List.contains ("resource", CellText name))
                |> List.pick (fun (column, cell) -> if column = key then Some cell else None)
            match cellOf "nix" "grants" with
            | CellText grants ->
                Expect.isTrue (grants.Contains "/nix,") (sprintf "the mount it reaches, said: %s" grants)
                Expect.isTrue (grants.Contains "daemon-socket") (sprintf "and the socket, said: %s" grants)
            | other -> failwithf "expected text, got %A" other

        // Sensitivity is the reason the surface exists at all: an operator has to be able to
        // see which of their own names are the loud ones, INCLUDING the ones that are only
        // loud because of what they reach.
        testCase "a resource is marked sensitive when something it reaches is" <| fun () ->
            let profile =
                OperatorProfile.parse """
                    { "version": 1,
                      "resources": { "danger": { "endpoint": "anywhere", "sensitive": true },
                                     "friendly": [ "danger" ],
                                     "quiet": { "exec": "/bin/true" } } }"""
                |> expect
            let sensitive name =
                OperatorResources.rows profile
                |> List.find (fun row -> row |> List.contains ("resource", CellText name))
                |> List.pick (fun (column, cell) -> if column = "sensitive" then Some cell else None)
            Expect.equal (sensitive "friendly") (CellText "yes") "a friendly name over a loud leaf is loud"
            Expect.equal (sensitive "quiet") CellAbsent "and one that reaches nothing loud is not"

        // --- what a person is shown --------------------------------------------------------

        // A leaf that materialises and was never shown is the fault this module exists to
        // prevent. Counts leaves and marks; never the wording, which is a design and moves.
        check "every leaf in a closure is described, and every sensitive one is marked" <| fun () ->
            property {
                let! declarations = genDag
                let! selection = genSelection declarations
                let profile = ResourceProfile.load declarations |> expect
                let closure = ResourceProfile.resolve profile selection |> expect
                let lines = ResourceClosure.describe closure
                Expect.equal
                    (List.length lines)
                    (Set.count (ResourceClosure.leaves closure))
                    "one line per leaf, and no leaf without one"
                Expect.equal
                    (lines |> List.filter (fun line -> line.Contains "(sensitive)") |> List.length)
                    (Set.count (ResourceClosure.sensitiveLeaves closure))
                    "and every sensitive leaf carries the mark"
            }

        // --- the third author: what this host can actually express -------------------------

        // The rule that makes this an algebra rather than a table of platform quirks: a leaf
        // is realised exactly when the host can make the distinction its KIND requires. A
        // host that can make all of them changes nothing at all — which is what says the
        // machinery costs nothing where it is not needed.
        check "a host that can express everything grants exactly what was asked" <| fun () ->
            property {
                let! declarations = genDag
                let! selection = genSelection declarations
                let profile = ResourceProfile.load declarations |> expect
                let closure = ResourceProfile.resolve profile selection |> expect
                let realised = RealisedClosure.of' HostLimits.unlimited closure
                Expect.equal (RealisedClosure.differences realised) [] "nothing differs"
                Expect.equal (RealisedClosure.held realised) (ResourceClosure.leaves closure)
                    "and everything asked for is held"
            }

        // The leaves that need no distinction are the ones every backend expresses the same
        // way, and they must never appear as a difference however poor the host. Without this
        // the rule could quietly grow into "anything might degrade", which is a table again.
        check "a mount, a variable and an executable are the same grant on every host" <| fun () ->
            property {
                let! declarations = genDag
                let! selection = genSelection declarations
                let! limits = genLimits
                let profile = ResourceProfile.load declarations |> expect
                let closure = ResourceProfile.resolve profile selection |> expect
                let realised = RealisedClosure.of' limits closure
                let differing = RealisedClosure.differences realised |> List.map fst
                for leaf in differing do
                    match leaf with
                    | Variable _
                    | Exec _ -> failwithf "%A cannot depend on the host" leaf
                    | Mount mount ->
                        Expect.equal mount.Mode ResourceMountMode.Overlay
                            "only an overlay mount can, and only because no backend has a union mount"
                    | Socket _
                    | Endpoint _ -> ()
            }

        // Every difference is one of the two directions, and which one is not a detail: a
        // coarsening is still HELD — wider than asked — and a withholding is not held at all.
        // A materialiser that dropped a coarsened leaf would give a sandbox less than the
        // person approved rather than more, which is the opposite mistake and just as wrong.
        check "a coarsened grant is still held, a withheld one is not" <| fun () ->
            property {
                let! declarations = genDag
                let! selection = genSelection declarations
                let! limits = genLimits
                let profile = ResourceProfile.load declarations |> expect
                let closure = ResourceProfile.resolve profile selection |> expect
                let realised = RealisedClosure.of' limits closure
                let held = RealisedClosure.held realised
                for leaf, outcome in RealisedClosure.differences realised do
                    match outcome with
                    | LeafRealisation.Coarsened _ ->
                        Expect.isTrue (Set.contains leaf held) (sprintf "%A is wider, not absent" leaf)
                    | LeafRealisation.Withheld _ ->
                        Expect.isFalse (Set.contains leaf held) (sprintf "%A is not granted" leaf)
                    | LeafRealisation.AsAsked -> failwith "an unchanged leaf is not a difference"
            }

        // Held is always a subset of asked. The coarsening is a widening of what ONE leaf
        // means, never a new leaf — so nothing a person was not shown can appear here, which
        // is the invariant the whole module exists for, still true after a third author.
        check "a host never adds a leaf nobody asked for" <| fun () ->
            property {
                let! declarations = genDag
                let! selection = genSelection declarations
                let! limits = genLimits
                let profile = ResourceProfile.load declarations |> expect
                let closure = ResourceProfile.resolve profile selection |> expect
                let realised = RealisedClosure.of' limits closure
                Expect.isTrue
                    (Set.isSubset (RealisedClosure.held realised) (ResourceClosure.leaves closure))
                    "held is within asked"
            }

        // Realising is a projection: putting an answer through the same host again changes
        // nothing. Without this a caller could not safely re-derive the actual grant, which is
        // what a page re-render and a sandbox restart both do.
        check "realising the same selection twice says the same thing" <| fun () ->
            property {
                let! declarations = genDag
                let! selection = genSelection declarations
                let! limits = genLimits
                let profile = ResourceProfile.load declarations |> expect
                let closure = ResourceProfile.resolve profile selection |> expect
                let once = RealisedClosure.of' limits closure
                let twice = RealisedClosure.of' limits (ResourceProfile.resolve profile selection |> expect)
                Expect.equal (RealisedClosure.differences twice) (RealisedClosure.differences once) "same differences"
                Expect.equal (RealisedClosure.held twice) (RealisedClosure.held once) "same held"
            }

        // Every difference is SAID, because a difference nobody is told about is the fault
        // this whole layer exists to remove — the Linux socket grant that resolved, prompted,
        // started, and then did not work.
        check "every difference gets a line a person can read" <| fun () ->
            property {
                let! declarations = genDag
                let! selection = genSelection declarations
                let! limits = genLimits
                let profile = ResourceProfile.load declarations |> expect
                let closure = ResourceProfile.resolve profile selection |> expect
                let realised = RealisedClosure.of' limits closure
                Expect.equal
                    (List.length (RealisedClosure.describeDifferences realised))
                    (List.length (RealisedClosure.differences realised))
                    "one line each"
                for line in RealisedClosure.describeDifferences realised do
                    Expect.isFalse (line = "") "and none of them empty"
            }
    ]
