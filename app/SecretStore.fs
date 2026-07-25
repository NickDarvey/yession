module Yession.Host.SecretStore

// The Manager's secret store (Plan 06): the pure `SecretsFile` envelope behind the
// `SecretsCodec`, values as per-entry AES-GCM ciphertext under a KEK the OS credential
// manager holds (KeyStore). One code path serves both modes: `Some path` = durable
// (encrypted file in the Manager's data dir, ManagerStore-style atomic writes);
// `None` = ephemeral (no credential manager on this host — no file I/O at all, a
// per-boot random KEK, everything dies with the Manager). `Resolve` is
// Manager-INTERNAL: it feeds environment injection only; no control route calls it,
// so a secret value never travels back over the control channel.

open System
open Yession.Domain
open Yession.Manager

type SecretStore =

    { List : SecretScope -> SecretMetadata list
      Set : SecretId -> string -> Async<Result<SecretMetadata, string>>
      /// false = did not exist.
      Delete : SecretId -> Async<Result<bool, string>>
      /// Decrypt-on-demand for env injection. Never exposed over the control channel.
      Resolve : SecretId -> Async<Result<string option, string>>
      /// True when backed by the encrypted file; false when in-memory only.
      Durable : bool
      /// True when the KEK was minted at this open (first run); false when loaded.
      KekMinted : bool
      /// Entries in the envelope at open (0 for a fresh or ephemeral store).
      EntriesAtOpen : int }

/// Why an open failed — structural, so the boot path can audit the KIND without
/// string-sniffing. `describe` renders exactly the wording the failures always had.
type OpenError =
    | KeyStoreFailure of detail: string
    | CorruptStore of path: string * detail: string
    | SealedByDifferentKey of path: string * fileKekId: string * hostKekId: string

module OpenError =
    let describe (error: OpenError) : string =
        match error with
        | KeyStoreFailure detail -> detail
        | CorruptStore (path, detail) -> sprintf "corrupt secrets store %s: %s" path detail
        | SealedByDifferentKey (path, fileKekId, hostKekId) ->
            sprintf "secrets store %s was sealed by a different key (%s, this host's is %s) — restore that key or delete the file" path fileKekId hostKekId

    let kind (error: OpenError) : string =
        match error with
        | KeyStoreFailure _ -> "keystore"
        | CorruptStore _ -> "corrupt"
        | SealedByDifferentKey _ -> "sealed"

let private now () : DateTimeOffset = DateTimeOffset.UtcNow

/// How a launched environment's `SecretRef` env vars resolve to values, pre-scoped to
/// the session being started. The ONLY consumer of `SecretStore.Resolve`.
type ResolveSecret = SessionId -> SecretName -> Async<Result<string, string>>

/// Open the store. Durable when `persistPath` is set: KEK through the KeyStore
/// (created on first run, durable in the credential manager BEFORE first use), file
/// loaded through the codec (missing = empty; corrupt = loud Error; KekId mismatch =
/// distinct loud Error). Ephemeral when `None`: same semantics, zero file I/O.
let openStore (persistPath: string option) (keyStore: KeyStore.KeyStore) : Async<Result<SecretStore, OpenError>> =
    async {
        // Resolve (or mint) the KEK — the only moment the raw key bytes are in scope.
        let! stored = keyStore.Get ()
        match stored |> Result.bind (function
                        | Some raw -> KeyStore.splitPayload raw |> Result.map (fun p -> p, false)
                        | None ->
                            let fresh = Interop.randomSecret (), SecretsCipher.generateKek ()
                            Ok (fresh, true)) with
        | Error e -> return Error (KeyStoreFailure (sprintf "secrets key store (%s): %s" keyStore.Name e))
        | Ok ((kekId, kekB64u), minted) ->
            let persist (result: Result<unit, string>) =
                async {
                    if minted then
                        // Durable in the credential manager BEFORE any entry is sealed
                        // under it — a crash between the two loses nothing.
                        let! set = keyStore.Set (KeyStore.payload kekId kekB64u)
                        return set |> Result.bind (fun () -> result)
                    else
                        return result
                }
            let! persisted = persist (Ok ())
            match persisted with
            | Error e -> return Error (KeyStoreFailure (sprintf "secrets key store (%s): could not persist the key: %s" keyStore.Name e))
            | Ok () ->

            let! cipher = SecretsCipher.importKey kekB64u

            // Load (or initialise) the envelope.
            let initial : Result<SecretsFile, OpenError> =
                match persistPath with
                | None -> Ok (SecretsFile.empty kekId)
                | Some path when not (Fs.exists path) -> Ok (SecretsFile.empty kekId)
                | Some path ->
                    match SecretsCodec.fromString (Fs.readText path) with
                    | Error e -> Error (CorruptStore (path, e))
                    | Ok file when file.KekId <> kekId ->
                        Error (SealedByDifferentKey (path, file.KekId, kekId))
                    | Ok file -> Ok file

            match initial with
            | Error e -> return Error e
            | Ok loaded ->
                // The single mutable envelope. Mutations are read-modify-write-save on
                // the Node loop AFTER their (async) encryption completes, so
                // interleaved Sets compose instead of losing entries.
                let mutable file = loaded
                let save () =
                    persistPath |> Option.iter (fun path -> Fs.writeTextAtomic path (SecretsCodec.toString file))

                let store =
                    { List = fun scope -> SecretsFile.list scope file
                      Set =
                        fun id value ->
                            async {
                                let! iv, ciphertext = cipher.Encrypt (SecretsCipher.aadFor id.Scope id.Name) value
                                let timestamp = now ()
                                let entry : SecretEntry =
                                    { Scope = id.Scope
                                      Name = id.Name
                                      CreatedAt = timestamp
                                      UpdatedAt = timestamp
                                      Iv = iv
                                      Ciphertext = ciphertext }
                                file <- SecretsFile.upsert entry file
                                save ()
                                match SecretsFile.list id.Scope file |> List.tryFind (fun m -> m.Id = id) with
                                | Some metadata -> return Ok metadata
                                | None -> return Error "secret vanished during set"
                            }
                      Delete =
                        fun id ->
                            async {
                                let existed = SecretsFile.tryFind id file |> Option.isSome
                                if existed then
                                    file <- SecretsFile.remove id file
                                    save ()
                                return Ok existed
                            }
                      Resolve =
                        fun id ->
                            async {
                                match SecretsFile.tryFind id file with
                                | None -> return Ok None
                                | Some entry ->
                                    let! decrypted = cipher.Decrypt (SecretsCipher.aadFor entry.Scope entry.Name) entry.Iv entry.Ciphertext
                                    return decrypted |> Result.map Some
                            }
                      Durable = Option.isSome persistPath
                      KekMinted = minted
                      EntriesAtOpen = loaded.Entries.Length }
                return Ok store
    }

