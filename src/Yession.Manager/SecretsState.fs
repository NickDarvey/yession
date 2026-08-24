namespace Yession.Manager

open System
open Yession.Domain
open Yession.Domain.Access

#if FABLE_COMPILER
open Thoth.Json
#else
open Thoth.Json.Net
#endif

/// The secrets envelope (Plan 06): the pure state behind the Manager's secret store,
/// and its explicit codec — the ONLY path between the store and bytes (the
/// `ManagerState`/`ManagerCodec` discipline). Values exist here only as per-entry
/// AES-256-GCM ciphertext, AAD-bound to the entry's scope+name, so the codec cannot
/// leak what it never holds and a ciphertext transplanted onto another entry fails
/// authentication. Metadata (scopes, names, timestamps) is cleartext BY DESIGN — it is
/// exactly what the list surface serves.
type SecretEntry =
    { Scope : SecretScope
      Name : SecretName
      CreatedAt : DateTimeOffset
      UpdatedAt : DateTimeOffset
      /// base64url, 12 random bytes — fresh per encryption, never reused under the KEK.
      Iv : string
      /// base64url AES-256-GCM output (ciphertext ‖ 16-byte tag, the WebCrypto layout).
      Ciphertext : string }

type SecretsFile =
    { /// Schema version — the migration hook (ManagerState discipline).
      Version : int
      /// Names WHICH KEK sealed these entries, so a decrypt failure distinguishes
      /// "sealed by a different key" from corruption — both loud, differently worded.
      KekId : string
      /// In first-set order; (Scope, Name) is the key.
      Entries : SecretEntry list }

module SecretsFile =

    let currentVersion = 1

    let empty (kekId: string) : SecretsFile =
        { Version = currentVersion; KekId = kekId; Entries = [] }

    let private isEntry (id: SecretId) (entry: SecretEntry) =
        entry.Scope = id.Scope && entry.Name = id.Name

    let tryFind (id: SecretId) (file: SecretsFile) : SecretEntry option =
        file.Entries |> List.tryFind (isEntry id)

    /// Insert or replace by (Scope, Name). A replaced entry keeps its CreatedAt.
    let upsert (entry: SecretEntry) (file: SecretsFile) : SecretsFile =
        let id = { SecretId.Scope = entry.Scope; SecretId.Name = entry.Name }
        match tryFind id file with
        | Some existing ->
            { file with
                Entries =
                    file.Entries
                    |> List.map (fun e -> if isEntry id e then { entry with CreatedAt = existing.CreatedAt } else e) }
        | None ->
            { file with Entries = file.Entries @ [ entry ] }

    let remove (id: SecretId) (file: SecretsFile) : SecretsFile =
        { file with Entries = file.Entries |> List.filter (isEntry id >> not) }

    /// The metadata listing for one scope. Returns `SecretMetadata` — a type that
    /// cannot carry a value.
    let list (scope: SecretScope) (file: SecretsFile) : SecretMetadata list =
        file.Entries
        |> List.filter (fun e -> e.Scope = scope)
        |> List.map (fun e ->
            { Id = { SecretId.Scope = e.Scope; SecretId.Name = e.Name }
              CreatedAt = e.CreatedAt
              UpdatedAt = e.UpdatedAt })

