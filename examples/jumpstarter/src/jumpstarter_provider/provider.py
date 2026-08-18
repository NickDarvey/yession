"""The provider: eight tools, one claim, and the MCP server they hang off.

Nothing here is Yession-shaped. Point any MCP client at the control url and it works, which
is the whole point of an out-of-process provider and the reason this is an example rather
than a built-in.

The claim is the interesting part. Jumpstarter arbitrates access with LEASES, and leases
live in the controller — which a direct-mode exporter does not have. So the arbitration is
this server's, and it is one claim over the WHOLE exporter rather than one per driver:
powering the board off invalidates the console session, so a claim per driver would be a
promise of isolation the hardware does not keep.
"""

from __future__ import annotations

import time
from contextlib import contextmanager
from dataclasses import dataclass
from typing import Any, Callable, Iterator

import anyio
from mcp.server.mcpserver import Context, MCPServer
from mcp.types import CallToolResult, TextContent

from .exporter import Exporter, ExporterError
from .stream import Console

# A claim follows a live MCP session. A session is alive while it is TALKING: every request
# under its id — a tool call, or the `tools/list` a client polls with — pushes the deadline
# out, and a client that has gone silent for this long has gone.
#
# ...or while it is LISTENING, which is the same fact arriving differently. Streamable HTTP
# lets a client open one GET and leave it open for the session's whole life; measured by
# requests that client spoke once and vanished, while it is in fact the most connected a
# client gets. `Claims.streaming` is that second reading, and it is here rather than in the
# caller because a rule that holds only while the caller keeps polling is not the claim's
# rule — it is the caller's habit, and the next client has not got it.
#
# One rule, not two: an explicit goodbye (`release`, or the DELETE that ends the session)
# and a crashed client are the same event seen at different speeds, and a second mechanism
# for the first case would be a second answer to "who holds this".
DEFAULT_CLAIM_TTL_SECONDS = 300.0

# Where the data leg lives, on the same listener as `/mcp`.
ATTACH_PATH = "/attach/"


@dataclass
class _Claim:
    holder: str
    taken_at: float
    last_seen: float


class Claims:
    """Who holds the exporter, and for how much longer.

    Its clock is a parameter so a test can age a claim without sleeping for five minutes.
    """

    def __init__(
        self,
        ttl_seconds: float = DEFAULT_CLAIM_TTL_SECONDS,
        now: Callable[[], float] = time.monotonic,
        on_release: Callable[[], None] = lambda: None,
    ) -> None:
        self.ttl_seconds = ttl_seconds
        self._now = now
        self._on_release = on_release
        self._claim: _Claim | None = None
        # Counted, not flagged: a client may hold more than one open stream at a time, and
        # the first to close must not answer for the rest.
        self._streaming: dict[str, int] = {}

    def touch(self, holder: str) -> None:
        """A session said something. Called for EVERY request, including the ones the MCP
        server answers itself — which is why it lives at the HTTP layer rather than in a
        tool body."""
        self.expire()
        if self._claim is not None and self._claim.holder == holder:
            self._claim.last_seen = self._now()

    @contextmanager
    def streaming(self, holder: str) -> Iterator[None]:
        """For as long as this session holds a stream open, it is talking.

        Entered around the request that IS the stream, so the claim follows that request's
        lifetime rather than its arrival — the one case the deadline cannot measure, because
        nothing further arrives to measure.
        """
        self._streaming[holder] = self._streaming.get(holder, 0) + 1
        try:
            yield
        finally:
            remaining = self._streaming.get(holder, 0) - 1
            if remaining > 0:
                self._streaming[holder] = remaining
            else:
                self._streaming.pop(holder, None)
                # The deadline starts NOW rather than from whenever this session last sent
                # a request. It has been alive the whole time the stream was open, so
                # measuring its silence from before that would expire a client that has
                # said nothing wrong — instantly, if the stream outlived one TTL.
                claim = self._claim
                if claim is not None and claim.holder == holder:
                    claim.last_seen = self._now()

    def expire(self) -> None:
        claim = self._claim
        if claim is None:
            return
        if self._streaming.get(claim.holder):
            return
        if self._now() - claim.last_seen > self.ttl_seconds:
            self._claim = None
            self._on_release()

    @property
    def holder(self) -> str | None:
        self.expire()
        return self._claim.holder if self._claim else None

    def acquire(self, holder: str) -> tuple[bool, str | None]:
        """(took it, who has it instead)."""
        self.expire()
        if self._claim is None:
            now = self._now()
            self._claim = _Claim(holder=holder, taken_at=now, last_seen=now)
            return True, None
        if self._claim.holder == holder:
            return True, None
        return False, self._claim.holder

    def release(self, holder: str) -> bool:
        self.expire()
        if self._claim is None or self._claim.holder != holder:
            return False
        self._claim = None
        self._on_release()
        return True


