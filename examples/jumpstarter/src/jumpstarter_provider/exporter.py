"""The Jumpstarter SDK, behind one seam.

Everything that knows what jumpstarter IS lives here, and it is all in one place for a
reason that is not tidiness: the SDK is synchronous and context-managed, and MCP is
asynchronous and request-shaped. A jumpstarter client is a `with` block; the console is a
`with` block inside it; and both have to stay open across HTTP requests that arrive on an
event loop and expect to return in milliseconds.

So the connection lives on a thread of its own, and requests reach it as commands on a
queue. One thread, one owner of every context manager, entered once and exited once — which
is the only arrangement a context manager and a request/response protocol can both live
with. The provider never touches the SDK; it asks this.
"""

from __future__ import annotations

import inspect
import os
import queue
import threading
import time
from dataclasses import dataclass, field
from typing import Any, Callable

import pexpect

from . import introspect


class ExporterError(Exception):
    """Something the caller should be told in prose, not a traceback."""


@dataclass
class _Command:
    run: Callable[["_Connection"], Any]
    reply: "queue.Queue[tuple[bool, Any]]" = field(default_factory=queue.Queue)


class _Connection:
    """The SDK objects, only ever touched from the worker thread."""

    def __init__(self, client: Any, console_name: str) -> None:
        self.client = client
        self.console_name = console_name
        self._console_cm: Any = None
        self.console: Any = None

    def open_console(self) -> Any:
        """The console, opened on first use and held for the life of the claim.

        Lazily, because a claim on the exporter is not a claim on its console: a caller
        that only power-cycles should not be holding a serial stream, and on real hardware
        opening one can assert DTR and reset the board.
        """
        if self.console is None:
            driver = getattr(self.client, self.console_name, None)
            if driver is None:
                raise ExporterError(
                    f"this exporter exports no '{self.console_name}' driver — "
                    "call status to see what it does export"
                )
            self._console_cm = driver.pexpect()
            self.console = self._console_cm.__enter__()
        return self.console

    def close_console(self) -> None:
        console, self._console_cm = self._console_cm, None
        self.console = None
        if console is not None:
            console.__exit__(None, None, None)


class Exporter:
    """A live connection to one exporter, usable from an event loop.

    Construction connects: a provider that cannot reach its exporter should say so on the
    first tool call rather than at boot, so `connect` is called lazily and its failure is an
    answer rather than a crash.
    """

    def __init__(self, host: str, console_name: str) -> None:
        self.host = host
        self.console_name = console_name
        self._commands: queue.Queue[_Command | None] = queue.Queue()
        self._thread: threading.Thread | None = None
        self._ready: queue.Queue[tuple[bool, Any]] = queue.Queue()

    # --- the thread ------------------------------------------------------------------

    def _serve(self) -> None:
        # The SDK reads its whole configuration from the environment, and this is the only
        # deployment this example is honest about: a direct, insecure, loopback exporter.
        # Setting them here rather than asking the operator to means one fewer way to have a
        # provider that starts and then cannot talk to anything.
        os.environ["JUMPSTARTER_HOST"] = self.host
        os.environ["JMP_GRPC_INSECURE"] = "1"
        # The exporter's report names the driver client classes to import. Trusting it is
        # the trust already implied by running the exporter — same box, same operator.
        os.environ["JMP_DRIVERS_ALLOW"] = "UNSAFE"

        from jumpstarter.utils.env import env  # imported here: it costs ~a second

        try:
            with env() as client:
                connection = _Connection(client, self.console_name)
                self._ready.put((True, None))
                try:
                    while True:
                        command = self._commands.get()
                        if command is None:
                            return
                        try:
                            command.reply.put((True, command.run(connection)))
                        except Exception as error:  # noqa: BLE001 — an answer, not a crash
                            command.reply.put((False, error))
                finally:
                    connection.close_console()
        except Exception as error:  # noqa: BLE001
            self._ready.put((False, error))
            # Whatever is queued behind a failed connection gets the same answer, rather
            # than blocking forever on a thread that has gone.
            while True:
                command = self._commands.get()
                if command is None:
                    return
                command.reply.put((False, error))

    def _ensure(self) -> None:
        if self._thread is not None and self._thread.is_alive():
            return
        self._thread = threading.Thread(target=self._serve, name="jumpstarter", daemon=True)
        self._thread.start()
        ok, error = self._ready.get(timeout=60)
        if not ok:
            self._thread = None
            raise ExporterError(f"cannot reach the exporter at {self.host}: {error}")

    def _do(self, run: Callable[[_Connection], Any], timeout: float = 120.0) -> Any:
        self._ensure()
        command = _Command(run=run)
        self._commands.put(command)
        try:
            ok, value = command.reply.get(timeout=timeout)
        except queue.Empty:
            # The thread is still inside that call — a driver method that never returns, or a
            # stream whose next item never arrives. Abandon it rather than queue behind it,
            # so the next call gets a fresh connection instead of inheriting the wedge. The
            # abandoned thread is a daemon and holds one SDK client until the process ends,
            # which is the honest cost of not being able to interrupt somebody else's library.
            self._thread = None
            self._ready = queue.Queue()
            raise ExporterError(
                "the exporter did not answer in time — that connection was dropped, "
                "so the next call reconnects"
            ) from None
        if not ok:
            raise ExporterError(str(value))
        return value

    def close(self) -> None:
        """Drop the connection. The next call reconnects."""
        if self._thread is not None and self._thread.is_alive():
            self._commands.put(None)
            self._thread.join(timeout=10)
        self._thread = None
        self._ready = queue.Queue()

    # --- what the provider asks ------------------------------------------------------

    def drivers(self) -> list[str]:
        """The names this exporter exports, one line each, with their type."""

        def run(connection: _Connection) -> list[str]:
            return [
                f"{name} — {type(child).__module__}.{type(child).__name__}"
                for name, child in connection.client.children.items()
            ]

        return self._do(run)

    def methods(self, driver: str) -> list[str]:
        """What this driver offers — see `introspect.offered` for what that excludes."""

        def run(connection: _Connection) -> list[str]:
            return introspect.offered(type(_resolve(connection.client, driver)))

        return self._do(run)

    def call(self, driver: str, method: str, args: list[Any]) -> str:
        """Call a driver method and describe what came back.

        The answer is a STRING rather than the value, because what came back is not always a
        value: a streaming method hands over a generator, and a caller shown
        `<generator object …>` has been told nothing. Shaping it here keeps the tool layer
        free of SDK shapes.
        """

        def run(connection: _Connection) -> str:
            target = _resolve(connection.client, driver)
            found = introspect.resolve(type(target), method, driver)
            if isinstance(found, introspect.Refusal):
                raise ExporterError(found.reason)
            return _describe(getattr(target, method)(*args))

        return self._do(run)

    def console_send(self, data: str) -> None:
        def run(connection: _Connection) -> None:
            connection.open_console().send(data.encode())

        self._do(run)

    def console_read(self, timeout: float) -> str:
        """Whatever the console has said SINCE THE LAST READ, up to a quiet period.

        "Since the last read" is the promise, so the read has to consume what it returns —
        see `_drain`, which is where that turns out to be the harder half.
        """

        def run(connection: _Connection) -> str:
            return _drain(connection.open_console(), timeout)

        return self._do(run, timeout=timeout + 60.0)

    def close_console_quietly(self) -> None:
        """Close the console without waiting for it, and without caring if it fails.

        Called when a claim ends — which can happen on the event loop, in the middle of
        somebody else's request, and by then there is nobody left to tell. A connection
        that has already gone has no console to close either.
        """
        if self._thread is None or not self._thread.is_alive():
            return
        self._commands.put(_Command(run=lambda connection: connection.close_console()))


