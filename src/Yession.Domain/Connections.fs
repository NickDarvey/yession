namespace Yession.Domain

open System

/// Connection-credential vocabulary (Plan 08). A "connection" is an external-service
/// credential a human established from inside a session — brokered by the Manager as
/// pure OAuth standards (or pasted as a static token), stored in the encrypted secret
/// store, and resolved back to sessions one value at a time. The Manager never learns
/// WHICH service a credential belongs to: the session chooses the storage name and
/// supplies provider endpoints as data; everything here is service-agnostic.

/// Who a connection credential belongs to when it is signed in for "all my sessions".
/// Still NO anonymous case: every owner here is one the Manager verified. An attributed
/// deployment owns by user; an unattributed one owns by `LocalOwner` — not a pseudo-user
/// invented for the occasion, but the single principal `--auth localhost` actually grants,
/// authorized against the unattributed access the Manager recorded for that launch.
///
/// It is deliberately NOT the browser peer. A peer id lives in origin-partitioned
/// localStorage, so it changes under the person holding it — a new origin, a cleared
/// store, a second device — and each new id used to strand the credential behind it. An
/// owner has to outlive the browser that named it.
type CredentialOwner =
    | UserOwner of UserId
    | LocalOwner

module CredentialOwner =

    /// The secret-store scope an owner's credentials live under.
    let scope (owner: CredentialOwner) : SecretScope =
        match owner with
        | UserOwner user -> UserScope user
        | LocalOwner -> LocalScope

    /// The owner an event/turn actor holds credentials as. Only an attributed actor names
    /// one: a `PeerRef` author is by definition someone nobody verified, so they own
    /// nothing of their own — under an unattributed deployment their turn falls through to
    /// `LocalScope`, which the Manager grants that launch, and under an attributed one it
    /// falls through to nothing at all. `None` for the non-human actors — an agent or
    /// process can never own a connection credential.
    let ofActor (actor: ActorRef) : CredentialOwner option =
        match actor with
        | UserRef user -> Some (UserOwner user)
        | PeerRef _ -> None
        | ActorRef.Agent | SessionProcess | System -> None

    /// A stable one-line rendering for logs ("user:<sub>" / "local"). Never a value.
    let describe (owner: CredentialOwner) : string =
        SecretScope.describe (scope owner)

/// How a stored connection credential behaves — status vocabulary, value-free.
/// `OAuthConnection` = brokered tokens the Manager can refresh; `StaticConnection` = a
/// pasted token/key returned verbatim.
type ConnectionKind =
    | OAuthConnection
    | StaticConnection

/// One stored connection as listings and the status stream see it. There is no value
/// field — a status cannot leak a credential because the type cannot carry one.
type ConnectionStatus =
    { Id : SecretId
      Kind : ConnectionKind
      UpdatedAt : DateTimeOffset }

/// The status-stream frame: every connection the receiving launch may currently read.
type ConnectionStatusList = { Connections : ConnectionStatus list }