/// Secret resolution for environment injection (Plan 06): which value a `SecretRef`
/// means for THIS session, in precedence order — the session's own scoped secret, then
/// a user-scoped secret of a user the Manager verified into the session's launch, then
/// the fallback. Every store step passes the same pure policy the control routes use
/// (InjectSecret); a Deny simply means "not this caller's secret — next scope", and
/// only a store FAILURE stops the walk.
module SecretResolution =

    /// What one SecretRef resolution came to — the audit observation the Manager maps
    /// to a log record (domain-shaped here; TelemetryReceiver compiles later).
    type InjectOutcome =
        | InjectedFromScope of SecretScope
        | InjectedFromFallback
        | InjectMissed of reason: string

    type Observe = SessionId -> SecretName -> InjectOutcome -> unit

    /// The explicit lowest-precedence fallback: the Manager's OWN process environment —
    /// the pre-Plan-06 "smallest local-dev store" (docs/GAPS.md), kept so existing
    /// flows work unseeded. Inside the Manager's trust boundary; any store entry
    /// shadows it.
    [<Fable.Core.Emit("process.env[$0]")>]
    let private processEnvRaw (name: string) : string = Fable.Core.Util.jsNative

    let processEnv : ResolveSecret =
        fun _sessionId name ->
            async {
                let value = processEnvRaw (SecretName.value name)
                if isNull (box value) then
                    return Error (sprintf "secret '%s' is not available" (SecretName.value name))
                else return Ok value
            }

    /// The scopes a session may draw injected values from, most specific first.
    let scopesFor (sessionId: SessionId) (users: Set<UserSubject>) : SecretScope list =
        SessionScope sessionId :: (users |> Set.toList |> List.map UserScope)

    let compose (observe: Observe) (store: SecretStore) (usersOf: SessionId -> Set<UserSubject>) (fallback: ResolveSecret) : ResolveSecret =
        fun sessionId name ->
            async {
                let users = usersOf sessionId
                let subject : AuthzSubject = { Session = Some sessionId; Users = users }
                let rec walk scopes =
                    async {
                        match scopes with
                        | [] ->
                            // The walk fell through to the fallback: its outcome is the
                            // resolution's outcome. Intermediate deny/miss steps stay
                            // silent — that is the precedence working, not an event.
                            match! fallback sessionId name with
                            | Ok value ->
                                observe sessionId name InjectedFromFallback
                                return Ok value
                            | Error e ->
                                observe sessionId name (InjectMissed e)
                                return Error e
                        | scope :: rest ->
                            let id : SecretId = { Scope = scope; Name = name }
                            let request =
                                { Subject = subject
                                  Action = SecretAction InjectSecret
                                  Resource = SecretResource id }
                            match Policy.authorize request with
                            | Deny _ -> return! walk rest
                            | Permit ->
                                match! store.Resolve id with
                                | Ok (Some value) ->
                                    observe sessionId name (InjectedFromScope scope)
                                    return Ok value
                                | Ok None -> return! walk rest
                                | Error e -> return Error e
                    }
                return! walk (scopesFor sessionId users)
            }
