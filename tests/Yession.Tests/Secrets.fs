module Yession.Tests.Secrets

// Plan 06, Step 1: the secrets vocabulary and the full ABAC decision table. Pure —
// cheap tier, every environment. The policy is default-deny: the table asserts every
// permitted cell explicitly and pins representative denies for each rule family, so a
// new Permit can only appear by editing the policy AND this table.

open System
open Fable.Core
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

// --- Step 3: cipher + store (real WebCrypto AES-GCM on Node; cheap tier) ----------------

open Yession.Host

[<Fable.Core.Emit("crypto.subtle.exportKey('raw', $0)")>]
let private exportRawKey (key: obj) : Fable.Core.JS.Promise<obj> = Fable.Core.Util.jsNative

let private freshPath (label: string) =
    sprintf "tests/Yession.Tests/out/.data/%s-%d.secrets.json" label (int (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ()) % 1000000)

let private sid (scope: SecretScope) (n: string) : SecretId =
    { Scope = scope; Name = SecretName.create n |> expect }

let private cipherTests =
    testList "cipher" [
        testCaseAsync "encrypt/decrypt round-trips under the right AAD" <|
            async {
                let! cipher = SecretsCipher.importKey (SecretsCipher.generateKek ())
                let aad = SecretsCipher.aadFor (SessionScope sessionA) name
                let! iv, ct = cipher.Encrypt aad "hunter2"
                let! back = cipher.Decrypt aad iv ct
                Expect.equal (expect back) "hunter2" "round-trips"
                Expect.isFalse (ct.Contains "hunter2") "ciphertext is not the plaintext"
            }

        testCaseAsync "a tampered ciphertext fails authentication" <|
            async {
                let! cipher = SecretsCipher.importKey (SecretsCipher.generateKek ())
                let aad = SecretsCipher.aadFor (SessionScope sessionA) name
                let! iv, ct = cipher.Encrypt aad "value"
                let tampered = (if ct.StartsWith "A" then "B" else "A") + ct.Substring 1
                let! r = cipher.Decrypt aad iv tampered
                Expect.isError r "tamper detected"
            }

        testCaseAsync "a ciphertext transplanted onto another entry's identity fails (AAD binding)" <|
            async {
                let! cipher = SecretsCipher.importKey (SecretsCipher.generateKek ())
                let! iv, ct = cipher.Encrypt (SecretsCipher.aadFor (UserScope alice) name) "alice's token"
                let! r = cipher.Decrypt (SecretsCipher.aadFor (SessionScope sessionA) name) iv ct
                Expect.isError r "transplant detected"
            }

        testCaseAsync "a different KEK cannot decrypt" <|
            async {
                let! sealer = SecretsCipher.importKey (SecretsCipher.generateKek ())
                let! other = SecretsCipher.importKey (SecretsCipher.generateKek ())
                let aad = SecretsCipher.aadFor (SessionScope sessionA) name
                let! iv, ct = sealer.Encrypt aad "value"
                let! r = other.Decrypt aad iv ct
                Expect.isError r "wrong key detected"
            }

        testCaseAsync "the imported KEK is non-extractable: exportKey rejects" <|
            async {
                let! cipher = SecretsCipher.importKey (SecretsCipher.generateKek ())
                let mutable threw = false
                try
                    let! _ = exportRawKey cipher.Key |> Async.AwaitPromise
                    ()
                with _ -> threw <- true
                Expect.isTrue threw "exporting the KEK must throw"
            }
    ]

let private openDurable path (keyStore: KeyStore.KeyStore) =
    async {
        let! opened = SecretStore.openStore (Some path) keyStore
        return expect (opened |> Result.mapError SecretStore.OpenError.describe)
    }

/// Open the in-memory ephemeral store (test double), stringifying the error kind.
let private openEphemeral () =
    async {
        let! opened = SecretStore.openStore None (KeyStore.random ())
        return expect (opened |> Result.mapError SecretStore.OpenError.describe)
    }

