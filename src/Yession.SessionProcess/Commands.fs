namespace Yession.SessionProcess

open Yession.Domain

/// Handling of `SessionCommand` requests on the Session Process (Step 06). Commands are
/// how clients ask for durable facts: the Process reads the current synced state,
/// decides, and appends — it is the only event writer (docs/design.md §1).
module SessionCommands =

    /// Handle one command from an accepted peer.
    ///
    /// `SendDraft` snapshots the draft's body from the synced state *at send time* and
    /// appends `MessageSent`; later draft edits never touch the sent message. Unknown or
    /// already-sent drafts are rejected. Dependencies are injected so the decision logic
    /// is testable without a doc or a transport: `readSynced` is the Process's view of
    /// the synced session state, `mintMessageId` supplies fresh message identities.
    let handle
        (readSynced: unit -> Result<SyncedSessionState, string>)
        (log: EventLog<SessionEvent>)
        (mintMessageId: unit -> MessageId)
        (peerId: PeerId)
        (command: SessionCommand)
        : Async<SessionCommandResult> =
        async {
            match command with
            | StartDraft ->
                // Drafts are created in the shared synced state (Step 05), not by command.
                return CommandRejected "drafts are started in the shared session state"
            | SendDraft draftId ->
                match readSynced () with
                | Error e -> return CommandRejected (sprintf "synced state unavailable: %s" e)
                | Ok synced ->
                    match Map.tryFind draftId synced.Drafts with
                    | None -> return CommandRejected "unknown draft"
                    | Some draft when draft.Status = Sent -> return CommandRejected "draft already sent"
                    | Some draft ->
                        let actor = HumanPeer peerId
                        let message =
                            { MessageId = mintMessageId ()
                              DraftId = Some draftId
                              Author = actor
                              Body = Ylmish.Text.toString draft.Body }
                        let! _ = log.Append actor (MessageSent message)
                        return CommandAccepted
        }
