module Yession.Host.SecretsCipher

// AES-256-GCM over Node's built-in WebCrypto (Plan 06). The key is imported with
// `extractable = false` — after import no code path can serialize it (`exportKey`
// rejects; pinned by test) — the same discipline ManagerOidc applies to the OIDC
// signing key. WebCrypto alone cannot PERSIST such a key (it has no OS keystore;
// non-extractability forbids serialization by design), which is exactly why the raw
// key bytes live in the OS credential manager (KeyStore) and are re-imported each
// start. Every encryption takes an AAD string binding the ciphertext to its owning
// entry's identity, so a ciphertext transplanted onto another entry fails GCM
// authentication.

open Fable.Core
open Yession.Domain

/// An imported, non-extractable AES-GCM key plus the operations it supports.
type Cipher =
    { /// aad -> plaintext -> (ivB64u, ciphertextB64u); fresh random 96-bit IV per call.
      Encrypt : string -> string -> Async<string * string>
      /// aad -> ivB64u -> ciphertextB64u -> plaintext. Error on GCM authentication
      /// failure: tampered ciphertext, transplanted entry, or the wrong KEK.
      Decrypt : string -> string -> string -> Async<Result<string, string>>
      /// The imported CryptoKey — the non-extractability pin surface for tests.
      Key : obj }

/// 32 random bytes, base64url — a fresh KEK (created once per store on first run, or
/// per boot for the ephemeral store).
[<Emit("Buffer.from(crypto.getRandomValues(new Uint8Array(32))).toString('base64url')")>]
let generateKek () : string = jsNative

[<Emit("crypto.subtle.importKey('raw', Buffer.from($0, 'base64url'), { name: 'AES-GCM' }, false, ['encrypt', 'decrypt'])")>]
let private importRaw (kekB64u: string) : JS.Promise<obj> = jsNative

[<Emit("""(async (key, aad, plaintext) => {
    const iv = crypto.getRandomValues(new Uint8Array(12));
    const ct = await crypto.subtle.encrypt(
        { name: 'AES-GCM', iv, additionalData: Buffer.from(aad, 'utf8') },
        key, Buffer.from(plaintext, 'utf8'));
    return [Buffer.from(iv).toString('base64url'), Buffer.from(ct).toString('base64url')];
})($0, $1, $2)""")>]
let private encryptImpl (key: obj) (aad: string) (plaintext: string) : JS.Promise<string * string> = jsNative

[<Emit("""crypto.subtle.decrypt(
    { name: 'AES-GCM', iv: Buffer.from($2, 'base64url'), additionalData: Buffer.from($1, 'utf8') },
    $0, Buffer.from($3, 'base64url')).then(pt => Buffer.from(pt).toString('utf8'))""")>]
let private decryptImpl (key: obj) (aad: string) (ivB64u: string) (ciphertextB64u: string) : JS.Promise<string> = jsNative

/// The AAD binding a ciphertext to the entry it encrypts: version + scope + name.
let aadFor (scope: SecretScope) (name: SecretName) : string =
    sprintf "yession-secret:1:%s:%s" (SecretScope.describe scope) (SecretName.value name)

/// Import a raw KEK as a NON-EXTRACTABLE AES-GCM key and return the cipher over it.
let importKey (kekB64u: string) : Async<Cipher> =
    async {
        let! key = importRaw kekB64u |> Interop.awaitPromise
        return
            { Encrypt = fun aad plaintext -> encryptImpl key aad plaintext |> Interop.awaitPromise
              Decrypt =
                fun aad iv ciphertext ->
                    async {
                        try
                            let! plaintext = decryptImpl key aad iv ciphertext |> Interop.awaitPromise
                            return Ok plaintext
                        with _ ->
                            return Error "decryption failed (tampered, transplanted, or sealed by a different key)"
                    }
              Key = key }
    }
