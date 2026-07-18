namespace Yession.Domain

/// MCP (Model Context Protocol) tool descriptors, in the standard `Tool` / `ListToolsResult`
/// shapes — so a Session Process can hand what it receives straight to an MCP client without
/// reshaping. Carried Manager→Session over the `/control/mcp` SSE stream (a second reverse leg
/// of the control RPC, alongside `/control/notifications`): the current list arrives on
/// subscribe, then a fresh full list whenever it changes. That mirrors MCP's own model, where
/// a `notifications/tools/list_changed` prompts the client to re-fetch the whole `tools/list` —
/// here the Manager just pushes the new list rather than pinging for a re-fetch.
type McpTool =
    { /// The tool's unique name — the MCP call target (`Tool.name`).
      Name : string
      /// Human-readable title (`Tool.title`), when the server provides one.
      Title : string option
      /// Human-readable description (`Tool.description`).
      Description : string option
      /// The tool's input JSON Schema — verbatim MCP `Tool.inputSchema` (a JSON object). Kept
      /// as raw JSON text so any schema passes the boundary losslessly; the wire codec embeds
      /// it as a real JSON object, not a quoted string.
      InputSchema : string }

/// The MCP tool list — the standard `ListToolsResult` (its `tools` array). One of these is the
/// payload of every `/control/mcp` frame. Pagination (`nextCursor`) is intentionally omitted:
/// the stream pushes the whole list each time, so there is nothing to page through.
type McpToolList = { Tools : McpTool list }

module McpToolList =
    let empty : McpToolList = { Tools = [] }
