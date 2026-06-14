namespace Yession.Domain

/// Collaborative session state shapes shared by the Session Process and the Browser
/// Client. These are the model shapes only; the Yjs/Ylmish encoding that keeps them in
/// sync arrives in Step 05. Kept in the shared domain so both peers project the same
/// vocabulary. See docs/design.md §2.2 and docs/plans/00-init/02-*.

type DraftStatus =
    | Active
    | Sending
    | Sent

type DraftState =
    { DraftId : DraftId
      Author  : PeerId
      Body    : string
      Status  : DraftStatus }

type SharedBrief = { Body : string }

/// Collaborative state synced via Ylmish in Step 05.
type SyncedSessionState =
    { Drafts      : Map<DraftId, DraftState>
      SharedBrief : SharedBrief option }

module SyncedSessionState =

    /// Nothing synced yet: no drafts, no shared brief.
    let empty : SyncedSessionState = { Drafts = Map.empty; SharedBrief = None }