def _said(text: str) -> CallToolResult:
    """Prose, as the one shape a tool that SOMETIMES carries `_meta` may answer with. The
    SDK refuses a union return, and rightly: a signature that is sometimes a result and
    sometimes a string is two contracts."""
    return CallToolResult(content=[TextContent(type="text", text=text)])


def _short(holder: str) -> str:
    """An MCP session id is 32 hex characters, and a refusal that quotes all of it reads as
    a hash rather than as somebody. The first eight are enough to tell two clients apart."""
    return holder[:8]


class Provider:
    """The MCP server, the exporter behind it, and the claim between them."""

    def __init__(
        self,
        exporter: Exporter,
        claims: Claims,
        version: str,
        origin: Callable[[], str],
        console: Console,
    ) -> None:
        self.exporter = exporter
        self.claims = claims
        # Where this server's own data leg is, resolved LATE: a provider bound to port 0
        # does not know its address until it is listening, and every test binds port 0.
        self.origin = origin
        self.console = console
        # `jumpstarter` is the namespace every tool of this server lands in for a client
        # that prefixes them (Yession shows `mcp__jumpstarter__power`), so it is not a
        # label — it is half of every name the model sees.
        self.mcp = MCPServer("jumpstarter", version=version)
        self._register()

    # --- the claim, as the tools see it ------------------------------------------------

    def _holder(self, ctx: Context) -> str:
        # The session id is on the request, which is what makes a claim outlive a tool call
        # without the caller having to carry a token around.
        return dict(ctx.headers).get("mcp-session-id", "")

    def _refusal(self, ctx: Context) -> str | None:
        """Why this caller may not touch the hardware, in the words it should read.

        A refusal is an ANSWER, not an error: "somebody else has it" is a fact about the
        world, and a caller told it as a failure reasonably concludes something is broken
        and tries to fix it.
        """
        holder = self._holder(ctx)
        if not holder:
            return "this request carries no MCP session id, so nothing can be claimed by it"
        current = self.claims.holder
        if current is None:
            return "nobody holds this exporter — call acquire first"
        if current != holder:
            return f"the exporter is held by {_short(current)}, not by you — it is in use, not broken"
        return None

    # --- the tools ---------------------------------------------------------------------
    #
    # The descriptions are written for the MODEL, not for a person reading the source: what
    # the tool is for, what it costs, and what to do next. A tool description is the only
    # documentation its caller will ever see.

    def _register(self) -> None:
        mcp = self.mcp

        @mcp.tool()
        async def status(ctx: Context) -> str:
            """What this exporter is: whether it can be reached, which drivers it exports (power, serial, storage and so on), and who currently holds it. Costs nothing and needs no claim — call it first, and call it again if something stops answering."""
            holder = self.claims.holder
            if holder is None:
                held = "nobody holds it"
            elif holder == dict(ctx.headers).get("mcp-session-id", ""):
                held = "you hold it"
            else:
                held = f"held by {_short(holder)} — it is in use, not broken"
            try:
                drivers = await _off_loop(self.exporter.drivers)
            except ExporterError as error:
                return f"the exporter is not answering: {error}"
            listed = "\n".join(f"  {line}" for line in drivers) or "  (it exports nothing)"
            return f"exporter at {self.exporter.host}, {held}.\nDrivers:\n{listed}"

        @mcp.tool()
        async def acquire(ctx: Context) -> CallToolResult:
            """Claim this exporter for your exclusive use, and get its console as a stream. Everything that touches the hardware needs the claim, because powering the board off cuts the console out from under anyone else. One holder at a time: if somebody else has it, this says who. Release it when you are done."""
            holder = self._holder(ctx)
            if not holder:
                return _said("this request carries no MCP session id, so nothing can be claimed by it")
            took, other = self.claims.acquire(holder)
            if not took:
                return _said(f"the exporter is already held by {_short(other or '')} — it is in use, not broken")
            held = (
                "the exporter is yours. It stays yours while your session keeps talking to this "
                "server or keeps its event stream open, and is released automatically after "
                f"{int(self.claims.ttl_seconds)}s of neither."
            )
            # The console, as a stream a CLIENT can open — in `_meta`, which is the place MCP
            # reserves for data meant for the client rather than the model. A client that has
            # never heard of the key ignores it and still gets the prose, and still has the
            # three console tools; one that knows it turns the console into a terminal people
            # can watch and type into.
            offer = self.console.offer(self.origin(), holder)
            if offer is None:
                return _said(f"{held} Its console is already attached to a terminal.")
            return CallToolResult(
                content=[TextContent(type="text", text=held)],
                meta={"dev.yession/stream": offer},
            )

        @mcp.tool()
        async def release(ctx: Context) -> str:
            """Give the exporter back, closing the console if one is open. Safe to call when you do not hold it."""
            holder = self._holder(ctx)
            if not self.claims.release(holder):
                current = self.claims.holder
                if current is None:
                    return "nobody held the exporter"
                return f"the exporter is held by {_short(current)}, not by you"
            return "released the exporter"

        @mcp.tool()
        async def power(ctx: Context, action: str) -> str:
            """Power the device under test on, off, or cycle it (off, a pause, then on) — the usual way to recover a board that has stopped responding. Needs the claim. Anything on the console at the time is lost; re-read it after. There is no way to ASK whether the board is on: jumpstarter's power drivers take commands and report measurements, so read the volts and amps with driver_call power read instead."""
            refusal = self._refusal(ctx)
            if refusal:
                return refusal
            wanted = action.strip().lower()
            if wanted not in ("on", "off", "cycle"):
                return f"'{action}' is not a power action — use on, off or cycle"
            try:
                if wanted == "cycle":
                    await _off_loop(lambda: self.exporter.call("power", "off", []))
                    await anyio.sleep(2.0)
                    await _off_loop(lambda: self.exporter.call("power", "on", []))
                else:
                    await _off_loop(lambda: self.exporter.call("power", wanted, []))
            except ExporterError as error:
                return f"the power driver refused: {error}"
            return f"power {wanted} done"

        @mcp.tool()
        async def driver_call(ctx: Context, driver: str, method: str, args: list[Any] | None = None) -> str:
            """Call any method on any driver this exporter exports — the general case behind the power and console tools. `driver` is a name from status (dotted for a nested one, like "bench.power"), `method` is one of its methods, `args` are positional. Call with no method to be told exactly what that driver offers, which is the reliable way to find out — a method that streams (like power read) is drained to its first few items and says so. Needs the claim."""
            refusal = self._refusal(ctx)
            if refusal:
                return refusal
            try:
                if not method:
                    offered = await _off_loop(lambda: self.exporter.methods(driver))
                    return f"{driver} offers: {', '.join(offered)}"
                result = await _off_loop(lambda: self.exporter.call(driver, method, list(args or [])))
            except ExporterError as error:
                return str(error)
            # `!r` is the exporter's job now, and it already did it: `call` answers with a
            # STRING it shaped (a repr, or a drained stream). Repr-ing that again wrapped
            # every answer in quotes, so a list of readings read as one long string.
            return f"{driver}.{method} returned {result}"

        @mcp.tool()
        async def serial_send(ctx: Context, data: str) -> str:
            """Write to the device's serial console. Nothing is appended, so end a command with \\n yourself. The console opens on first use and stays open until you release the exporter. Needs the claim. This says only that the bytes went out — read or expect to find out what happened."""
            refusal = self._refusal(ctx)
            if refusal:
                return refusal
            # One writer, and while a terminal is attached it is the terminal's — where a
            # lease says who is typing and everyone can see the keystrokes. Writing here as
            # well would be a second door onto one console, past that lease.
            if self.console.attached:
                return (
                    "the console is attached to a terminal — type into it there "
                    "(write_terminal), where people can see who is typing"
                )
            try:
                await _off_loop(lambda: self.exporter.console_send(data))
            except ExporterError as error:
                return f"the console refused the write: {error}"
            return f"sent {len(data)} characters"

        @mcp.tool()
        async def serial_read(ctx: Context, timeout_seconds: float = 2.0) -> str:
            """Everything the console has said since you last read it, up to a pause of timeout_seconds. Needs the claim. Use expect instead when you know what you are waiting for — this one always waits the whole timeout out."""
            refusal = self._refusal(ctx)
            if refusal:
                return refusal
            try:
                seen = await self.console.read(_timeout(timeout_seconds))
            except ExporterError as error:
                return f"the console is not readable: {error}"
            return seen if seen else "(the console said nothing)"

        @mcp.tool()
        async def serial_expect(ctx: Context, pattern: str, timeout_seconds: float = 10.0) -> str:
            """Wait for the console to say something matching pattern (a Python regular expression) and return everything up to and including it — a login prompt, a boot banner, a shell prompt after a command. Returns as soon as it matches. On a timeout you still get what WAS said, which is usually where the answer is. Needs the claim."""
            refusal = self._refusal(ctx)
            if refusal:
                return refusal
            try:
                matched, seen = await self.console.expect(pattern, _timeout(timeout_seconds))
            except ExporterError as error:
                return f"the console is not readable: {error}"
            if matched:
                return seen
            body = seen if seen else "(nothing at all)"
            return f"'{pattern}' did not appear within {_timeout(timeout_seconds):.0f}s. The console said:\n{body}"

    # --- the app -----------------------------------------------------------------------

    def app(self) -> Any:
        """The ASGI app, with the one thing the MCP server does not do for us wrapped
        around it: seeing every request, so a claim can follow a session it never hears
        about at the tool layer (`tools/list` is answered by the server itself, and a
        polling client is a live client — as is a streaming one, below)."""
        inner = self.mcp.streamable_http_app()
        claims = self.claims
        console = self.console

        async def app(scope: Any, receive: Any, send: Any) -> None:
            if scope["type"] == "http":
                session = _session_of(scope)
                if session:
                    claims.touch(session)
                else:
                    claims.expire()
                # The optional server->client stream is a GET that arrives once and stays
                # open for the session's life. Touching it on arrival would credit it with
                # one instant of liveness and then let the claim expire underneath a client
                # that is still connected, so the claim follows the stream while it is OPEN.
                if session and scope.get("method") == "GET":
                    with claims.streaming(session):
                        await inner(scope, receive, send)
                    return
            # The data leg (Plan 19), on the same port as the control leg — which is the
            # whole reason a WebSocket was the right shape: the upgrade rides the listener
            # the provider already has.
            if scope["type"] == "websocket":
                path = scope.get("path", "")
                if path.startswith(ATTACH_PATH):
                    await console.serve(path[len(ATTACH_PATH) :], receive, send)
                    return
                await send({"type": "websocket.close", "code": 1008})
                return
            await inner(scope, receive, send)

        return app


