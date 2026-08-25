namespace Yession.Domain.Sandboxes

open Yession.Domain

/// The facts a sandbox records — an environment identified, started or stopped, a work sandbox's life, and the shell profile set on it.
///
/// These sit BELOW `SessionEvent`, because the union names them, while the
/// projections that fold that union sit above it — so Sandboxes spans the event
/// spine rather than living on one side of it.
[<RequireQualifiedAccess>]
type EnvironmentNeedIdentified =
    { Reason : string
      AgentTurnId : AgentTurnId option }

and EnvironmentStartRequested =
    { EnvironmentId : string
      SpecSummary : string }

and EnvironmentStarted =
    { EnvironmentId : string
      ContainerRef : string }

and EnvironmentStartFailed =
    { EnvironmentId : string
      Reason : string }

and [<RequireQualifiedAccess>] EnvironmentStopRequested =
    { EnvironmentId : string }

and [<RequireQualifiedAccess>] EnvironmentStopped =
    { EnvironmentId : string }

and WorkSandboxStarted =
    { MessageId : MessageId
      /// Which sandbox, scope included. The wire form is `SandboxRef.render`, and it is
      /// backward compatible BY CONSTRUCTION rather than by a migration: a session-owned ref
      /// renders to the bare name every log already holds, and `parse` reads a bare name back
      /// as session-owned. A log written before repos could declare sandboxes genuinely had
      /// only session-owned ones, so that is not a guess.
      Sandbox : SandboxRef
      /// Which backend it came up on, so the record says what confinement it actually
      /// got rather than what the operator configured at some point.
      Backend : string
      /// The credential NAMES forwarded into it — never a value, and never a token
      /// shape that could be mistaken for one. Forwarding is a fact about the sandbox
      /// that outlives the turn that asked for it, so the log has to carry it; what the
      /// credential IS belongs only in the sandbox's env.
      Forwarded : string list
      /// Whose credentials were forwarded. Distinct from `Actor` on purpose: for an
      /// agent-issued start the AGENT is the acting party while the credentials are the
      /// turn human's (Plan 08 — no borrowing, and the agent has no scope of its own).
      /// `None` when nothing was forwarded, because then nobody's were.
      CredentialOwner : ActorRef option
      Actor : ActorRef }

and WorkSandboxStopped =
    { MessageId : MessageId
      Sandbox : SandboxRef
      Actor : ActorRef }

and ShellProfileSet =
    { MessageId : MessageId
      /// Which sandbox's shells this is about. A path is only a path inside the filesystem
      /// that has it, so the profile is per sandbox rather than per session — and a repo's
      /// sandbox is a sandbox, so this addresses one the same way everything else does.
      Sandbox : SandboxRef
      /// Where a shell opened in that sandbox starts. `None` is the CLEAR — back to the
      /// sandbox's own default, which is what every terminal did before this plan.
      WorkingDirectory : string option
      Actor : ActorRef }
