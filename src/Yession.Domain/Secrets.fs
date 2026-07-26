namespace Yession.Domain

open System

/// Secrets vocabulary (Plan 06). A secret's identity is scope + name; its value never
/// appears in this module — `SecretMetadata` is what listings serve, and by construction
/// it cannot carry a value. Deliberately no shared/global scope: a Manager-wide secret
/// would be ambient authority with no owner to authorize against (docs/GAPS.md).

/// Who a secret belongs to. `UserId`/`PeerId` live in Identity.fs — the same identities
/// events attribute to (`ActorRef`), not secrets-only concepts. A peer scope names a
/// stable browser identity (docs/plans/07): meaningful in unattributed deployments where
/// no user exists; under a real strategy, `UserScope` is the durable home.
type SecretScope =
    | SessionScope of SessionId
    | UserScope of UserId
    | PeerScope of PeerId

module SecretScope =
    /// A stable one-line rendering for logs and cipher AAD
    /// ("session:<id>" / "user:<sub>" / "peer:<id>").
    let describe (scope: SecretScope) : string =
        match scope with
        | SessionScope sessionId -> "session:" + SessionId.value sessionId
        | UserScope user -> "user:" + UserId.value user
        | PeerScope peer -> "peer:" + PeerId.value peer

/// A secret's identity: which scope owns it, and its name within that scope.
type SecretId = { Scope : SecretScope; Name : SecretName }

/// Everything a listing may reveal. There is no value field — a listing cannot leak a
/// value because the type cannot carry one.
type SecretMetadata =
    { Id : SecretId
      CreatedAt : DateTimeOffset
      UpdatedAt : DateTimeOffset }
