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
      Users : Set<UserSubject> }

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

type AuthzAction =
    | SecretAction of SecretAction

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
    /// writable by sessions (the user surface is the recorded follow-up). Anything not
    /// explicitly permitted is denied.
    let authorize (request: AuthzRequest) : Decision =
        let ownSession (owner: SessionId) =
            match request.Subject.Session with
            | Some caller when caller = owner -> Permit
            | _ -> Deny "not the owning session"
        let boundUser (user: UserSubject) =
            if Set.contains user request.Subject.Users then Permit
            else Deny "user is not signed in to this session"
        match request.Action, request.Resource with
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
        | _ ->
            Deny "no rule permits this request"
