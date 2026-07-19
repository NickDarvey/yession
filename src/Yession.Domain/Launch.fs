namespace Yession.Domain

/// Session launch vocabulary (Step 10). The Session Manager owns process launch: a
/// Session Process is started by the Manager, never directly, establishing the authority
/// boundary later steps delegate scoped capabilities across (docs/design.md §3).

type SessionLaunchRequest =
    { SessionId : SessionId }

/// The bootstrap URI is a string for Fable portability; it is a plain
/// `http://127.0.0.1:<port>/` local URI in Phase 2.
type SessionLaunchResult =
    { SessionId : SessionId
      ProcessId : string
      LocalBootstrapUri : string }

type StartSession = SessionLaunchRequest -> Async<SessionLaunchResult>
