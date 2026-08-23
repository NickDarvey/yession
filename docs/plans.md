# Plans

The plans were working documents: one per piece of work, written before it, revised during it,
and carrying the argument for the shape that landed. They are gone — each one's rationale now
lives beside the code it governs, which is where it goes red when it stops being true.

What survives is the citation. Roughly a thousand comments and tests date a decision by naming
its plan — `(Plan 13, stage 2e)`, `(Plan 20, stage 2)` — and those tags are the only remaining
way to tell which decisions were taken together. This page is what they resolve against.

## Reused numbers

Numbers were assigned per track, not globally, so **seven of them name more than one plan**. A
citation of one of these numbers is ambiguous on its own; the SUBJECT of the comment resolves
it, and nothing else does. Not the title, either — several plans never spelled their own number
in their heading, and were cited by it anyway (`Plan 04` in `Identity.fs` is session
authorization, `Plan 04` in `Agent.fs` is telemetry).

| # | Names | Told apart by |
|---|---|---|
| 02 | 2 | the Manager process vs. the client's Metro/Zune styling |
| 03 | 3 | docker integration tests vs. one draft per client vs. rich text editing |
| 04 | 3 | rich-text test cleanup vs. session authorization vs. telemetry |
| 14 | 2 | git repos vs. terminal replay in chat |
| 20 | 2 | collaborative terminals vs. the offline conversation cache |
| 24 | 2 | credential expiry vs. sandbox read scope |
| 25 | 3 | manager/session UX vs. shell profile vs. terminal history |

## The plans

| # | Plan | What it decided |
|---|---|---|
| 01 | Collaborative message queue | The queue is collaborative state: edit and reorder until the agent takes it. |
| 02 | The Manager as its own process | Split the Manager out of the session bin; it launches and supervises. |
| 02 | Metro / Zune styling for the client shell | The client's visual language. |
| 03 | Docker backend integration tests | Mounts, build specs and refs in `EnvironmentSpec` exercised in the verify gate. |
| 03 | One WIP draft per client | Collaborate on one draft, or write your own; drafts keyed by author. |
| 03 | Linear-style rich text editing | ProseMirror over a Yjs `XmlFragment`; syntax disappears as you type it. |
| 04 | Rich-text test cleanup | A body-agnostic test seam after the XmlFragment flip; ~83 call sites triaged. |
| 04 | Session authorization | The Manager as an OIDC provider; each launch registers as a client. |
| 04 | OpenTelemetry | Direct emitters plus env pass-through into every child. |
| 05 | General presence cursors | One presence system for caret and selection, wherever a peer is focused. |
| 06 | Secrets in the Manager, and an ABAC layer | Encrypted at rest under an OS-credential-manager key; pure default-deny policy. |
| 07 | BYO user authorization | Trusted-header identity, the actor glossary, and the `x-yession-*` scheme. |
| 08 | Connections | A standards-only OAuth broker in the Manager, and Claude sign-in over it. |
| 09 | Remote session access | The session registry stream, and BYO serving in front of it. |
| 10 | Mounted sessions | One public address, stated once; a session serves under its own mount. |
| 11 | Idle session reaping | Sessions report busy/idle; the Manager reaps on silence. Also the stable way back in. |
| 12 | Path-mounted by default | An `{id}` template by default, and the storage promise the client can then keep. |
| 13 | Terminals on the WorkSandbox | Blocks, leases and transcripts over the sandbox seam. |
| 14 | Git repos | Bootstrap clones beside the agent, shared into the WorkSandbox. |
| 14 | Terminal work in the chat | Chips, block tabs, and replay as a first-class surface. |
| 15 | The imperative session API | Commands the agent runs, queries everyone reads; the read surface is generated. |
| 16 | Extensibility | Custom MCP servers, foreign terminals, and a tool-use record. |
| 17 | Declaring MCP servers | The Manager declares, every session it names gets it, live. |
| 18 | Jumpstarter | A provider in somebody else's language, with its own tests. |
| 19 | Provider streams | A stream a provider offers becomes a terminal a person can use. |
| 20 | Collaborative terminals | The list, pins, agent terminals, and wakes. |
| 20 | The conversation survives the network | Cursor-served event history, kept in the Cache API; cold open with no network. |
| 21 | Tokens that expire | Refreshable grants in the broker, so a user token need not be immortal. |
| 22 | The terminal survives the network too | Transcript history on the same cursor terms as the event log. |
| 23 | The classifier gates every act | Manual approval removed outright, replaced by one `ProposedAct` seam. |
| 24 | A credential that stopped working says so | Refresh failure reaches the connection panel, not only a turn's error. |
| 24 | Scoping a sandbox's reads to a directory | What srt can and cannot confine, verified by running it. |
| 25 | Manager & session UX fixes | The triage list from the 2026-08-21 UX review. |
| 25 | The shell profile | One durable fact about a session's terminals: where a new shell starts. |
| 25 | Terminal history as position × fidelity | Position is navigation, fidelity is a mode, and the mode never moves you. |
| 26 | Removing a repo | `remove_repo`, the verb `add_repo`'s advice had assumed for two plans. |

## The initial delivery steps

Before the plans there was one ordered sequence, cited as `Step NN`:

| # | Step |
|---|---|
| 00 | Foundations & shared domain types |
| 01 | Append-only event log |
| 02 | Session Process model & conversation projection |
| 03 | WebRTC transport & multiplexed frame protocol |
| 04 | Web app bootstrap & client Elmish shell |
| 05 | Ylmish/Yjs collaborative draft sync |
| 06 | Send draft & MessageSent event flow |
| 07 | Client event consumption by offset |
| 08 | Claude Code SDK agent turn |
| 09 | Phase 1 end-to-end acceptance |
| 10 | Session Manager & Session Process launch |
| 11 | Scoped environment capability & container handles |
| 12 | Lazy environment lifecycle |
| 13 | Command execution & read-only command log |
| 14 | Phase 2 authority & catch-up acceptance |

The sequence kept running past 14 into Phases 3 and 4 without files of its own, so citations
like `Step 19` (durable Yjs persistence) and `Step 24` (the control channel) name steps that
only ever existed as commits. `Step 11`'s container handles were later replaced by the sandbox
seam — see `docs/design.md` §3.