module SecretsCodec =

    let private secretName : Codec<SecretName> =
        { Encode = SecretName.value >> Encode.string
          Decode =
            Decode.string
            |> Decode.andThen (fun raw ->
                match SecretName.create raw with
                | Ok v -> Decode.succeed v
                | Error e -> Decode.fail e) }

    let private userSubject : Codec<UserId> =
        { Encode = UserId.value >> Encode.string
          Decode =
            Decode.string
            |> Decode.andThen (fun raw ->
                match UserId.create raw with
                | Ok v -> Decode.succeed v
                | Error e -> Decode.fail e) }

    /// Shared with the control wire:
    /// {"kind":"session",...} | {"kind":"user",...} | {"kind":"peer",...} | {"kind":"local"}.
    /// `local` carries no key — the deployment IS the owner, so there is nothing to name.
    let secretScope : Codec<SecretScope> =
        { Encode =
            (fun scope ->
                match scope with
                | SessionScope sessionId ->
                    Encode.object [ "kind", Encode.string "session"; "sessionId", Codec.sessionId.Encode sessionId ]
                | UserScope user ->
                    Encode.object [ "kind", Encode.string "user"; "sub", userSubject.Encode user ]
                | PeerScope peer ->
                    Encode.object [ "kind", Encode.string "peer"; "peerId", Codec.peerId.Encode peer ]
                | LocalScope ->
                    Encode.object [ "kind", Encode.string "local" ])
          Decode =
            Decode.field "kind" Decode.string
            |> Decode.andThen (function
                | "session" -> Decode.field "sessionId" Codec.sessionId.Decode |> Decode.map SessionScope
                | "user" -> Decode.field "sub" userSubject.Decode |> Decode.map UserScope
                | "peer" -> Decode.field "peerId" Codec.peerId.Decode |> Decode.map PeerScope
                | "local" -> Decode.succeed LocalScope
                | other -> Decode.fail (sprintf "Unknown secret scope: %s" other)) }

    let secretMetadata : Codec<SecretMetadata> =
        { Encode =
            fun (m: SecretMetadata) ->
                Encode.object
                    [ "scope", secretScope.Encode m.Id.Scope
                      "name", secretName.Encode m.Id.Name
                      "createdAt", Codec.timestamp.Encode m.CreatedAt
                      "updatedAt", Codec.timestamp.Encode m.UpdatedAt ]
          Decode =
            Decode.object (fun get ->
                { SecretMetadata.Id =
                    { Scope = get.Required.Field "scope" secretScope.Decode
                      Name = get.Required.Field "name" secretName.Decode }
                  SecretMetadata.CreatedAt = get.Required.Field "createdAt" Codec.timestamp.Decode
                  SecretMetadata.UpdatedAt = get.Required.Field "updatedAt" Codec.timestamp.Decode }) }

    let private secretEntry : Codec<SecretEntry> =
        { Encode =
            fun (e: SecretEntry) ->
                Encode.object
                    [ "scope", secretScope.Encode e.Scope
                      "name", secretName.Encode e.Name
                      "createdAt", Codec.timestamp.Encode e.CreatedAt
                      "updatedAt", Codec.timestamp.Encode e.UpdatedAt
                      "iv", Encode.string e.Iv
                      "ciphertext", Encode.string e.Ciphertext ]
          Decode =
            Decode.object (fun get ->
                { SecretEntry.Scope = get.Required.Field "scope" secretScope.Decode
                  SecretEntry.Name = get.Required.Field "name" secretName.Decode
                  SecretEntry.CreatedAt = get.Required.Field "createdAt" Codec.timestamp.Decode
                  SecretEntry.UpdatedAt = get.Required.Field "updatedAt" Codec.timestamp.Decode
                  SecretEntry.Iv = get.Required.Field "iv" Decode.string
                  SecretEntry.Ciphertext = get.Required.Field "ciphertext" Decode.string }) }

    let secretsFile : Codec<SecretsFile> =
        { Encode =
            fun (f: SecretsFile) ->
                Encode.object
                    [ "version", Encode.int f.Version
                      "kekId", Encode.string f.KekId
                      "entries", f.Entries |> List.map secretEntry.Encode |> Encode.list ]
          Decode =
            Decode.object (fun get ->
                { SecretsFile.Version = get.Required.Field "version" Decode.int
                  SecretsFile.KekId = get.Required.Field "kekId" Decode.string
                  SecretsFile.Entries = get.Required.Field "entries" (Decode.list secretEntry.Decode) }) }

    let toString (file: SecretsFile) : string =
        secretsFile.Encode file |> Encode.toString 2

    let fromString (json: string) : Result<SecretsFile, string> =
        Decode.fromString secretsFile.Decode json
