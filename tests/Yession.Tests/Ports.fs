module Yession.Tests.Ports

// Session port selection (Plan 11). Two things are under test and they fail differently:
// `SessionPorts.create` is the boot gate — every rejection must name what was wrong — and
// the registry helpers are the allocator, whose one absolute rule is that a port already
// promised to a session is never handed to another and never taken away.

open System
open Fable.Pyxpecto
open Yession.Domain
open Yession.Manager

let private expect =
    function
    | Ok v -> v
    | Error e -> failwith e

/// The Manager's port in these tests. Deliberately far from the ranges below, except where
/// a case is specifically about the overlap.
let private managerPort = 8321

let private record (id: string) (port: int option) : SessionRecord =
    { SessionId = SessionId.create id |> expect
      DisplayName = id
      CreatedAt = DateTimeOffset (2026, 8, 1, 12, 0, 0, TimeSpan.Zero)
      DataDir = sprintf "sessions/%s" id
      Port = port }

let private stateOf (records: SessionRecord list) : ManagerState =
    { Version = ManagerState.currentVersion; Sessions = records }

let private configTests =
    testList "SessionPorts.create" [
        testCase "unset is Ephemeral — the zero-config default is today's behaviour" <| fun () ->
            Expect.equal (SessionPorts.create managerPort "" |> expect) Ephemeral "blank should be ephemeral"
            Expect.equal (SessionPorts.create managerPort "   " |> expect) Ephemeral "whitespace should be ephemeral"

        testCase "a range is pinned and round-trips through describe" <| fun () ->
            match SessionPorts.create managerPort "8400-8499" |> expect with
            | Pinned range ->
                Expect.equal (PortRange.first range) 8400 "first"
                Expect.equal (PortRange.last range) 8499 "last"
                Expect.equal (PortRange.describe range) "8400-8499" "describe should round-trip"
            | Ephemeral -> failwith "expected a pinned range"

        testCase "a single port is a range of one" <| fun () ->
            match SessionPorts.create managerPort "8400" |> expect with
            | Pinned range ->
                Expect.equal (PortRange.toList range) [ 8400 ] "one port"
                Expect.equal (PortRange.describe range) "8400" "describe should not invent a range"
            | Ephemeral -> failwith "expected a pinned range"

        testCase "surrounding whitespace is trimmed, not rejected" <| fun () ->
            Expect.isOk (SessionPorts.create managerPort "  8400-8499  ") "should trim"

        testCase "malformed text is rejected rather than guessed at" <| fun () ->
            Expect.isError (SessionPorts.create managerPort "not-a-port") "letters"
            Expect.isError (SessionPorts.create managerPort "8400-") "missing end"
            Expect.isError (SessionPorts.create managerPort "-8499") "missing start"
            Expect.isError (SessionPorts.create managerPort "8400-8450-8499") "three parts"
            Expect.isError (SessionPorts.create managerPort "84 00") "embedded space"
            Expect.isError (SessionPorts.create managerPort "999999999999") "far too long to be a port"

        testCase "a range that ends before it starts is rejected" <| fun () ->
            Expect.isError (SessionPorts.create managerPort "8499-8400") "inverted"

        testCase "privileged and out-of-range ports are rejected" <| fun () ->
            Expect.isError (SessionPorts.create managerPort "80-90") "below 1024"
            Expect.isError (SessionPorts.create managerPort "1023") "just below 1024"
            Expect.isError (SessionPorts.create managerPort "70000") "above 65535"
            Expect.isOk (SessionPorts.create managerPort "1024") "1024 is the first allowed"
            Expect.isOk (SessionPorts.create managerPort "65535") "65535 is the last allowed"

        // The case this parameter exists for: a range that swallows the management UI is a
        // deployment that works until the day a session is allocated it.
        testCase "a range containing the Manager's own port is refused at boot" <| fun () ->
            Expect.isError (SessionPorts.create 8321 "8300-8400") "range straddles the manager port"
            Expect.isError (SessionPorts.create 8321 "8321") "range IS the manager port"
            Expect.isOk (SessionPorts.create 8321 "8400-8499") "a range clear of it is fine"

        testCase "the rejection says which port it objected to" <| fun () ->
            match SessionPorts.create 8321 "8300-8400" with
            | Ok _ -> failwith "expected a rejection"
            | Error e ->
                Expect.isTrue (e.Contains "8321") (sprintf "should name the manager port: %s" e)
    ]

