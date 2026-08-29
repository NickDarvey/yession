module Yession.Tests.SeededFiles

// `files:` — the one thing a repo writes into a sandbox that is not a name — under
// Hedgehog.
//
// Not part of the resources algebra, deliberately: every leaf there reaches OUT of the
// sandbox and so is the operator's to offer first, while a seeded file lands in a home
// this session made for that sandbox and reaches nothing. There is no ceiling for it to
// exceed, which is why the repo may write it — and that argument holds only while the
// path cannot LEAVE the home. So that is what is generated against.
//
// The interesting mistake is a spelling nobody thought of, which is exactly what examples
// are bad at. Segments are drawn from a pool built to collide: the ones that climb out,
// the ones that only look like it, and the separators that decide which is which.
// Constructed rather than filtered — the vendored Hedgehog has no `Gen.filter`, no
// `Gen.choice` and no shrinking.

open Fable.Pyxpecto
open Hedgehog
open Yession.Domain.Sandboxes

let private check (name: string) (body: unit -> Property<unit>) =
    testCase name <| fun () -> Property.check (body ())

/// `..` climbs out; `...`, `..b` and `b..` only look like it; the empty segment and the
/// backslash are what decide which is which. A pool of random names would generate a
/// forest of paths that are all obviously fine.
let private segmentPool =
    [| ".."; "."; ""; "a"; "..b"; "b.."; "..."; "\\"; " "; "x" |]

/// Path-shaped strings. One draw in four gets a leading slash, so absolute spellings are
/// GENERATED rather than assumed covered by an example.
let private genHomePathish : Gen<string> =
    gen {
        let! indexes = Gen.list (Range.linear 1 5) (Gen.int32 (Range.linear 0 (segmentPool.Length - 1)))
        let! leading = Gen.int32 (Range.linear 0 3)
        let body = indexes |> List.map (fun i -> segmentPool.[i]) |> String.concat "/"
        return (if leading = 0 then "/" + body else body)
    }

/// Where a relative path lands, walked the way a filesystem resolves one — an independent
/// prediction, so the property is not the implementation agreeing with itself.
///
/// Depth may never go negative at ANY point rather than merely at the end: `../a/b`
/// escapes even though it finishes two levels down.
let private staysInside (relative: string) : bool =
    if relative.StartsWith "/" then false
    else
        let rec walk depth segments =
            match segments with
            | [] -> true
            | segment :: rest ->
                match segment with
                | "" | "." -> walk depth rest
                | ".." -> if depth = 0 then false else walk (depth - 1) rest
                | _ -> walk (depth + 1) rest
        walk 0 (relative.Split '/' |> Array.toList)

let tests =
    testList "seeded files" [

        // The promise the whole feature rests on.
        check "a path this build accepts can never land outside the sandbox's home" <| fun () ->
            property {
                let! raw = genHomePathish
                match HomePath.create raw with
                // Refused is the other half, and the decoder cases pin it by example.
                // There is nothing to prove about a path that never became one.
                | Error _ -> ()
                | Ok path ->
                    Expect.isTrue
                        (staysInside (HomePath.value path))
                        (sprintf "'%s' was accepted and resolves outside the home" raw)
            }

        // Accepted UNCHANGED or refused, never quietly rewritten. Normalising here would
        // BE the bypass: the string a check ran against and the string something later
        // writes to would be two different paths.
        check "a path is accepted as it was written, or not at all" <| fun () ->
            property {
                let! raw = genHomePathish
                match HomePath.create raw with
                | Error _ -> ()
                | Ok path ->
                    Expect.equal (HomePath.value path) (raw.Trim ()) "what was accepted is what was written"
            }

        // And the refusal says something. A generated corpus makes it cheap to assert that
        // no accepted-or-refused decision is ever silent, which is what somebody fixing a
        // `yession.yaml` actually needs.
        check "a refusal names the path it refused" <| fun () ->
            property {
                let! raw = genHomePathish
                match HomePath.create raw with
                | Ok _ -> ()
                | Error reason -> Expect.isFalse (reason = "") (sprintf "'%s' was refused without saying why" raw)
            }
    ]
