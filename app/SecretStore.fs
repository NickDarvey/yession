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
      Durable : bool }

let private now () : DateTimeOffset = DateTimeOffset.UtcNow

/// Open the store. Durable when `persistPath` is set: KEK through the KeyStore
/// (created on first run, durable in the credential manager BEFORE first use), file
/// loaded through the codec (missing = empty; corrupt = loud Error; KekId mismatch =
/// distinct loud Error). Ephemeral when `None`: same semantics, zero file I/O.
let openStore (persistPath: string option) (keyStore: KeyStore.KeyStore) : Async<Result<SecretStore, string>> =
    async {
        // Resolve (or mint) the KEK — the only moment the raw key bytes are in scope.
        let! stored = keyStore.Get ()
        match stored |> Result.bind (function
                        | Some raw -> KeyStore.splitPayload raw |> Result.map (fun p -> p, false)
                        | None ->
                            let fresh = Interop.randomSecret (), SecretsCipher.generateKek ()
                            Ok (fresh, true)) with
        | Error e -> return Error (sprintf "secrets key store (%s): %s" keyStore.Name e)
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
            | Error e -> return Error (sprintf "secrets key store (%s): could not persist the key: %s" keyStore.Name e)
            | Ok () ->

            let! cipher = SecretsCipher.importKey kekB64u

            // Load (or initialise) the envelope.
            let initial : Result<SecretsFile, string> =
                match persistPath with
                | None -> Ok (SecretsFile.empty kekId)
                | Some path when not (Fs.exists path) -> Ok (SecretsFile.empty kekId)
                | Some path ->
                    match SecretsCodec.fromString (Fs.readText path) with
                    | Error e -> Error (sprintf "corrupt secrets store %s: %s" path e)
                    | Ok file when file.KekId <> kekId ->
                        Error (sprintf "secrets store %s was sealed by a different key (%s, this host's is %s) — restore that key or delete the file" path file.KekId kekId)
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
                      Durable = Option.isSome persistPath }
                return Ok store
    }