let private allocationTests =
    testList "Registry port allocation" [
        testCase "the lowest free port is chosen" <| fun () ->
            let range = match SessionPorts.create managerPort "8400-8402" |> expect with
                        | Pinned r -> r
                        | Ephemeral -> failwith "expected pinned"
            let state = stateOf [ record "alpha" (Some 8400); record "beta" (Some 8402) ]
            Expect.equal (ManagerState.nextPort range state |> expect) 8401 "should fill the gap"

        testCase "an exhausted range is an error, not a duplicate" <| fun () ->
            let range = match SessionPorts.create managerPort "8400-8401" |> expect with
                        | Pinned r -> r
                        | Ephemeral -> failwith "expected pinned"
            let state = stateOf [ record "alpha" (Some 8400); record "beta" (Some 8401) ]
            Expect.isError (ManagerState.nextPort range state) "nothing left to hand out"

        // The rule that protects the browser's storage: a stored address is never revised.
        testCase "a stored port outside the range is kept and still excluded from allocation" <| fun () ->
            let range = match SessionPorts.create managerPort "8400-8401" |> expect with
                        | Pinned r -> r
                        | Ephemeral -> failwith "expected pinned"
            let state = stateOf [ record "legacy" (Some 9999); record "alpha" (Some 8400) ]
            let assigned, allocations = ManagerState.assignMissingPorts (Pinned range) state |> expect
            Expect.equal allocations [] "nothing needed assigning"
            Expect.equal
                (assigned.Sessions |> List.map (fun s -> s.Port))
                [ Some 9999; Some 8400 ]
                "the out-of-range port must survive untouched"
            Expect.equal (ManagerState.nextPort range assigned |> expect) 8401 "8400 is taken, 8401 is next"

        testCase "portless sessions are assigned in creation order, without collisions" <| fun () ->
            let ports = SessionPorts.create managerPort "8400-8499" |> expect
            let state = stateOf [ record "alpha" None; record "beta" (Some 8400); record "gamma" None ]
            let assigned, allocations = ManagerState.assignMissingPorts ports state |> expect
            Expect.equal
                (assigned.Sessions |> List.map (fun s -> s.Port))
                [ Some 8401; Some 8400; Some 8402 ]
                "existing ports untouched, new ones filling upward"
            Expect.equal (List.length allocations) 2 "two assignments should be reported"

        testCase "assignment reports what it decided, so an address never appears unexplained" <| fun () ->
            let ports = SessionPorts.create managerPort "8400-8499" |> expect
            let _, allocations = ManagerState.assignMissingPorts ports (stateOf [ record "alpha" None ]) |> expect
            Expect.equal (allocations |> List.map (fun (id, p) -> SessionId.value id, p)) [ "alpha", 8400 ] "one allocation"

        testCase "an exhausted range fails the assignment rather than leaving a session address-less" <| fun () ->
            let ports = SessionPorts.create managerPort "8400" |> expect
            Expect.isError
                (ManagerState.assignMissingPorts ports (stateOf [ record "alpha" None; record "beta" None ]))
                "two sessions cannot share one port"

        // Turning pinning off must not throw the addresses away, or turning it back on
        // would relocate every session at once — the data-losing move this all exists to
        // prevent.
        testCase "Ephemeral assigns nothing and discards nothing" <| fun () ->
            let state = stateOf [ record "alpha" (Some 8400); record "beta" None ]
            let assigned, allocations = ManagerState.assignMissingPorts Ephemeral state |> expect
            Expect.equal allocations [] "nothing assigned"
            Expect.equal
                (assigned.Sessions |> List.map (fun s -> s.Port))
                [ Some 8400; None ]
                "stored ports survive so switching back restores the same addresses"
    ]

let private duplicateTests =
    testList "Registry port conflicts" [
        testCase "a clean registry reports no duplicates" <| fun () ->
            let state = stateOf [ record "alpha" (Some 8400); record "beta" (Some 8401); record "gamma" None ]
            Expect.equal (ManagerState.duplicatePorts state) [] "no clashes"

        testCase "several sessions holding one port are reported with their ids" <| fun () ->
            let state = stateOf [ record "alpha" (Some 8400); record "beta" (Some 8400); record "gamma" (Some 8401) ]
            match ManagerState.duplicatePorts state with
            | [ (port, ids) ] ->
                Expect.equal port 8400 "the contested port"
                Expect.equal (ids |> List.map SessionId.value) [ "alpha"; "beta" ] "both claimants, in registry order"
            | other -> failwithf "expected exactly one clash, got %A" other

        testCase "portless sessions are not duplicates of each other" <| fun () ->
            Expect.equal
                (ManagerState.duplicatePorts (stateOf [ record "alpha" None; record "beta" None ]))
                []
                "None is not a claim on anything"
    ]

let tests =
    testList "Session ports (Plan 11)" [ configTests; allocationTests; duplicateTests ]
