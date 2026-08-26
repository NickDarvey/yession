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
                        return ResourceDecl.Leaf (leaf, sensitivity)
                    }
                else
                    gen {
                        let! shape = Gen.int32 (Range.linear 0 2)
                        if shape = 0 then
                            let! leaf = genLeafFor i
                            let! sensitivity = genSensitivity
                            return ResourceDecl.Leaf (leaf, sensitivity)
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
    | Some (_, ResourceDecl.Leaf (leaf, sensitivity)) -> [ leaf, sensitivity ]
    | Some (_, ResourceDecl.Composition children) -> children |> List.collect (expand declarations)

let private check (name: string) (body: unit -> Property<unit>) = testCase name <| fun () -> Property.check (body ())

/// Two resources that each load and cannot hold together: one target, two modes.
let private alternatives =
    let at = { From = "/nix"; At = "/nix"; Mode = ResourceMountMode.Read }
    [ ResourceName.create "nix-ro" |> expect, ResourceDecl.Leaf (Mount at, Sensitivity.Ordinary)
      ResourceName.create "nix-rw" |> expect,
        ResourceDecl.Leaf (Mount { at with Mode = ResourceMountMode.Write }, Sensitivity.Ordinary) ]

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
                    [ a, ResourceDecl.Leaf (Variable ("CC", "clang"), Sensitivity.Ordinary)
                      b, ResourceDecl.Leaf (Variable ("CC", "gcc"), Sensitivity.Ordinary)
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
                    [ danger, ResourceDecl.Leaf (Endpoint "anywhere.example.com", Sensitivity.Sensitive) ]
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
                    [ loud, ResourceDecl.Leaf (shared, Sensitivity.Sensitive)
                      quiet, ResourceDecl.Leaf (shared, Sensitivity.Ordinary)
                      both, ResourceDecl.Composition [ quiet; loud ] ]
                let profile = ResourceProfile.load declarations |> expect
                Expect.isTrue
                    (ResourceProfile.resolve profile [ both ] |> expect |> ResourceClosure.isSensitive)
                    "the louder mark wins, whichever order the routes are folded in"
            }

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
    ]
