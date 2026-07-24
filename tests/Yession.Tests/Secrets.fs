module Yession.Tests.Secrets

// Plan 06, Step 1: the secrets vocabulary and the full ABAC decision table. Pure —
// cheap tier, every environment. The policy is default-deny: the table asserts every
// permitted cell explicitly and pins representative denies for each rule family, so a
// new Permit can only appear by editing the policy AND this table.

open System
open Fable.Pyxpecto
open Yession.Domain

let private expect =
    function
    | Ok v -> v
    | Error e -> failwith e

let private sessionA = SessionId.create "session-aa" |> expect
let private sessionB = SessionId.create "session-bb" |> expect
let private alice = UserSubject.create "alice" |> expect
let private bob = UserSubject.create "bob" |> expect
let private name = SecretName.create "deploy-token" |> expect

/// A launch of session A that alice (and only alice) has signed in to.
let private callerA : AuthzSubject = { Session = Some sessionA; Users = Set.singleton alice }
/// A launch of session A with no completed login.
let private callerANoUsers : AuthzSubject = { Session = Some sessionA; Users = Set.empty }
/// A subject with no session at all (the future UI shape).
let private noSession : AuthzSubject = { Session = None; Users = Set.singleton alice }

let private request subject action resource : AuthzRequest =
    { Subject = subject; Action = SecretAction action; Resource = resource }

let private permits msg r = Expect.equal (Policy.authorize r) Permit msg
let private denies msg r =
    match Policy.authorize r with
    | Deny _ -> ()
    | Permit -> failwithf "expected Deny: %s" msg

let private onOwn action = request callerA action (SecretResource { Scope = SessionScope sessionA; Name = name })
let private onSibling action = request callerA action (SecretResource { Scope = SessionScope sessionB; Name = name })
let private onUser subject user action = request subject action (SecretResource { Scope = UserScope user; Name = name })

let private constructorTests =
    testList "constructors" [
        testCase "UserSubject trims surrounding whitespace" <| fun () ->
            Expect.equal (UserSubject.create "  local  " |> expect |> UserSubject.value) "local" "trimmed"
        testCase "UserSubject rejects blank input" <| fun () ->
            Expect.isError (UserSubject.create "   ") "blank rejected"
        testCase "SecretScope.describe distinguishes scopes" <| fun () ->
            Expect.equal (SecretScope.describe (SessionScope sessionA)) "session:session-aa" "session form"
            Expect.equal (SecretScope.describe (UserScope alice)) "user:alice" "user form"
    ]

let private policyTests =
    testList "policy decision table" [
        // A session owns its session-scoped secrets outright.
        testCase "own session scope: set/delete/inject/list permit" <| fun () ->
            permits "set" (onOwn SetSecret)
            permits "delete" (onOwn DeleteSecret)
            permits "inject" (onOwn InjectSecret)
            permits "list" (request callerA ListSecrets (SecretCollection (SessionScope sessionA)))

        // Nothing crosses sessions.
        testCase "sibling session scope: every action denies" <| fun () ->
            denies "set" (onSibling SetSecret)
            denies "delete" (onSibling DeleteSecret)
            denies "inject" (onSibling InjectSecret)
            denies "list" (request callerA ListSecrets (SecretCollection (SessionScope sessionB)))

        // User scope reads (list/inject) require the Manager-recorded binding.
        testCase "bound user: list + inject permit" <| fun () ->
            permits "inject" (onUser callerA alice InjectSecret)
            permits "list" (request callerA ListSecrets (SecretCollection (UserScope alice)))

        testCase "unbound user: list + inject deny" <| fun () ->
            denies "inject other user" (onUser callerA bob InjectSecret)
            denies "inject before any login" (onUser callerANoUsers alice InjectSecret)
            denies "list other user" (request callerA ListSecrets (SecretCollection (UserScope bob)))

        // Sessions never write user scope, binding or not.
        testCase "user scope writes deny even for a bound user" <| fun () ->
            denies "set" (onUser callerA alice SetSecret)
            denies "delete" (onUser callerA alice DeleteSecret)

        // Default-deny for shapes no rule matches.
        testCase "session-less subject denies on session scope" <| fun () ->
            denies "set without a session" (request noSession SetSecret (SecretResource { Scope = SessionScope sessionA; Name = name }))
        testCase "list of a RESOURCE (not a collection) has no rule and denies" <| fun () ->
            denies "list resource" (onOwn ListSecrets)
        testCase "write aimed at a COLLECTION has no rule and denies" <| fun () ->
            denies "set collection" (request callerA SetSecret (SecretCollection (SessionScope sessionA)))

        testCase "deny reasons never echo the secret name" <| fun () ->
            match Policy.authorize (onSibling SetSecret) with
            | Deny reason -> Expect.isFalse (reason.Contains "deploy-token") "reason is generic"
            | Permit -> failwith "expected Deny"
    ]

// --- Step 2: the envelope + wire codecs -------------------------------------------------

open Yession.Manager

let private entry scope entryName iv ct : SecretEntry =
    { Scope = scope
      Name = entryName
      CreatedAt = DateTimeOffset.Parse "2026-07-24T10:00:00Z"
      UpdatedAt = DateTimeOffset.Parse "2026-07-24T11:00:00Z"
      Iv = iv
      Ciphertext = ct }

