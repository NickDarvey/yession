module Yession.Host.KeyStore

// Where the secrets KEK lives (Plan 06): a function-shaped capability over the OS
// credential manager. The stored payload is "<kekId>:<keyB64u>" — id and key travel
// together, so the store can tell "sealed by a different key" from corruption. The
// product implementation (Keyring-backed, `detect`) arrives with the `Fable.Keyring`
// binding; `inMemory`/`random` serve tests and the ephemeral no-credential-manager
// path. There is deliberately NO plaintext-file store and NO env-var store: a host
// without a credential manager refuses persistence instead of degrading it.

type KeyStore =
    { Name : string
      /// Cheap probe: is the backing credential store actually usable here?
      Available : unit -> Async<bool>
      /// The stored KEK payload; None when absent (first run).
      Get : unit -> Async<Result<string option, string>>
      Set : string -> Async<Result<unit, string>> }

/// Compose a KEK payload / split one. The id is any opaque string without ':'.
let payload (kekId: string) (kekB64u: string) : string = kekId + ":" + kekB64u

let splitPayload (raw: string) : Result<string * string, string> =
    match raw.IndexOf ':' with
    | i when i > 0 && i < raw.Length - 1 -> Ok (raw.Substring (0, i), raw.Substring (i + 1))
    | _ -> Error "malformed KEK payload (expected <kekId>:<key>)"

/// A mutable in-memory cell: the test double, and (seeded) the ephemeral path.
let inMemory () : KeyStore =
    let mutable stored : string option = None
    { Name = "in-memory"
      Available = fun () -> async { return true }
      Get = fun () -> async { return Ok stored }
      Set = fun value -> async { stored <- Some value; return Ok () } }

/// An in-memory store pre-seeded with a fresh random KEK — the ephemeral store's key:
/// it exists nowhere but this process and dies with it.
let random () : KeyStore =
    let store = inMemory ()
    let seeded = payload (Yession.Host.Interop.randomSecret ()) (SecretsCipher.generateKek ())
    { store with Get = fun () -> async { return Ok (Some seeded) } }
