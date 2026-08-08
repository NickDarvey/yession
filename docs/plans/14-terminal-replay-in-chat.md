# Plan 14 — Terminal work in the chat, and replay as a first-class surface

> **Status: shipped**, stages 0-7. Two things landed differently from the sketch below and
> are called out where they happen: keyframes are written at range STARTS only (stage 3), and
> the DVR is built out of the whole-terminal cast rather than a custom player source (stage 7).
> Builds directly on [Plan 13](13-worksandbox-terminals.md), which shipped
> terminals, blocks, the pty, live mode, the transcript sidecar, the chunk route and the
> asciinema replay of a closed terminal. This plan takes the two things Plan 13 deliberately
> left open — the browser terminal viewport, and the fact that terminal work is invisible from
> the conversation — and turns them into one surface.

Terminal work stops being a separate log you have to go and read. A command someone ran appears in the chat where it ran; an interactive stretch appears when it
concluded; either one opens read-only in the side pane as a tab you can leave open. A live
terminal can be rewound like live TV and caught back up, through the same mechanism that
replays a finished one.

## What this reverses, and what it does not

`Conversation.fs` says, in a comment that is load-bearing:

> Terminals (Plan 13) project into `TerminalProjection`. A command someone ran is not
> something someone said: it belongs beside its output, in the terminal it ran in, not
> interleaved with the conversation.

That was right about the **fold** and wrong about the **screen**. `ConversationProjection` is
what builds the agent's context; folding terminal events into it would double-feed the model,
which already receives block outcomes through `TerminalDigest`, and would silently change what
every turn reads. So the fold does not change. The interleaving happens one layer up, in a
**timeline** the view reads and nothing else does.

That is affordable precisely because both projections are folds of the *same* ordered log:
merging them is sorting by `EventOffset`, not reconciling two clocks. The only thing standing
in the way is that `ConversationItem` does not currently carry its offset, which is a one-field
change (the projection is derived on both sides and persisted nowhere).

**The consequence, stated rather than discovered later:** the human's chat and the agent's chat
diverge. A person scrolling back sees twelve commands the agent's context does not contain in
that form. This is deliberate — the digest gives the agent the outcomes it needs, bounded and
tailed, and the chat gives a person the shape of what happened — but it means "what is in the
conversation" now has two answers depending on who is asking. Anything that reasons about the
conversation as a single artefact (export, search, a future summarizer) has to pick one.

## What appears in the chat

**The unit is the block or the lease stretch, never the terminal.** Blocks exist only in block
mode and lease stretches only in live mode, so the two tile a terminal's timeline exactly, with
no overlap and no gap. That partition is already in the model, which is why it needs no
"was this a long session?" heuristic and no threshold to tune — a terminal that starts as a
shell, becomes a `vim` session, and returns to a prompt contributes chips, then one session
item, then more chips, in order, and each of them is true.

**A block chip** is one line: who ran what, and its status — running, exit code, or rejected.
No output. Output is one tap away and putting a tail inline would make the chat noisy at exactly
the moments it is busiest, and would re-raise the secrets question for everything a command
prints. A chip anchors at the block's **start** and mutates in place as it finishes, which means
a four-minute build's result lands above messages sent while it ran. That is Claude Code's
behaviour and it is the right one: the alternative, appearing only on completion, makes long
work invisible while it is the only thing happening.

Rejected commands get a chip too. `BlockRejected` exists so that *"agent: `rm -rf /` — rejected
by nick"* is a thing you can read; a rejection that appears nowhere is indistinguishable from a
bug. It has nothing to replay, and its tab says so.

**Approval stays in the panel.** The chip reports; it does not act. This is the narrower choice
— there is a real argument that reviewing what the agent is about to run is the same act as
reading what it is about to say, which is the argument the composer's design already makes —
but a chat that is also a control surface is a bigger change than this plan needs to be, and
the queue already has a home.

**A session item** is one line when a lease stretch ends: who held it, for how long, and how it
ended. The four endings read differently on purpose and `TerminalLeaseEnd` already distinguishes
them — released, stolen by someone, holder gone, reclaimed after idle. Those are event-log facts,
so the item renders instantly at any scroll depth without touching a transcript. Its **poster**
— a still of the final screen — is not a fact and is discussed under stage 4.