# How much of a stream is an answer. A power line samples forever, so "drain it" has to mean
# "take enough to see, then stop": whichever of these comes first, and the caller is told when
# it was the cap rather than the end.
MAX_ITEMS = 10
DRAIN_SECONDS = 5.0


def _describe(result: Any) -> str:
    """What a driver method returned, in a form a model can read.

    A generator is the case that matters: `power.read()` is the only measurement jumpstarter's
    power interface offers, and it arrives as a stream. Draining it here is what turns "the
    board's power state" from `<generator object …>` into volts and amps.
    """
    if inspect.iscoroutine(result):
        # Close it rather than leak it: an un-awaited coroutine warns at garbage-collection
        # time, in a log nobody is reading, long after the call that made it.
        result.close()
        return "that method is asynchronous, and this provider calls synchronous ones — nothing was run"
    if inspect.isasyncgen(result):
        return "that method streams asynchronously, which this provider cannot drain — nothing was read"
    if inspect.isgenerator(result):
        taken: list[Any] = []
        deadline = time.monotonic() + DRAIN_SECONDS
        truncated = False
        try:
            for item in result:
                taken.append(item)
                if len(taken) >= MAX_ITEMS or time.monotonic() >= deadline:
                    truncated = True
                    break
        finally:
            result.close()
        if not taken:
            return "the stream ended without yielding anything"
        body = ", ".join(repr(item) for item in taken)
        return f"{body} (still going — first {len(taken)})" if truncated else body
    return repr(result)


def _resolve(client: Any, path: str) -> Any:
    """`power`, or `bench.power` — a dot path into the driver tree."""
    target = client
    for part in path.split("."):
        children = getattr(target, "children", {})
        if part not in children:
            known = ", ".join(children) or "nothing"
            raise ExporterError(f"no driver '{path}' — at '{part}' this exporter has {known}")
        target = children[part]
    return target


def _text(value: Any) -> str:
    if value is None:
        return ""
    if isinstance(value, bytes):
        return value.decode(errors="replace")
    return str(value)


# How long a read waits for MORE, once something has arrived. A caller asking for "what it
# said and then stopped" wants the pause that ENDED it, not the timeout it was willing to
# wait: a device that answers in 20ms should not cost two seconds to hear.
QUIET_SECONDS = 0.05


def _drain(console: Any, timeout: float) -> str:
    """Everything the console has said, CONSUMED.

    `(?s).+` and not `[pexpect.TIMEOUT]`, which is the same call with the opposite effect.
    pexpect consumes what it MATCHED and leaves the buffer alone when it times out, so an
    expect for TIMEOUT hands back `before` — the whole buffer — and keeps it. Every read
    then answered with everything the console had ever said, growing without bound; the
    stream re-sent that history five times a second; and the next `expect` matched a prompt
    from ten minutes ago and reported it as having just arrived.

    Matching a real pattern consumes it, which is the whole of the fix.
    """
    chunks: list[str] = []
    wait = timeout
    while True:
        if console.expect([r"(?s).+", pexpect.TIMEOUT], timeout=wait) == 1:
            return "".join(chunks)
        chunks.append(_text(console.after))
        wait = QUIET_SECONDS