def _session_of(scope: Any) -> str:
    """The MCP session id off an ASGI request, or "" for a request that carries none."""
    for name, value in scope.get("headers", []):
        if name == b"mcp-session-id":
            return value.decode()
    return ""


def _timeout(seconds: float) -> float:
    """A timeout is a promise to answer, so it is bounded: a caller that asks for an hour
    would hold the whole server on one request."""
    return max(0.1, min(float(seconds), 120.0))


async def _off_loop(work: Callable[[], Any]) -> Any:
    """Everything the exporter does is a blocking queue round-trip, and doing it on the
    event loop would stall every other session for the length of a console read."""
    return await anyio.to_thread.run_sync(work)


def create(
    host: str,
    console: str,
    ttl_seconds: float,
    version: str,
    origin: Callable[[], str] = lambda: "",
) -> Provider:
    exporter = Exporter(host=host, console_name=console)
    stream = Console(exporter=exporter, off_loop=_off_loop)

    # A claim ending closes the console, wherever that claim ended: `release`, or silence.
    # The console is the DUT's, not the holder's, and leaving it open would hand the next
    # holder a stream mid-sentence. The STREAM is the claim's for the same reason, so its
    # token dies with it.
    def released() -> None:
        stream.release()
        exporter.close_console_quietly()

    claims = Claims(ttl_seconds=ttl_seconds, on_release=released)
    return Provider(exporter=exporter, claims=claims, version=version, origin=origin, console=stream)