**No terminal-level chat item.** A terminal that opened and closed having run nothing leaves no
trace in the chat, which is correct: nothing happened. The whole recording stays reachable from
the panel's closed-terminal tab, which stage 3e already ships, and from a "play whole terminal"
step-out inside any chip's tab.

## The pane

The side pane stops being "the terminal panel" and becomes a **tab strip over three kinds of
thing**: a live terminal, a block's read-only view, and a stretch's replay. Today the selection
is `TerminalChoice : TerminalId option` — one terminal, no tab list — so this is a genuine model
change rather than a rename: a tab needs an identity a `TerminalId` cannot express.

Tabs are **local to the client**. Opening a recording to read it must not move anyone else's
screen; that is reading, not collaborating. Presence still shows who is *in* a terminal, because
that is about the shared thing, not about which recording you happen to be looking at.

On a phone the columns collapse to one and opening a tab switches the column to the pane,
keeping the tab strip. The chat is a back-swipe away rather than a dismissed overlay, which
keeps desktop and phone the same mental model at the cost of more layout work. The WCAG floor
applies without exception: the column swap moves focus to the pane, dismissing returns it to the
chip that opened it, and the tab strip is a real tablist operable from the keyboard.

## Replay: what a range actually needs

A block chip's tab shows one command's output. The naive construction — take transcript lines
`FromSeq..ToSeq`, wrap them in a header, hand them to the player — is wrong twice, and both
are worth writing down because both look fine in a demo.

**Times are absolute.** asciicast timestamps are relative to the start of the *file*. Slice a
block that ran forty minutes in and the player sits idle for forty minutes before the first
frame. The range extractor rebases to zero, or every block replay is broken in a way that looks
like a hang.

**Screen state is path-dependent.** A terminal's screen at line 500 is a function of every byte
before it — colours set earlier, cursor position, scroll region, whether something entered the
alternate screen. Replaying a slice into a fresh VT is *approximately* right for ordinary
command output and wrong exactly when it matters. For block mode this is nearly saved by OSC 133:
a block starts at a fresh prompt line, so a fresh VT is close. "Close" is not a property to build
an audit trail on, and it is not close at all for a lease stretch, which by definition begins
with a program already owning the screen.

So: **keyframes**. At each range START — every block's `FromSeq`, every lease stretch's — the
Session Process serializes the emulator's screen — the same serializer `TerminalSnapshot` already uses to bring
a joining peer up to date — and writes it to a sidecar keyed by transcript seq. A ranged replay
is then *header + one synthesized output record that paints the keyframe + the rebased range*,
which is a valid asciicast the stock player renders with no modification.

Note what this is **not** for. Keyframes are usually a seek-performance trick, and that was the
weaker case: the player rebuilds VT state by re-feeding from `t=0`, which is milliseconds for an
ordinary recording and only hurts near the 64 MB per-terminal cap. The reason to build them is
*correctness of a ranged replay*, and their cost is bounded by the number of blocks and leases
rather than by output volume — the same argument that keeps input and resize records uncapped
while output is capped.

They stay in a **sidecar**, not in the `.cast`. Plan 13 bought a standard, replayable format
on purpose; putting a private record type in the file spends that.

**Shipped at range starts only.** A keyframe at every range END too would be a keyframe nothing
reads: a range `[from, to)` asks for the one at `from`, and the next range's `from` gets its own.
Capturing it reads the transcript position first and starts the serialize in the same tick, which
is what makes the pairing exact — the emulator's write barrier resolves over everything queued
before it and nothing that arrived after.

**Whole-terminal replay needs none of this.** The full cast starts at the start, so it mounts as
it does today, with two additions the player already supports: `markers` at each block boundary
turn the recording into chapters, and `startAt` lets "play whole terminal" from a chip land on
that block in full context. Two paths — sliced for the chip, whole for the step-out — because
they answer different questions: "what did this command print" and "what was going on around it".

## Live TV

Rewinding a running terminal is the same construction with the tail moving. History comes from
the immutable chunk route through the browser's HTTP cache; the live tail arrives on the data
channel as `TerminalOutput`; keyframes give seek targets that do not require re-feeding from the
beginning. The union of the two transports the design already has is precisely a DVR.

