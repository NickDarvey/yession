module Fable.Jose

// Fable bindings to the `jose` npm package — the de-facto standard JavaScript JWT/JWKS
// library, built on WebCrypto. This is the binding layer only, mirroring how
// `Fable.Dockerode` wraps dockerode: it declares the slice of jose's surface the OIDC
// provider uses and nothing more.
//
// Keys are WebCrypto `CryptoKey` objects. Generated with `extractable = false`, the
// private key cannot be exported by ANY code path — `exportJWK`/`crypto.subtle.exportKey`
// throw — so it exists only in process memory and dies with the process. That is an
// API-level guarantee (WebCrypto), not memory encryption.
//
// Fable-only: `dotnet build` type-checks it; Fable emits the JS that runs on Node.

open Fable.Core

/// An opaque WebCrypto CryptoKey (public or private half of a keypair).
type [<AllowNullLiteral>] CryptoKey =
    abstract ``type`` : string
    abstract extractable : bool

type [<AllowNullLiteral>] KeyPair =
    abstract publicKey : CryptoKey
    abstract privateKey : CryptoKey

/// Generate a keypair for the given JWS algorithm (e.g. "EdDSA" → Ed25519).
/// Pass `{ extractable = false }` to make the PRIVATE key non-exportable; the public key
/// stays exportable regardless (it must be, for JWKS).
[<Import("generateKeyPair", "jose")>]
let generateKeyPair (alg: string) (options: obj) : JS.Promise<KeyPair> = jsNative

/// Export a key as a JWK object. Throws on a non-extractable private key — which is the
/// invariant the provider relies on (only the public key can ever reach `/jwks`).
[<Import("exportJWK", "jose")>]
let exportJWK (key: CryptoKey) : JS.Promise<obj> = jsNative

/// The `new SignJWT(payload)` builder: chain claim setters, then sign with a private key.
type [<AllowNullLiteral>] SignJwt =
    abstract setProtectedHeader : obj -> SignJwt
    abstract setIssuer : string -> SignJwt
    abstract setSubject : string -> SignJwt
    abstract setAudience : string -> SignJwt
    abstract setIssuedAt : unit -> SignJwt
    /// e.g. "10m" — relative expiry.
    abstract setExpirationTime : string -> SignJwt
    abstract sign : CryptoKey -> JS.Promise<string>

[<Import("SignJWT", "jose")>]
let private signJwtCtor : obj = jsNative

[<Emit("new $0($1)")>]
let private construct (ctor: obj) (arg: obj) : 'a = jsNative

/// Start a signing builder over the given payload claims (plain JS object).
let signJwt (payload: obj) : SignJwt = construct signJwtCtor payload

type [<AllowNullLiteral>] JwtVerifyResult =
    abstract payload : obj
    abstract protectedHeader : obj

/// Verify a JWT's signature and standard claims. `keyOrSet` is a CryptoKey or a JWK-set
/// resolver from `createLocalJWKSet`; `options` carries expected `issuer`/`audience`.
/// Rejects (promise) on any failure.
[<Import("jwtVerify", "jose")>]
let jwtVerify (jwt: string) (keyOrSet: obj) (options: obj) : JS.Promise<JwtVerifyResult> = jsNative

/// Build a key resolver over a `{ keys: [...] }` JWKS document.
[<Import("createLocalJWKSet", "jose")>]
let createLocalJWKSet (jwks: obj) : obj = jsNative
