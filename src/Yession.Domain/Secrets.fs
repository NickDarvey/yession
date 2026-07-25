namespace Yession.Domain

open System

/// Secrets vocabulary (Plan 06). A secret's identity is scope + name; its value never
/// appears in this module — `SecretMetadata` is what listings serve, and by construction
/// it cannot carry a value. Deliberately no shared/global scope: a Manager-wide secret
/// would be ambient authority with no owner to authorize against (docs/GAPS.md).

/// An authenticated user subject — the OIDC `sub` claim the Manager itself issued
/// (docs/plans/04-session-authorization.md). Under the shipped localhost strategy this
/// is the single "local" user; upstream strategies mint real subjects.
type UserSubject = private UserSubject of string

module UserSubject =
    let create (raw: string) : Result<UserSubject, string> =
        if String.IsNullOrWhiteSpace raw then Error "UserSubject cannot be blank"
        else Ok (UserSubject (raw.Trim ()))
    let value (UserSubject s) = s

/// Who a secret belongs to.
type SecretScope =
    | SessionScope of SessionId
    | UserScope of UserSubject

module SecretScope =
    /// A stable one-line rendering for logs and cipher AAD ("session:<id>" / "user:<sub>").
    let describe (scope: SecretScope) : string =
        match scope with
        | SessionScope sessionId -> "session:" + SessionId.value sessionId
        | UserScope user -> "user:" + UserSubject.value user

/// A secret's identity: which scope owns it, and its name within that scope.
type SecretId = { Scope : SecretScope; Name : SecretName }

/// Everything a listing may reveal. There is no value field — a listing cannot leak a
/// value because the type cannot carry one.
type SecretMetadata =
    { Id : SecretId
      CreatedAt : DateTimeOffset
      UpdatedAt : DateTimeOffset }