The stock player cannot express this: its file source is static, and its live sources
(websocket, eventsource) do not seek backwards. So the client owns the timeline and drives the
player through a custom source — history, tail, seek, and a "jump to live" that reattaches.

**Shipped without the custom source, and the difference is worth stating.** The client already
holds every record — history fetched over the chunk route, tail arriving on the data channel —
so rewinding PINS the transcript length at that moment and mounts the ordinary whole-terminal
cast over `[0, length)`, through the same player a finished terminal replays in. Seeking is then
the player's own, and it needs no keyframes because the cast starts at the start. "Jump to live"
drops the pin and the live screen comes back.

What that gives up is watching the tail arrive while behind it: the recording under the reader
is fixed until they catch up. That is deliberate — a recording that grew under a scrub bar would
move it out from under them, which is the one thing rewinding is for avoiding — and it is why
the custom source is not needed to satisfy "rewound like live TV through the same mechanism". If
following-while-behind is ever wanted, the custom source is where it goes.

**This is offered on any live terminal, not only interactive ones.** The mechanism does not care
which mode the terminal is in; both are one growing byte stream. Scrubbing back through a running
build's output is the same act as scrubbing back through a `vim` session, and a rule that
offered it in one and not the other would be a special case to explain rather than a feature.

**It cannot come first.** There is nothing to rewind from until the browser can show a live
terminal, which is the viewport Plan 13 left open: the Session Process side is complete — lease,
flip, `TerminalInput`/`TerminalResize`, the idle timeout — and the client has no producer for
those frames and no live screen. So the viewport is a stage of this plan, and the DVR sits on it.

## Retention

**The 7-day deletion of closed transcripts goes away.** Chat chips are permanent and chips tile
the whole timeline, so every recording is referenced by something a person can tap; deleting one
turns a chip into a dead end. Recordings now live as long as the session does and are deleted
with it.

**Be clear-eyed about what that costs.** The per-terminal 64 MB output cap becomes the only
ceiling, so a session's transcript floor is 64 MB × terminals and it never shrinks. A long-lived
session that opens terminals freely is the case that bites; a session-wide budget that sheds the
oldest closed recording is the obvious answer if it does, and it is not being built now.

`TerminalTranscriptTruncated` and the stated-gap rendering stay exactly as they are — the
per-terminal cap can still be met, and a chip whose bytes were dropped says so rather than
opening an empty player.

## Delivery

Eight stages, each independently verifiable and each shippable on its own.

### Stage 0 — retention becomes session-lifetime

Delete `TranscriptRetention.closedFor` and the sweep that reads it; recordings are removed when
the session's data is. Smallest possible change, first, so no chip is ever created that a
timer will later orphan.

*Tests:* the existing retention suite loses its deletion case and gains one asserting a closed
terminal's transcript survives past the old window. Cheap tier.

### Stage 1 — the timeline

`ConversationItem` gains its `EventOffset`. A new `TimelineProjection` merges conversation items,
block chips and lease-stretch items by offset; it is a view-level fold and `ConversationProjection`
is untouched. `TerminalLeaseTaken`/`TerminalLeaseReleased` gain `FromSeq`/`ToSeq` — the Session
Process knows the transcript position when it appends them, nothing can derive it afterwards, and
an event schema change is cheapest before there are recordings that lack it.

Chips render in the chat. Tapping selects that terminal in the existing panel and scrolls to the
block — degenerate, but not a dead tap, and it means the timeline ships before the tab model.

*Tests:* cheap tier throughout. Ordering under interleaving (a block that starts before a message
and finishes after it), a chip mutating in place from running to exit code, a rejected command's
chip, a stretch item for each of the four lease endings, and the property that
`ConversationProjection` is byte-identical before and after.

### Stage 2 — the pane tab model

`PaneTab` replaces `TerminalChoice`: a live terminal, a block view, or a stretch replay, several
open at once, closeable, local to the client. Chat chips open tabs. A block's tab renders command
and output read-only from the chunks the client already has — no player yet.

*Tests:* cheap tier for the tab model and the read-only block render (Fable.Lit SSR); `Browser`
for keyboard operation of the tab strip and focus return on close.

### Stage 3 — keyframes and the ranged cast

The Session Process writes a screen keyframe at each block and lease boundary into
`<session>.term-<id>.keys.jsonl`. `TranscriptReplay` gains `range`, producing header + keyframe
record + rebased records. The block tab replays exactly rather than approximately.