let private envelopeTests =
    testList "SecretsFile envelope" [
        testCase "round-trips through the codec" <| fun () ->
            let file =
                SecretsFile.empty "kek-1"
                |> SecretsFile.upsert (entry (SessionScope sessionA) name "aXY" "Y3Q")
                |> SecretsFile.upsert (entry (UserScope alice) name "aXYy" "Y3Qy")
            let round = SecretsCodec.toString file |> SecretsCodec.fromString |> expect
            Expect.equal round file "identical after round-trip"

        testCase "unknown scope kind fails the decode" <| fun () ->
            let json = """{"version":1,"kekId":"k","entries":[{"scope":{"kind":"planet","sessionId":"x"},"name":"n","createdAt":"2026-07-24T10:00:00Z","updatedAt":"2026-07-24T10:00:00Z","iv":"a","ciphertext":"b"}]}"""
            Expect.isError (SecretsCodec.fromString json) "unknown kind rejected"

        testCase "corrupt JSON fails loudly" <| fun () ->
            Expect.isError (SecretsCodec.fromString "{ not json") "corrupt input rejected"

        testCase "upsert replaces by (scope, name) and keeps CreatedAt" <| fun () ->
            let first = entry (SessionScope sessionA) name "iv1" "ct1"
            let second = { entry (SessionScope sessionA) name "iv2" "ct2" with CreatedAt = DateTimeOffset.Parse "2026-07-24T12:00:00Z" }
            let file = SecretsFile.empty "k" |> SecretsFile.upsert first |> SecretsFile.upsert second
            Expect.equal file.Entries.Length 1 "replaced, not appended"
            Expect.equal file.Entries.Head.Ciphertext "ct2" "new ciphertext"
            Expect.equal file.Entries.Head.CreatedAt first.CreatedAt "creation timestamp survives"

        testCase "same name in different scopes are distinct entries" <| fun () ->
            let file =
                SecretsFile.empty "k"
                |> SecretsFile.upsert (entry (SessionScope sessionA) name "iv1" "ct1")
                |> SecretsFile.upsert (entry (UserScope alice) name "iv2" "ct2")
            Expect.equal file.Entries.Length 2 "two entries"

        testCase "remove deletes exactly the identified entry" <| fun () ->
            let file =
                SecretsFile.empty "k"
                |> SecretsFile.upsert (entry (SessionScope sessionA) name "iv1" "ct1")
                |> SecretsFile.upsert (entry (SessionScope sessionB) name "iv2" "ct2")
                |> SecretsFile.remove { Scope = SessionScope sessionA; Name = name }
            Expect.equal (file |> SecretsFile.tryFind { Scope = SessionScope sessionA; Name = name }) None "gone"
            Expect.isTrue (file |> SecretsFile.tryFind { Scope = SessionScope sessionB; Name = name } |> Option.isSome) "sibling untouched"

        testCase "list filters by scope and yields metadata only" <| fun () ->
            let file =
                SecretsFile.empty "k"
                |> SecretsFile.upsert (entry (SessionScope sessionA) name "iv1" "ct1")
                |> SecretsFile.upsert (entry (UserScope alice) name "iv2" "ct2")
            let listed = SecretsFile.list (SessionScope sessionA) file
            Expect.equal listed.Length 1 "one entry in scope"
            Expect.equal listed.Head.Id.Scope (SessionScope sessionA) "right scope"
            // The metadata JSON carries no value/ciphertext field at all.
            let json = ControlWire.toString ControlWire.secretMetadata listed.Head
            Expect.isFalse (json.Contains "ct1") "no ciphertext in metadata"
            Expect.isFalse (json.Contains "value") "no value field in metadata"
    ]

let private wireTests =
    testList "control wire codecs" [
        testCase "set request round-trips" <| fun () ->
            let r : ControlWire.SetSecretRequest = { Scope = SessionScope sessionA; Name = name; Value = "s3cr3t" }
            let round = ControlWire.toString ControlWire.setSecretRequest r |> ControlWire.fromString ControlWire.setSecretRequest |> expect
            Expect.equal round r "identical"

        testCase "list request + response round-trip" <| fun () ->
            let req : ControlWire.ListSecretsRequest = { Scope = UserScope alice }
            Expect.equal (ControlWire.toString ControlWire.listSecretsRequest req |> ControlWire.fromString ControlWire.listSecretsRequest |> expect) req "request"
            let resp : ControlWire.ListSecretsResponse =
                { Secrets = [ { Id = { Scope = UserScope alice; Name = name }
                                CreatedAt = DateTimeOffset.Parse "2026-07-24T10:00:00Z"
                                UpdatedAt = DateTimeOffset.Parse "2026-07-24T10:00:00Z" } ] }
            Expect.equal (ControlWire.toString ControlWire.listSecretsResponse resp |> ControlWire.fromString ControlWire.listSecretsResponse |> expect) resp "response"

        testCase "delete request + response round-trip" <| fun () ->
            let req : ControlWire.DeleteSecretRequest = { Scope = SessionScope sessionA; Name = name }
            Expect.equal (ControlWire.toString ControlWire.deleteSecretRequest req |> ControlWire.fromString ControlWire.deleteSecretRequest |> expect) req "request"
            let resp : ControlWire.DeleteSecretResponse = { Deleted = true }
            Expect.equal (ControlWire.toString ControlWire.deleteSecretResponse resp |> ControlWire.fromString ControlWire.deleteSecretResponse |> expect) resp "response"

        testCase "blank secret name fails the request decode" <| fun () ->
            let json = """{"scope":{"kind":"session","sessionId":"session-aa"},"name":"   ","value":"v"}"""
            Expect.isError (ControlWire.fromString ControlWire.setSecretRequest json) "blank name rejected"
    ]

let tests =
    testList "Secrets" [
        constructorTests
        policyTests
        envelopeTests
        wireTests
    ]
