namespace Yession.Domain

open System

/// Connection-credential vocabulary (Plan 08). A "connection" is an external-service
/// credential a human established from inside a session — brokered by the Manager as
/// pure OAuth standards (or pasted as a static token), stored in the encrypted secret
/// store, and resolved back to sessions one value at a time. The Manager never learns
/// WHICH service a credential belongs to: the session chooses the storage name and
/// supplies provider endpoints as data; everything here is service-agnostic.

/// Who a connection credential belongs to when it is signed in for "all my sessions":
/// exactly the user-or-peer split `ActorRef` and `SecretScope` already carry, made
/// first-class. Deliberately NO anonymous case — an attributed deployment never handles
/// one, and an unattributed (localhost) deployment owns credentials by witnessed peer,
/// not by a pseudo-user.
type CredentialOwner =
    | UserOwner of UserId
    | PeerOwner of PeerId

module CredentialOwner =

    /// The secret-store scope an owner's credentials live under.
    let scope (owner: CredentialOwner) : SecretScope =
        match owner with
        | UserOwner user -> UserScope user
        | PeerOwner peer -> PeerScope peer

    /// The owner behind an event/turn actor. `None` for the non-human actors — an agent
    /// or process can never own a connection credential.
    let ofActor (actor: ActorRef) : CredentialOwner option =
        match actor with
        | UserRef user -> Some (UserOwner user)
        | PeerRef peer -> Some (PeerOwner peer)
        | ActorRef.Agent | SessionProcess | System -> None

    /// A stable one-line rendering for logs ("user:<sub>" / "peer:<id>"). Never a value.
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