let private storeTests =
    testList "secret store" [
        testCaseAsync "set → list yields metadata; resolve returns the value; delete removes" <|
            async {
                let path = freshPath "roundtrip"
                let! store = openDurable path (KeyStore.inMemory ())
                let id = sid (SessionScope sessionA) "deploy-token"
                let! set = store.Set id "hunter2"
                Expect.equal (expect set).Id id "metadata identifies the secret"
                Expect.equal (store.List (SessionScope sessionA) |> List.length) 1 "listed"
                let! resolved = store.Resolve id
                Expect.equal (expect resolved) (Some "hunter2") "resolves internally"
                let! deleted = store.Delete id
                Expect.isTrue (expect deleted) "existed"
                let! resolvedAfter = store.Resolve id
                Expect.equal (expect resolvedAfter) None "gone"
            }

        testCaseAsync "the KEK is durable in the key store before first use, and reopening decrypts" <|
            async {
                let path = freshPath "reopen"
                let keyStore = KeyStore.inMemory ()
                let! store = openDurable path keyStore
                let! kek = keyStore.Get ()
                Expect.isTrue (expect kek |> Option.isSome) "KEK persisted at open, before any entry"
                let id = sid (SessionScope sessionA) "deploy-token"
                let! _ = store.Set id "hunter2"
                // A fresh store over the same file + key store (a Manager restart).
                let! reopened = openDurable path keyStore
                let! resolved = reopened.Resolve id
                Expect.equal (expect resolved) (Some "hunter2") "survives the restart"
                Expect.isTrue reopened.Durable "durable mode"
            }

        testCaseAsync "the value on disk is ciphertext only" <|
            async {
                let path = freshPath "atrest"
                let! store = openDurable path (KeyStore.inMemory ())
                let! _ = store.Set (sid (SessionScope sessionA) "deploy-token") "hunter2-at-rest"
                let onDisk = Fs.readText path
                Expect.isFalse (onDisk.Contains "hunter2-at-rest") "no plaintext at rest"
                Expect.isTrue (onDisk.Contains "deploy-token") "metadata is cleartext by design"
            }

        testCaseAsync "a corrupt file fails the open loudly" <|
            async {
                let path = freshPath "corrupt"
                Fs.writeTextAtomic path "{ not json"
                let! opened = SecretStore.openStore (Some path) (KeyStore.inMemory ())
                Expect.isError opened "corrupt store must not look empty"
            }

        testCaseAsync "a file sealed by a different key fails distinctly" <|
            async {
                let path = freshPath "wrongkek"
                let! first = openDurable path (KeyStore.inMemory ())
                let! _ = first.Set (sid (SessionScope sessionA) "deploy-token") "v"
                // A different key store = a different KEK (the first one is lost).
                let! reopened = SecretStore.openStore (Some path) (KeyStore.inMemory ())
                match reopened with
                | Error (SecretStore.SealedByDifferentKey _ as e) ->
                    Expect.isTrue ((SecretStore.OpenError.describe e).Contains "sealed by a different key") "distinct wording"
                | Error other -> failwithf "expected SealedByDifferentKey, got %A" other
                | Ok _ -> failwith "expected the open to fail"
            }

        testCaseAsync "the ephemeral store works without any file and reports non-durable" <|
            async {
                let! store = openEphemeral ()
                Expect.isFalse store.Durable "ephemeral"
                let id = sid (SessionScope sessionA) "deploy-token"
                let! _ = store.Set id "transient"
                let! resolved = store.Resolve id
                Expect.equal (expect resolved) (Some "transient") "usable in memory"
            }

        testCaseAsync "interleaved sets both land (encryption completes before the read-modify-write)" <|
            async {
                let path = freshPath "interleave"
                let! store = openDurable path (KeyStore.inMemory ())
                let a = store.Set (sid (SessionScope sessionA) "one") "1"
                let b = store.Set (sid (SessionScope sessionA) "two") "2"
                let! _ = [ a; b ] |> Async.Parallel
                Expect.equal (store.List (SessionScope sessionA) |> List.length) 2 "both entries present"
            }
    ]

// --- Step 4: the real OS credential manager ([Keyring]) ---------------------------------
// Runs only where the run declares a usable credential store: `check Keyring` drives
// the genuine Keychain / Credential Manager / Secret Service on a desktop, and
// self-wraps in a private D-Bus session + gnome-keyring in headless containers and CI.

let private keyringTests =
    testList "OS keyring round-trip" [
        testCaseAsync "the keyring-backed KeyStore probes available and round-trips the KEK payload" <|
            async {
                // A namespaced test entry so a real machine's product entry is never touched.
                let store = KeyStore.keyring "yession-test" ("roundtrip-" + Yession.Host.Interop.randomSecret ())
                let! available = store.Available ()
                Expect.isTrue available "this run declared Keyring, so the store must probe available"
                let! empty = store.Get ()
                Expect.equal (expect empty) None "starts absent"
                let payload = KeyStore.payload "kek-test" (SecretsCipher.generateKek ())
                let! set = store.Set payload
                expect set
                let! back = store.Get ()
                Expect.equal (expect back) (Some payload) "round-trips through the OS store"
            }

        testCaseAsync "a whole SecretStore runs durable over the real keyring" <|
            async {
                let keyStore = KeyStore.keyring "yession-test" ("store-" + Yession.Host.Interop.randomSecret ())
                let path = freshPath "keyring"
                let! store = openDurable path keyStore
                Expect.isTrue store.Durable "durable over the OS store"
                let id = sid (SessionScope sessionA) "deploy-token"
                let! _ = store.Set id "keyring-held"
                // Reopen: the KEK comes back from the credential manager and decrypts.
                let! reopened = openDurable path keyStore
                let! resolved = reopened.Resolve id
                Expect.equal (expect resolved) (Some "keyring-held") "KEK survives via the OS store"
            }
    ]

