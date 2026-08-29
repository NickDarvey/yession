namespace Yession.Domain

/// Manager → Session notifications: the reverse leg of the control RPC. Everything else
/// on the control channel is the child calling the Manager (start/stop/execute/name);
/// this is the Manager pushing an out-of-band change DOWN to a running Session Process,
/// multiplexed over one stream (an SSE subscription in the wire adapter). Like the rest
/// of the control channel, NO session content travels here — only what the Manager owns
/// or receives on the session's behalf and the session could not otherwise learn about.
///
/// The Session Process hands each notification to a `NotificationHandler`, which dispatches
/// it into the process loop and MAY record a durable `SessionEvent` (so the change reaches
/// clients and the agent's next context) — or re-drain, or ignore it. It is never obliged
/// to: a notification is a signal, not a durable fact.
type SessionNotification =
    /// A hook delivery the Manager verified and matched against a filter this session
    /// declared (`Yession.Domain.Hooks`). The first real notification, and the one the
    /// `EnvironmentChanged of unit` placeholder that stood here was waiting for.
    ///
    /// The Manager forwards it without having read it: it checked the signature over the
    /// bytes, resolved the paths the session's own filter named, and passed the delivery
    /// on. `Endpoint` is which of its hook endpoints took it in — the unit an operator
    /// configures a secret and a signature scheme for — and `Subscription` is which of
    /// this session's filters matched, so the session dispatches without matching again.
    ///
    /// Headers ride with the body because a delivery IS both: the event type lives in a
    /// header for most providers, so a relay that dropped them would be lossy about the
    /// message rather than tidy about metadata.
    | WebhookDelivered of
        subscription: string *
        endpoint: string *
        headers: (string * string) list *
        body: string

/// The Session Process's notification seam: each notification the Manager pushes is handed
/// to this handler. Fire-and-forget by shape (like the scheduler drain) — a handler that
/// needs to append an event starts that work itself. Failures are the handler's to
/// contain; a notification is best-effort and never disturbs the session.
type NotificationHandler = SessionNotification -> unit
