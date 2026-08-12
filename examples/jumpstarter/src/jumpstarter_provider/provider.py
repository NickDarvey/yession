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
from dataclasses import dataclass
from typing import Any, Callable

import anyio
from mcp.server.mcpserver import Context, MCPServer

from .exporter import Exporter, ExporterError

# A claim follows a live MCP session. A session is alive while it is TALKING: every request
# under its id — a tool call, or the `tools/list` a client polls with — pushes the deadline
# out, and a client that has gone silent for this long has gone.
#
# One rule, not two: an explicit goodbye (`release`, or the DELETE that ends the session)
# and a crashed client are the same event seen at different speeds, and a second mechanism
# for the first case would be a second answer to "who holds this".
DEFAULT_CLAIM_TTL_SECONDS = 300.0


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

    def touch(self, holder: str) -> None:
        """A session said something. Called for EVERY request, including the ones the MCP
        server answers itself — which is why it lives at the HTTP layer rather than in a
        tool body."""
        self.expire()
        if self._claim is not None and self._claim.holder == holder:
            self._claim.last_seen = self._now()

    def expire(self) -> None:
        claim = self._claim
        if claim is not None and self._now() - claim.last_seen > self.ttl_seconds:
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


def _short(holder: str) -> str:
    """An MCP session id is 32 hex characters, and a refusal that quotes all of it reads as
    a hash rather than as somebody. The first eight are enough to tell two clients apart."""
    return holder[:8]


class Provider:
    """The MCP server, the exporter behind it, and the claim between them."""

    def __init__(self, exporter: Exporter, claims: Claims, version: str) -> None:
        self.exporter = exporter
        self.claims = claims
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
        async def acquire(ctx: Context) -> str:
            """Claim this exporter for your exclusive use. Everything that touches the hardware needs the claim, because powering the board off cuts the console out from under anyone else. One holder at a time: if somebody else has it, this says who. Release it when you are done."""
            holder = self._holder(ctx)
            if not holder:
                return "this request carries no MCP session id, so nothing can be claimed by it"
            took, other = self.claims.acquire(holder)
            if not took:
                return f"the exporter is already held by {_short(other or '')} — it is in use, not broken"
            return (
                "the exporter is yours. It stays yours while you keep talking to this server, "
                f"and is released automatically after {int(self.claims.ttl_seconds)}s of silence."
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
                seen = await _off_loop(lambda: self.exporter.console_read(_timeout(timeout_seconds)))
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
                matched, seen = await _off_loop(
                    lambda: self.exporter.console_expect(pattern, _timeout(timeout_seconds))
                )
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
        polling client is a live client)."""
        inner = self.mcp.streamable_http_app()
        claims = self.claims

        async def app(scope: Any, receive: Any, send: Any) -> None:
            if scope["type"] == "http":
                for name, value in scope.get("headers", []):
                    if name == b"mcp-session-id":
                        claims.touch(value.decode())
                        break
                else:
                    claims.expire()
            await inner(scope, receive, send)

        return app


def _timeout(seconds: float) -> float:
    """A timeout is a promise to answer, so it is bounded: a caller that asks for an hour
    would hold the whole server on one request."""
    return max(0.1, min(float(seconds), 120.0))


async def _off_loop(work: Callable[[], Any]) -> Any:
    """Everything the exporter does is a blocking queue round-trip, and doing it on the
    event loop would stall every other session for the length of a console read."""
    return await anyio.to_thread.run_sync(work)


def create(host: str, console: str, ttl_seconds: float, version: str) -> Provider:
    exporter = Exporter(host=host, console_name=console)
    # A claim ending closes the console, wherever that claim ended: `release`, or silence.
    # The console is the DUT's, not the holder's, and leaving it open would hand the next
    # holder a stream mid-sentence.
    claims = Claims(ttl_seconds=ttl_seconds, on_release=exporter.close_console_quietly)
    return Provider(exporter=exporter, claims=claims, version=version)
