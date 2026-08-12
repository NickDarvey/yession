namespace Yession.Domain

// A stream a provider offers becomes a terminal (Plan 19).
//
// This is the whole of the extension point, and it is deliberately small: a tool result may
// carry a `StreamOffer`, and an offer becomes a terminal people can see and type into.
// Nothing here — and nothing anywhere else in the product — knows what is on the other end
// of the stream. A serial device, a board's console, a shell on another host and a CI runner
// are the same fact to this file: bytes, and a declaration of what else they can do.
//
// A DECORATOR over the registry rather than a branch inside the MCP client, for the reason
// `ToolUseLog` is one: every call the agent makes goes through `InvokeTool`, in-process and
// proxied alike, so a seam here cannot be bypassed by a tool that arrives later.

open System.Collections.Generic

/// What a session can do about a stream somebody offered it.
type StreamAttach =
    { /// Open a terminal over it. The title is the ticket's label.
      Open : AttachTicket -> Async<Result<TerminalId, string>>
      /// Is that terminal still open? What makes "one terminal per stream" mean the LIVE one
      /// rather than one somebody closed an hour ago.
      IsOpen : TerminalId -> bool }

module StreamAttach =

    /// A session that turns nothing into a terminal — the default, and what every
    /// composition that has not been given a terminal manager gets.
    let unavailable : StreamAttach =
        { Open = fun _ -> async { return Error "this session cannot open terminals" }
          IsOpen = fun _ -> false }

/// Session-scoped. `Decorate` is called once per turn, because the ceiling is a turn's.
type StreamAttacher =
    { Decorate : ToolRegistry -> ToolRegistry }

module ToolStreams =

    /// How many NEW terminals one turn may be given. A turn that calls a dozen tools which
    /// each offer a stream is not a turn anybody meant to have a dozen panels open, and a
    /// pane full of terminals nobody asked for is a worse failure than a refusal the model
    /// can read.
    ///
    /// Only new ones count: a provider answering its own `status` with the stream it is
    /// already serving offers the same url every time, and that costs nothing.
    let perTurnLimit = 4

    let private opened (id: TerminalId) =
        sprintf
            "Opened as terminal %s — people in this session can see it and type into it."
            (TerminalId.value id)

    /// The session's stream attacher. The url→terminal map lives here, for the SESSION,
    /// because a provider polled across two turns is offering one stream, not two.
    let create (attach: StreamAttach) : StreamAttacher =
        let live = Dictionary<string, TerminalId> ()

        /// Attach one offer. Answers what to tell the model, and whether a NEW terminal was
        /// opened — which is what the ceiling counts.
        let attachOne (offer: StreamOffer) : Async<string * bool> =
            async {
                let url = offer.Ticket.Url
                match live.TryGetValue url with
                | true, id when attach.IsOpen id -> return opened id, false
                | _ ->
                    match! attach.Open offer.Ticket with
                    | Ok id ->
                        live.[url] <- id
                        return opened id, true
                    | Error reason ->
                        // The stream is the provider's and the failure is ours, so the model
                        // is told which of the two went wrong. It may well have a tool for
                        // giving the claim back.
                        return sprintf "This session could not open the stream it was offered: %s" reason, false
            }

        let decorate (registry: ToolRegistry) : ToolRegistry =
            let mutable remaining = perTurnLimit
            { registry with
                Invoke =
                    fun call ->
                        async {
                            match! registry.Invoke call with
                            | Error e -> return Error e
                            | Ok answer ->
                                match answer.Stream with
                                | None -> return Ok answer
                                | Some offer ->
                                    if remaining <= 0 then
                                        return
                                            Ok
                                                { answer with
                                                    Text =
                                                        answer.Text
                                                        + "\n\nThis turn has already been given "
                                                        + string perTurnLimit
                                                        + " terminals, so this stream was not opened. Ask again on a later turn."
                                                    Stream = None }
                                    else
                                        let! line, isNew = attachOne offer
                                        if isNew then remaining <- remaining - 1
                                        return Ok { answer with Text = answer.Text + "\n\n" + line }
                        } }

        { Decorate = decorate }