// --- Step 7: injection precedence (pure walk over an in-memory store; cheap tier) -------

let private resolutionTests =
    testList "injection resolution" [
        testCaseAsync "session scope shadows user scope shadows the fallback" <|
            async {
                let! store = openEphemeral ()
                let id scope = sid scope "deploy-token"
                let! _ = store.Set (id (SessionScope sessionA)) "from-session"
                let! _ = store.Set (id (UserScope alice)) "from-user"
                let fallback : SecretStore.ResolveSecret = fun _ _ -> async { return Ok "from-env" }
                let resolve = SecretStore.SecretResolution.compose (fun _ _ _ -> ()) store (fun _ -> Set.singleton alice) fallback

                let! full = resolve sessionA name
                Expect.equal (expect full) "from-session" "the session's own secret wins"

                let! _ = store.Delete (id (SessionScope sessionA))
                let! userLevel = resolve sessionA name
                Expect.equal (expect userLevel) "from-user" "a bound user's secret is next"

                let! _ = store.Delete (id (UserScope alice))
                let! envLevel = resolve sessionA name
                Expect.equal (expect envLevel) "from-env" "the process-env fallback is last"
            }

        testCaseAsync "an unbound user's secret is invisible: the policy skips the scope" <|
            async {
                let! store = openEphemeral ()
                let! _ = store.Set (sid (UserScope alice) "deploy-token") "alice's"
                // Session B has no bound users: alice's scope is never a candidate, and
                // even a hand-crafted walk would be denied by the policy.
                let fallback : SecretStore.ResolveSecret = fun _ n -> async { return Error (sprintf "secret '%s' is not available" (SecretName.value n)) }
                let resolve = SecretStore.SecretResolution.compose (fun _ _ _ -> ()) store (fun _ -> Set.empty) fallback
                let! outcome = resolve sessionB name
                Expect.isError outcome "nothing resolves"
            }

        testCaseAsync "the injection walk reports its outcome to the observer" <|
            async {
                let! store = openEphemeral ()
                let! _ = store.Set (sid (SessionScope sessionA) "deploy-token") "v"
                let mutable observed : (string * SecretStore.SecretResolution.InjectOutcome) list = []
                let observe sid n outcome = observed <- (SecretName.value n, outcome) :: observed
                let ok : SecretStore.ResolveSecret = fun _ _ -> async { return Ok "env-v" }
                let miss : SecretStore.ResolveSecret = fun _ _ -> async { return Error "nope" }
                let resolveHit = SecretStore.SecretResolution.compose observe store (fun _ -> Set.empty) miss
                let! _ = resolveHit sessionA name
                let resolveEnv = SecretStore.SecretResolution.compose observe store (fun _ -> Set.empty) ok
                let other = SecretName.create "OTHER" |> expect
                let! _ = resolveEnv sessionA other
                let resolveMiss = SecretStore.SecretResolution.compose observe store (fun _ -> Set.empty) miss
                let! _ = resolveMiss sessionA other
                Expect.equal
                    (List.rev observed)
                    [ "deploy-token", SecretStore.SecretResolution.InjectedFromScope (SessionScope sessionA)
                      "OTHER", SecretStore.SecretResolution.InjectedFromFallback
                      "OTHER", SecretStore.SecretResolution.InjectMissed "nope" ]
                    "one observation per resolution, naming the source"
            }

        testCaseAsync "a total miss reports the fallback's legible error" <|
            async {
                let! store = openEphemeral ()
                let resolve = SecretStore.SecretResolution.compose (fun _ _ _ -> ()) store (fun _ -> Set.empty) SecretStore.SecretResolution.processEnv
                let missing = SecretName.create "YESSION_DEFINITELY_MISSING" |> expect
                let! outcome = resolve sessionA missing
                match outcome with
                | Error e -> Expect.isTrue (e.Contains "YESSION_DEFINITELY_MISSING") "names the missing secret"
                | Ok _ -> failwith "expected a miss"
            }
    ]

