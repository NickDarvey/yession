namespace Yession.Domain.Repos

open Yession.Domain

/// The facts a repo records. They sit BELOW `SessionEvent` because the union names them,
/// and the projections that fold that union sit above it — so a feature spans the event
/// spine rather than living on one side of it. What this buys is that `Repo` and `Branch`
/// stop being field labels in the namespace every file opens.
type RepoAdded =
    { /// The timeline note's identity, minted by the Process at append time — which is
      /// what lets the conversation projection fold this without inventing ids.
      MessageId : MessageId
      Repo : RepoRef
      /// The branch the clone landed on (the remote's default).
      Branch : string
      /// Who brought it in — the panel's human or the agent. Carried on the payload
      /// because the projection reads events, not envelopes, and "who added this repo"
      /// is the fact the shared-trust disclosure hangs off.
      Actor : ActorRef }

and RepoRemoved =
    { MessageId : MessageId
      Repo : RepoRef
      Actor : ActorRef }

and RepoBranchSwitched =
    { MessageId : MessageId
      Repo : RepoRef
      Branch : string
      /// True when the switch created the branch (`-b`), false when it checked out an
      /// existing one — one event, because it is one act at the panel and one verb.
      Created : bool
      Actor : ActorRef }

/// A repo's `yession.yaml` asked this session for something, and the session did not do it.
///
/// The `repo_config` query already answers "what became of every declaration" — but only to
/// somebody who thought to ask, and a person who has just broken their own file has no
/// reason to suspect there is a question. A start that SUCCEEDS has always announced itself
/// (`WorkSandboxStarted`); this is the missing half, so that the two outcomes of a
/// declaration are visible in the same place rather than one on the timeline and one behind
/// a query.
///
/// Recorded on CHANGE and never per fold. The fold re-runs after every repo verb, so a note
/// per outcome would rebuild exactly the accumulation `SessionEnvironment` had to stop —
/// which is why the query was chosen over notes in the first place, and why this is a delta
/// rather than a reversal of that choice.
and RepoConfigRefused =
    { MessageId : MessageId
      Repo : RepoRef
      /// Which declaration. `None` is the FILE itself — it could not be read at all, so
      /// there is no sandbox to name, and the fix is in the YAML rather than in what it
      /// asked for.
      Sandbox : SandboxRef option
      /// Said whole, in the words the refusal already used. A note that summarised would be
      /// a second copy of a sentence the query is also showing, free to disagree with it.
      Reason : string
      /// The repo's file, as the party that asked (`ActorRef.Configured`) — the same
      /// attribution its successful starts carry.
      Actor : ActorRef }
