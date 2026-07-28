namespace Yession.Domain

/// The ABAC vocabulary (Plan 06): authorization decisions as one pure, total,
/// default-deny function over attributes of the subject, the action, and the resource.
/// Subjects are built ONLY by the Manager from state it verified itself (the per-launch
/// control secret, and the users it bound at ID-token issuance) — never from request
/// content, so the composite session+user identity is never self-asserted. Actions and
/// resources are unions: the next Manager-owned resource adds cases, not mechanisms.

/// Manager-verified caller attributes.
type AuthzSubject =
    { /// The launch making the call. None for future non-session callers (e.g. a
      /// logged-in management UI acting directly for a user).
      Session : SessionId option
      /// Users the Manager bound to that launch at ID-token issuance.
      Users : Set<UserId>
      /// Peers the Manager witnessed into that launch at ID-token issuance (the
      /// browser's peer id rides the authorize bounce, docs/plans/07). Like Users,
      /// recorded only by the Manager — never from request content.
      Peers : Set<PeerId> }

type SecretAction =
    /// Metadata only — names, scopes, timestamps. Never values.
    | ListSecrets
    | SetSecret
    | DeleteSecret
    /// Resolve a value into a launched environment's env vars. Manager-internal: the
    /// value terminates in the container environment, never on the control channel.
    /// This is the ONLY read the policy knows — there is no value-returning route.
    | InjectSecret

type AuthzResource =
    | SecretResource of SecretId
    | SecretCollection of SecretScope

/// Actions on connection credentials (Plan 08) — the Manager-brokered, owner- or
/// session-scoped external-service credentials. A separate family from `SecretAction`
/// because its rules differ on purpose: sessions may WRITE owner-scoped connection
/// credentials for identities bound to them (the sign-in flow is exactly that write),
/// and `ResolveCredential` RETURNS a value to the session (an agent turn needs the
/// token in-process, unlike container env injection). Generic secrets stay write-only
/// and user-scope-read-only; nothing here widens their rules.
type ConnectionAction =
    /// Begin/complete a brokered flow or store a pasted token — the narrow write.
    | ConnectCredential
    /// Metadata only — kind and timestamps, never values.
    | ReadConnectionStatus
    /// Release the credential's current value to the caller for one agent turn.
    | ResolveCredential
    | DisconnectCredential

type AuthzAction =
    | SecretAction of SecretAction
    | ConnectionAction of ConnectionAction

type AuthzRequest =
    { Subject : AuthzSubject
      Action : AuthzAction
      Resource : AuthzResource }

type Decision =
    | Permit
    /// Operator-facing reason (403 body, Manager log). Never echoes values.
    | Deny of reason: string

module Policy =

    /// The v1 policy. A session owns its session-scoped secrets outright; user-scoped
    /// secrets are listable/injectable by a session a bound user signed into, and never
    /// writable by sessions (the user surface is the recorded follow-up); peer-scoped
    /// secrets (docs/plans/07) are fully managed by a session the Manager witnessed
    /// that peer into — a peer that never completed a sign-in bounce holds nothing.
    /// Anything not explicitly permitted is denied.
    let authorize (request: AuthzRequest) : Decision =
        let ownSession (owner: SessionId) =
            match request.Subject.Session with
            | Some caller when caller = owner -> Permit
            | _ -> Deny "not the owning session"
        let boundUser (user: UserId) =
            if Set.contains user request.Subject.Users then Permit
            else Deny "user is not signed in to this session"
        let witnessedPeer (peer: PeerId) =
            if Set.contains peer request.Subject.Peers then Permit
            else Deny "peer is not signed in to this session"
        match request.Action, request.Resource with
        // Connection credentials (Plan 08): every action — including the write — is
        // permitted exactly where the caller IS the scope's owner: its own session
        // scope, a user the Manager bound to it, a peer it witnessed. That makes an
        // owner-scoped sign-in usable (and replaceable) from any session that owner is
        // signed into, and a session-scoped one from only that session.
        | ConnectionAction _, SecretResource { Scope = SessionScope owner } ->
            ownSession owner
        | ConnectionAction _, SecretResource { Scope = UserScope user } ->
            boundUser user
        | ConnectionAction _, SecretResource { Scope = PeerScope peer } ->
            witnessedPeer peer
        | SecretAction (SetSecret | DeleteSecret | InjectSecret), SecretResource { Scope = SessionScope owner } ->
            ownSession owner
        | SecretAction ListSecrets, SecretCollection (SessionScope owner) ->
            ownSession owner
        | SecretAction InjectSecret, SecretResource { Scope = UserScope user } ->
            boundUser user
        | SecretAction ListSecrets, SecretCollection (UserScope user) ->
            boundUser user
        | SecretAction (SetSecret | DeleteSecret), SecretResource { Scope = UserScope _ } ->
            Deny "user-scoped secrets are managed by the user, not sessions"
        | SecretAction (SetSecret | DeleteSecret | InjectSecret), SecretResource { Scope = PeerScope peer } ->
            witnessedPeer peer
        | SecretAction ListSecrets, SecretCollection (PeerScope peer) ->
            witnessedPeer peer
        | _ ->
            Deny "no rule permits this request"