// --- Step 6: the secrets control routes over real HTTP ([Ports]) ------------------------
// A bare control server (the Phase4 pattern) with a known caller table and the SAME
// pre-authorized handlers the Manager composes (ProcessManager.secretsApiFor), driven
// as raw HTTP so denied shapes below the typed client are expressible.

type private ControlReply =
    abstract status : int
    abstract body : string

[<Fable.Core.Emit("fetch($0, { method: 'POST', headers: { 'x-yession-control': $1, 'content-type': 'application/json' }, body: $2 }).then(async r => ({ status: r.status, body: await r.text() }))")>]
let private postControl (url: string) (secret: string) (body: string) : Fable.Core.JS.Promise<ControlReply> = Fable.Core.Util.jsNative

let private startControlServer (callers: (string * Control.ControlCaller) list) (api: Control.SecretsApi option) (onUnauthorized: string -> unit) =
    async {
        let table = Map.ofList callers
        let dummyRegister (_: string) (_: SessionId) (_: string) : Yession.Oidc.RegisterClientResponse =
            { ClientId = "unused"; ClientSecret = "unused"; Issuer = "unused" }
        let handler (req: Interop.IncomingMessage) (res: Interop.ServerResponse) =
            if not (Control.tryHandle
                        (fun secret -> Map.tryFind secret table)
                        (fun _ _ -> async { return Ok () })
                        (fun _ _ -> fun () -> ())
                        (fun _ -> fun () -> ())
                        dummyRegister
                        api
                        onUnauthorized
                        req res) then
                res.writeHead (404, Fable.Core.JsInterop.createObj [ "content-type", box "text/plain" ]) |> ignore
                res.``end`` "not found"
        let server = Interop.createServer handler
        let! listening =
            Async.FromContinuations (fun (cont, _, _) ->
                server.listen (0, "127.0.0.1", fun () -> cont server) |> ignore)
        return listening, sprintf "http://127.0.0.1:%d" (Interop.serverPort listening)
    }

let private caller sessionId users : Control.ControlCaller =
    { SessionId = sessionId; Capabilities = None; Users = users }

