namespace Yession.Domain

/// Manager → Session notifications: the reverse leg of the control RPC. Everything else
/// on the control channel is the child calling the Manager (start/stop/execute/name);
/// this is the Manager pushing an out-of-band change DOWN to a running Session Process,
/// multiplexed over one stream (an SSE subscription in the wire adapter). Like the rest
/// of the control channel, NO session content travels here — only environment/lifecycle
/// signals the Manager owns and the session could not otherwise learn about.
///
/// The Session Process hands each notification to a `NotificationHandler`, which dispatches
/// it into the process loop and MAY record a durable `SessionEvent` (so the change reaches
/// clients and the agent's next context) — or re-drain, or ignore it. It is never obliged
/// to: a notification is a signal, not a durable fact.
type SessionNotification =
    /// PLACEHOLDER. `of unit` carries no payload on purpose — this case exists only so the
    /// transport, the wire codec, and the dispatch seam are real and end-to-end tested
    /// before there is a genuine signal to send. REPLACE IT (payload and this comment)
    /// with the first real notification — the motivating one is an environment change the
    /// Manager observes out-of-band, e.g. a container it owns dying without the session
    /// having stopped it.
    | EnvironmentChanged of unit

/// The Session Process's notification seam: each notification the Manager pushes is handed
/// to this handler. Fire-and-forget by shape (like the scheduler drain) — a handler that
/// needs to append an event starts that work itself. Failures are the handler's to
/// contain; a notification is best-effort and never disturbs the session.
type NotificationHandler = SessionNotification -> unit
