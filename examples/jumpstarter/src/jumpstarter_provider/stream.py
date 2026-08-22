"""The data leg: the console as a stream, over a WebSocket.

A tool call is request/response and a console is not, so the bytes get a connection of their
own — the same shape the serial example uses, and the same shape ttyd, gotty and
`kubectl exec` use, because the point of a wire is that somebody else can implement it:

  * BINARY frames are data, both directions. No framing of ours.
  * TEXT frames are control: {"type":"resize",...} (nothing to do here — a console has no
    size) and {"type":"kill"} (end the stream, not the claim).
  * Termination is {"type":"exited","code":N} and THEN a close, because an abnormal closure
    carries nothing and that path exists anyway.

The console has exactly ONE reader, and that is the whole simplification. It used to have
two: this provider offered `serial_read`/`serial_expect` as well, and since a `pexpect` spawn
is consumed by whoever reads it, the drain had to tee every chunk into a buffer those tools
could read instead — with an arrival event to wake them and a cap to bound it. All of that
existed to keep two readers from starving each other.

The tools are gone. Yession's own `read_terminal` reads the same console through the terminal
this stream becomes, against a durable transcript rather than a handle that is consumed by
looking at it — so the drain now reads the console and writes it to the socket, and there is
nothing to keep in step.
"""

from __future__ import annotations

import json
import re
import secrets
from typing import Any, Awaitable, Callable

import anyio

from .exporter import ExporterError

# How long one console read waits before the drain loops again. Short, because it is the
# latency between a device saying something and a person seeing it; not zero, because each
# one is a queue round trip to the SDK thread.
DRAIN_SECONDS = 0.2



class Console:
    """The console, as one drain with two readers and one writer."""

    def __init__(self, exporter: Any, off_loop: Callable[[Callable[[], Any]], Awaitable[Any]]) -> None:
        self._exporter = exporter
        self._off_loop = off_loop
        self._token: str | None = None
        self._holder: str | None = None
        self._send: Callable[[bytes], Awaitable[None]] | None = None

    # --- the offer -------------------------------------------------------------------

    @property
    def attached(self) -> bool:
        return self._send is not None

    def offer(self, origin: str, holder: str) -> dict[str, Any] | None:
        """The stream this holder may open, as a client reads it — or `None` when one is
        already open, because a second token would be a second reader of a console that has
        exactly one.

        Single-use, and minted fresh each time: the token IS the authority to reach the
        device, so one that could be replayed would hand a second client the stream. That is
        also what makes `renewable` true rather than hopeful — ask again after a stream
        ends and you get one that works.
        """
        if self.attached:
            return None
        self._token = secrets.token_hex(16)
        self._holder = holder
        return {
            "url": f"{origin}/attach/{self._token}",
            "label": f"{self._exporter.console_name} console",
            "renewable": True,
        }

    def release(self) -> None:
        """The claim ended. The stream is the claim's, so it ends too."""
        self._token = None
        self._holder = None

    # --- what the tools read ----------------------------------------------------------
    #
    # Reads answer from the drain while a stream is attached and from the console itself
    # otherwise. Same question, same shape of answer; the difference is only who is holding
    # the `pexpect` handle, which is not something a caller should have to know.

    # --- the socket -------------------------------------------------------------------

    async def serve(self, token: str, receive: Any, send: Any) -> None:
        """One attach, from the ASGI websocket scope onwards.

        Written against raw ASGI rather than a framework's WebSocket class for the reason
        the rest of this example is written longhand: what is on the wire is the part worth
        reading.
        """
        message = await receive()
        if message["type"] != "websocket.connect":
            return
        await send({"type": "websocket.accept"})

        # Spent on use, and checked AFTER the accept so the refusal can be said in band —
        # a close code carries nothing a client can act on.
        if token != self._token or self._token is None or self.attached:
            await send({"type": "websocket.send", "text": json.dumps({"type": "exited", "code": 1})})
            await send({"type": "websocket.close", "code": 1000})
            return
        self._token = None

        # Every send goes through here, because the other end can vanish at ANY point — a
        # browser tab closing, a session ending — and a stream that raised on a client that
        # has already gone would turn an ordinary goodbye into an error in the logs.
        gone = False

        async def out(message: dict[str, Any]) -> None:
            nonlocal gone
            if gone:
                return
            try:
                await send(message)
            except Exception:  # noqa: BLE001 — the client is gone; there is nobody to tell
                gone = True

        async def write(data: bytes) -> None:
            await out({"type": "websocket.send", "bytes": data})

        self._send = write
        try:
            async with anyio.create_task_group() as tasks:
                tasks.start_soon(self._drain, out)
                await self._pump(receive)
                tasks.cancel_scope.cancel()
        finally:
            self._send = None
        await out({"type": "websocket.send", "text": json.dumps({"type": "exited", "code": 0})})
        await out({"type": "websocket.close", "code": 1000})

    async def _drain(self, send: Any) -> None:
        """The one reader of the console while a stream is open. Everything it reads goes to
        the socket AND to the buffer the tools read, which is what stops a human opening a
        terminal from blinding the agent."""
        while True:
            try:
                chunk = await self._off_loop(lambda: self._exporter.console_read(DRAIN_SECONDS))
            except ExporterError as error:
                # The console went away — the claim was released, the board was powered off,
                # the exporter died. The stream says so in band and ends; it does not retry,
                # because the thing it would retry against is gone.
                await send(
                    {"type": "websocket.send", "text": json.dumps({"type": "failed", "reason": str(error)})}
                )
                return
            if chunk:
                await send({"type": "websocket.send", "bytes": chunk.encode()})

    async def _pump(self, receive: Any) -> None:
        """Everything the other end sends: bytes to the console, text as control."""
        while True:
            message = await receive()
            kind = message["type"]
            if kind == "websocket.disconnect":
                return
            if kind != "websocket.receive":
                continue
            data = message.get("bytes")
            if data is not None:
                try:
                    await self._off_loop(lambda: self._exporter.console_send(data.decode(errors="replace")))
                except ExporterError:
                    return
                continue
            text = message.get("text")
            if not text:
                continue
            try:
                control = json.loads(text)
            except ValueError:
                continue
            # A console has no size, so `resize` is nothing to do rather than an error;
            # `kill` ends the STREAM and not the claim, because the exporter is still yours
            # after you close a terminal on it.
            if isinstance(control, dict) and control.get("type") == "kill":
                return