*Tests:* cheap tier for rebasing and for keyframe selection; a `Ports Native` case that runs a
command which sets colour and cursor state *before* the block under test and asserts the ranged
replay reproduces the screen the emulator had — the assertion that the naive slice fails.

### Stage 4 — the video item

The player mounts in a stretch's tab. `poster: npt:<end>` gives the item its still, which costs
nothing extra because the player builds it by replaying internally. `markers` per block turn a
whole-terminal recording into chapters, and "play whole terminal" from a chip mounts the full
cast with `startAt` on that block.

*Tests:* `Browser`, extending the existing host-free harness rather than minting a second one —
poster renders, markers appear, `startAt` lands on the right frame.

### Stage 5 — the phone

One column, the tab strip retained, chat a back-swipe away. Focus moves to the pane on open and
returns to the originating chip on close.

*Tests:* `Browser`, driven through the CDP driver in `.agents/skills/ui-exploration/SKILL.md`
— headless Chromium's window-size clamp makes naive mobile screenshots lie.

### Stage 6 — the live viewport

Plan 13's open item: the client renders a live screen from `TerminalOutput` and
`TerminalSnapshot`, produces `TerminalInput`/`TerminalResize`, and wires take/release/steal to
the lease bar that already exists.

The client composes the screen with the SAME emulator the Session Process uses, linked rather
than copied — fed the same bytes in the same order, the two screens cannot disagree, which is
the property `TerminalSnapshot` already rests on.

*Tests:* `Browser`, host-free — the keystroke translation is the part only a real browser can
answer, because a `KeyboardEvent` is not something a rendered string has. The two-peer pty flow
is NOT covered: it needs `Pty` and a sandbox that can spawn. The Process half of it is already
pinned in the pty suite.

### Stage 7 — the DVR

Rewind and jump-to-live on any live terminal, over the whole-terminal cast pinned at the moment
of rewind (see "Live TV" above for why the custom source turned out to be unnecessary).

*Tests:* cheap tier for the pinning and for what ends a rewind; `Browser` for the control
surface and for the recording really playing in a real player.

## Protocol and versioning

Session events change (the lease range fields), and the Session↔Browser frame protocol is
untouched, so the Manager↔Session contract is unaffected: **no major bump**. Stages 1, 2, 4, 6
and 7 land user-facing capability and carry `+semver: minor` on a line of its own in a commit
body on the branch. Stage 0 is a `refactor:`/`fix:` with no marker; stages 3 and 5 carry
`+semver: patch` at most.

## What this plan leaves open

- **A session-wide transcript budget.** Stage 0 removes the only time-based bound and does not
  replace it. Named here so that "the disk grew" is a known consequence rather than a discovery.
- **Approval from the chip.** The chat reports terminal work and does not act on it.
- **Search across the timeline.** Merging two projections into one screen invites "find the
  command that did X", and nothing here indexes anything.
- **The GAPS entries from Plan 13, unchanged**: the agent takes no lease, terminals are not gated
  per user beyond session membership, and the docker backend runs as the image's user.

## Things worth considering before this starts

- **The exposure profile widens.** Plan 13 does not record keystrokes, on purpose, because live
  mode makes typing a password ordinary. Output *is* recorded, and a screen that displayed a
  secret is in the recording. Today reaching it means going into a terminal; after this it is one
  tap from the main chat, for everyone in the session, permanently — and stage 0 makes
  "permanently" literal. Nothing here is newly unsafe, but the distance between a secret and a
  casual reader gets much shorter.
- **The chat becomes the busiest surface in the product.** An agent that runs twenty commands to
  answer one question produces twenty chips around one message. The chip is one line by design,
  but if that still reads as noise the answer is grouping consecutive same-terminal chips, and
  that is a decision better made against a real transcript than in advance.
- **Two replay paths is a real cost.** Sliced-for-the-chip and whole-for-the-step-out are
  justified by answering different questions, and they are still two things to keep correct. If
  one of them starts drifting, the sliced path is the one to delete — `startAt` on the whole cast
  can express everything the slice can, just more expensively.
- **Stage 6 is the largest and least certain piece**, and stages 1–5 do not depend on it. If it
  slips, everything about replaying finished work still ships; only the DVR waits.
