namespace Yession.Client

open Yession.Domain

/// Pure rendering of the client shell to HTML. The view is a total function of the model,
/// so it is identical whether produced by the Session Process for the static bootstrap or
/// by the browser as the model updates. Step 04 renders the connection status, the local
/// display name, and the offset / catch-up indicators (offsets are a core product
/// invariant, not a debug detail); the draft editor, send button, conversation timeline,
/// and agent stream are placeholders filled by later steps.
module View =

    let private connectionLabel =
        function
        | Disconnected -> "Disconnected"
        | Connecting -> "Connecting"
        | Connected -> "Connected"
        | Reconnecting -> "Reconnecting"

    let private offsetText =
        function
        | Some offset -> string (EventOffset.value offset)
        | None -> "—"

    let private catchUpText (consumer: EventConsumerState) =
        if consumer.IsCatchingUp then "Catching up" else "Up to date"

    /// Render the client shell as an HTML fragment (the contents of `#app`).
    let render (model: ClientModel) : string =
        let consumer = model.EventConsumer
        String.concat "" [
            "<section class=\"connection\">"
            sprintf "<span class=\"status status-%s\" data-connection>%s</span>"
                (connectionLabel model.Connection |> fun s -> s.ToLowerInvariant())
                (connectionLabel model.Connection)
            sprintf "<span class=\"peer\" data-display-name>%s</span>" model.Peer.DisplayName
            "</section>"
            "<section class=\"offsets\">"
            sprintf "<span class=\"offset offset-processed\" data-last-processed-offset>%s</span>"
                (offsetText consumer.LastProcessedOffset)
            sprintf "<span class=\"offset offset-latest\" data-latest-known-offset>%s</span>"
                (offsetText consumer.LatestKnownOffset)
            sprintf "<span class=\"catch-up\" data-catch-up>%s</span>" (catchUpText consumer)
            "</section>"
            // Placeholders filled by later steps (05 draft editor + send, 07 timeline, 08 agent).
            "<section class=\"draft\" data-draft-editor></section>"
            "<section class=\"timeline\" data-conversation></section>"
            "<section class=\"agent\" data-agent-stream></section>"
        ]

    /// Render a full HTML document hosting the client shell. Used by the Session Process
    /// static bootstrap so the served page *is* the client shell.
    let page (model: ClientModel) : string =
        String.concat "" [
            "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">"
            "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
            "<title>Yession</title></head><body>"
            sprintf "<main id=\"app\">%s</main>" (render model)
            "</body></html>"
        ]