let private routeTests =
    testList "control routes" [
        testCaseAsync "a session writes, lists (metadata only), and deletes its own scope; strangers and siblings are refused" <|
            async {
                let! store = openEphemeral ()
                // Recording sinks: every audit record and every 401 path this run emits.
                let mutable audited : SecretStore.Audit.Record list = []
                let mutable unauthorized : string list = []
                let! _, url =
                    startControlServer
                        [ "secret-a", caller sessionA (Set.singleton alice)
                          "secret-b", caller sessionB Set.empty ]
                        (Some (ProcessManager.secretsApiFor (fun r -> audited <- r :: audited) store))
                        (fun path -> unauthorized <- path :: unauthorized)
                let post route secret body = postControl (sprintf "%s/control/secrets/%s" url route) secret body |> Async.AwaitPromise
                let eventNames () =
                    audited
                    |> List.rev
                    |> List.choose (fun r ->
                        match Map.tryFind "event.name" r.Attributes with
                        | Some (SecretStore.Audit.StringValue n) -> Some n
                        | _ -> None)

                // Own scope: the full lifecycle.
                let setBody = ControlWire.toString ControlWire.setSecretRequest { Scope = SessionScope sessionA; Name = name; Value = "hunter2" }
                let! set = post "set" "secret-a" setBody
                Expect.equal set.status 200 "own-scope set permits"
                Expect.isFalse (set.body.Contains "hunter2") "the set response carries no value"
                let! listed = post "list" "secret-a" (ControlWire.toString ControlWire.listSecretsRequest { Scope = SessionScope sessionA })
                Expect.equal listed.status 200 "own-scope list permits"
                Expect.isTrue (listed.body.Contains "deploy-token") "metadata names the secret"
                Expect.isFalse (listed.body.Contains "hunter2") "the raw list body carries no value"

                // An unknown secret is turned away at the door.
                let! unknown = post "set" "not-a-secret" setBody
                Expect.equal unknown.status 401 "invalid control secret"

                // Session B cannot touch A's scope (and the deny does not echo values).
                let! cross = post "set" "secret-b" setBody
                Expect.equal cross.status 403 "cross-session write denies"
                let! crossList = post "list" "secret-b" (ControlWire.toString ControlWire.listSecretsRequest { Scope = SessionScope sessionA })
                Expect.equal crossList.status 403 "cross-session list denies"

                // User scope: sessions never write; a bound user's collection lists.
                let! userWrite = post "set" "secret-a" (ControlWire.toString ControlWire.setSecretRequest { Scope = UserScope alice; Name = name; Value = "v" })
                Expect.equal userWrite.status 403 "sessions cannot write user scope"
                let! userList = post "list" "secret-a" (ControlWire.toString ControlWire.listSecretsRequest { Scope = UserScope alice })
                Expect.equal userList.status 200 "a bound user's collection lists"
                let! unboundList = post "list" "secret-b" (ControlWire.toString ControlWire.listSecretsRequest { Scope = UserScope alice })
                Expect.equal unboundList.status 403 "an unbound session cannot list a user's collection"

                // There is no read-back route, for anyone.
                let! get = post "get" "secret-a" setBody
                Expect.equal get.status 404 "/control/secrets/get does not exist"

                // Delete closes the lifecycle.
                let! deleted = post "delete" "secret-a" (ControlWire.toString ControlWire.deleteSecretRequest { Scope = SessionScope sessionA; Name = name })
                Expect.equal deleted.status 200 "own-scope delete permits"
                Expect.isTrue (deleted.body.Contains "true") "reports the entry existed"

                // The audit trail (Plan 06 telemetry): permitted ops and every deny
                // became records; the 401 hook saw the path; no formatted record leaks
                // the value.
                Expect.equal
                    (eventNames ())
                    [ "yession.secret.set"; "yession.secret.list"        // own-scope set + list
                      "yession.authz.deny"; "yession.authz.deny"        // session B's two cross-scope attempts
                      "yession.authz.deny"                              // user-scope write
                      "yession.secret.list"                             // bound user's list
                      "yession.authz.deny"                              // unbound user list
                      "yession.secret.delete" ]
                    "one audit record per authorization decision, in order"
                Expect.equal unauthorized [ "/control/secrets/set" ] "the 401 hook carries the request path"
                for r in audited do
                    Expect.isFalse ((SecretStore.Audit.format r).Contains "hunter2") "no audit record ever renders a value"
            }

        testCaseAsync "the session-side typed capability drives the full lifecycle over the wire" <|
            async {
                let! store = openEphemeral ()
                let! _, url =
                    startControlServer
                        [ "secret-a", caller sessionA Set.empty ]
                        (Some (ProcessManager.secretsApiFor (fun _ -> ()) store))
                        ignore
                // The capability a Session Process builds in SessionMain: pre-bound to
                // its own scope; failures are values.
                let capability = ControlClient.secretsCapabilities url "secret-a" sessionA
                let! set = capability.SetSecret name "hunter2"
                Expect.equal (expect set).Id { Scope = SessionScope sessionA; Name = name } "metadata round-trips"
                let! listed = capability.ListSecrets ()
                Expect.equal (expect listed |> List.length) 1 "lists the one secret"
                let! deleted = capability.DeleteSecret name
                Expect.isTrue (expect deleted) "reports existence on delete"
                let! deletedAgain = capability.DeleteSecret name
                Expect.isFalse (expect deletedAgain) "second delete reports absence"
            }

        testCaseAsync "the agent capability surface denies cleanly without a grant" <|
            async {
                // What a turn sees when no secrets capability is threaded (Host passes
                // None): every operation is a legible Error value, never an exception.
                let! set = AgentCapabilities.none.SetSecret name "v"
                Expect.isError set "set denies"
                let! listed = AgentCapabilities.none.ListSecrets ()
                Expect.isError listed "list denies"
                let! deleted = AgentCapabilities.none.DeleteSecret name
                Expect.isError deleted "delete denies"
            }

        testCaseAsync "a Manager without a secrets store answers 403" <|
            async {
                let! _, url = startControlServer [ "secret-a", caller sessionA Set.empty ] None ignore
                let! reply =
                    postControl
                        (url + "/control/secrets/list")
                        "secret-a"
                        (ControlWire.toString ControlWire.listSecretsRequest { Scope = SessionScope sessionA })
                    |> Async.AwaitPromise
                Expect.equal reply.status 403 "no store configured"
            }
    ]

let tests =
    testList "Secrets" [
        constructorTests
        policyTests
        envelopeTests
        wireTests
        cipherTests
        storeTests
        resolutionTests
        Tag.needs "OS keyring" [ Tag.Keyring ] (fun () -> keyringTests)
        Tag.needs "Secrets control routes" [ Tag.Ports ] (fun () -> routeTests)
    ]
