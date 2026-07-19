namespace Yession.SessionProcess

// Pure per-session authorization state (the RP side's bookkeeping): pending logins
// (OAuth `state` → PKCE verifier), established cookie sessions, and minted peer tokens.
// Randomness and the clock are injected, so the cheap test tier covers the lifecycle
// (single-use, expiry) deterministically. Everything is in-memory and dies with the
// session process — matching the cookies (no Max-Age) and the Manager's own state.

/// Logins that have been redirected to the provider and not yet called back.
/// Single-use and short-lived: a state is consumed on first take, and an abandoned
/// login expires after five minutes.
type PendingLogins (nowUnix: unit -> int64) =
    let lifetimeSeconds = 300L
    let mutable pending : Map<string, string * int64> = Map.empty

    member _.Add (state: string) (verifier: string) : unit =
        pending <- Map.add state (verifier, nowUnix ()) pending

    member _.Take (state: string) : string option =
        match Map.tryFind state pending with
        | None -> None
        | Some (verifier, issuedAt) ->
            pending <- Map.remove state pending
            if nowUnix () - issuedAt > lifetimeSeconds then None else Some verifier

/// Established browser sessions: opaque cookie value → authenticated subject.
type CookieSessions (mint: unit -> string) =
    let mutable sessions : Map<string, string> = Map.empty

    member _.Mint (subject: string) : string =
        let value = mint ()
        sessions <- Map.add value subject sessions
        value

    member _.SubjectOf (value: string) : string option =
        Map.tryFind value sessions

/// Peer tokens: minted for an authenticated browser (via `/me`) and presented in
/// `PeerHello` over the data channel — cookies cannot ride WebRTC, so the session
/// mints its own bearer for that hop. Validation is set membership: a token is valid
/// iff this process minted it.
type PeerTokens (mint: unit -> string) =
    let mutable tokens : Set<string> = Set.empty

    member _.Mint () : string =
        let token = mint ()
        tokens <- Set.add token tokens
        token

    member _.Validate (token: string) : bool =
        Set.contains token tokens
