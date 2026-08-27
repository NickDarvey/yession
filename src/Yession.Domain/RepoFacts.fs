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
/// What a repo's file asks this session for, in the words a person would be shown, recorded
/// the first time it says something new.
///
/// Recorded on CHANGE and never per fold — the fold re-runs after every repo verb. What
/// makes a change worth saying is that a checkout's capability set is authored by whoever can
/// push to it: a `uses:` line added in a pull request takes effect the next time anybody
/// touches a repo, and until now it did so without anybody being told.
///
/// The GRANTS are carried rather than a digest of them, and that is deliberate. A hash would
/// be smaller and would make the log unreadable — a person auditing this cannot tell what
/// `a4f2…` meant — and a short one could be collided by whoever authors the file, which is
/// exactly the party this exists to watch. Carrying the list costs a few lines and cannot be
/// forged into looking unchanged.
and RepoCapabilitiesChanged =
    { MessageId : MessageId
      Repo : RepoRef
      /// Everything this repo's sandboxes would hold, flattened, deduplicated and sorted —
      /// the same rendering the operator's `resources` surface uses, so what is said here and
      /// what is read there cannot become two answers.
      Granted : string list
      /// The repo's file, as the party asking.
      Actor : ActorRef }

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
