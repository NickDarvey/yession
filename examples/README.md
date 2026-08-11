# Examples

Integrations, built the way somebody outside this repository would build them.

Everything here is **standalone**. An example references nothing from `Yession.Domain` or
`Yession.Host`, has its own project and its own bundle, and is not carried by the npm package
or the Nix installable. That separation is the whole point: an example that quietly reaches
into the product's internals demonstrates a path only this repository can walk.

Build one from the repository root:

```bash
dotnet fsi tasks.fsx example serial
```

| | |
|---|---|
| [serial](serial/) | An MCP server that lends the host's serial devices to an agent. Shows the shape of a provider that owns a **resource**: arbitration, a session-scoped claim, and a second transport for the bytes a tool call cannot carry. |

## Why a provider rather than a feature

Yession declares MCP servers by url, and a session's own MCP client does the rest. Nothing in
the product knows what serial is — which means a provider is not a plugin with a blessed
interface to conform to. It is a server. If your integration can answer `initialize`,
`tools/list` and `tools/call` over HTTP, it is already declarable.

The interesting design questions are therefore not about Yession at all. They are the ones the
serial example works through: who owns a resource the OS hands out exactly once, what happens
to that claim when the client crashes, and how you carry a stream when your protocol is
request/response.
