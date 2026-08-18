"""A real exporter, a real provider, and a client that speaks the wire.

Nothing here is a double. The exporter is upstream's `jmp run` in direct mode, the provider
is the one that ships, and the client is HTTP — because the contract under test is a
protocol, and a test that called the tool functions directly would prove only that Python
can call Python.
"""

from __future__ import annotations

import json
import socket
import subprocess
import threading
import time
import urllib.error
import urllib.request
from contextlib import contextmanager
from pathlib import Path

import pytest
import uvicorn

from jumpstarter_provider.provider import create

HERE = Path(__file__).parent


def _free_port() -> int:
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return int(sock.getsockname()[1])


def _wait_for_port(port: int, deadline: float, what: str, process: subprocess.Popen | None = None) -> None:
    while time.monotonic() < deadline:
        if process is not None and process.poll() is not None:
            raise RuntimeError(f"{what} exited with {process.returncode}")
        with socket.socket() as sock:
            sock.settimeout(0.25)
            if sock.connect_ex(("127.0.0.1", port)) == 0:
                return
        time.sleep(0.1)
    raise RuntimeError(f"{what} never bound port {port}")


@pytest.fixture(scope="session")
def exporter() -> str:
    """A direct-mode exporter on loopback: MockPower and a loopback serial line."""
    port = _free_port()
    process = subprocess.Popen(
        [
            "jmp",
            "run",
            "--exporter-config",
            str(HERE / "exporter.yaml"),
            "--tls-grpc-listener",
            f"127.0.0.1:{port}",
            "--tls-grpc-insecure",
        ],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )
    try:
        _wait_for_port(port, time.monotonic() + 60, "the exporter", process)
        yield f"127.0.0.1:{port}"
    finally:
        process.terminate()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()


@pytest.fixture
def provider(exporter: str) -> str:
    """The provider, served on an OS-assigned port for the length of one test.

    Per test rather than per session, because the claim is the thing under test and a
    claim that leaked from the previous test would be a mystery in this one.
    """
    # Short enough that a test can watch a claim expire without sleeping for minutes, and
    # comfortably longer than the slowest single call (a power cycle pauses for two
    # seconds): liveness is refreshed when a request ARRIVES, so a claim must outlast one.
    port = _free_port()
    # The provider's own stream address, which it cannot work out for itself: it is served
    # here rather than by `main`, and a data leg with no address would offer a url nobody
    # can open.
    built = create(
        host=exporter,
        console="serial",
        ttl_seconds=5.0,
        version="test",
        origin=lambda: f"ws://127.0.0.1:{port}",
    )
    config = uvicorn.Config(built.app(), host="127.0.0.1", port=port, log_level="error")
    server = uvicorn.Server(config)
    thread = threading.Thread(target=server.run, daemon=True)
    thread.start()
    try:
        _wait_for_port(port, time.monotonic() + 30, "the provider")
        yield f"http://127.0.0.1:{port}/mcp"
    finally:
        server.should_exit = True
        thread.join(timeout=15)
        built.exporter.close()


class Client:
    """One MCP session over Streamable HTTP, longhand.

    Deliberately not the MCP SDK's client: this suite's job is to prove what goes over the
    wire, and a client from the same package as the server agrees with it by construction.
    """

    def __init__(self, url: str, name: str = "suite") -> None:
        self.url = url
        self.session_id = ""
        self.protocol = "2025-06-18"
        self._next_id = 0
        result, headers = self._post(
            {
                "jsonrpc": "2.0",
                "id": self._id(),
                "method": "initialize",
                "params": {
                    "protocolVersion": self.protocol,
                    "capabilities": {},
                    "clientInfo": {"name": name, "version": "0"},
                },
            }
        )
        self.session_id = headers.get("mcp-session-id", "")
        self.server_protocol = result["result"]["protocolVersion"]
        self.notify("notifications/initialized")

    def _id(self) -> int:
        self._next_id += 1
        return self._next_id

    def _post(self, body: dict, method: str = "POST") -> tuple[dict, dict]:
        request = urllib.request.Request(
            self.url,
            data=json.dumps(body).encode() if body else None,
            method=method,
            headers={
                "content-type": "application/json",
                # Both content types the spec allows a server to answer with: a client that
                # offered one would work against half of them.
                "accept": "application/json, text/event-stream",
                **({"mcp-session-id": self.session_id} if self.session_id else {}),
                **({"mcp-protocol-version": self.protocol} if self.session_id else {}),
            },
        )
        with urllib.request.urlopen(request, timeout=180) as response:
            raw = response.read().decode()
            headers = {name.lower(): value for name, value in response.headers.items()}
        return (_frame(raw), headers)

    def notify(self, method: str) -> None:
        self._post({"jsonrpc": "2.0", "method": method})

    def call(self, method: str, params: dict | None = None) -> dict:
        result, _ = self._post({"jsonrpc": "2.0", "id": self._id(), "method": method, "params": params or {}})
        return result

    def tools(self) -> list[str]:
        return [tool["name"] for tool in self.call("tools/list")["result"]["tools"]]

    def tool(self, name: str, **arguments) -> str:
        return self.result(name, **arguments)[0]

    def result(self, name: str, **arguments) -> tuple[str, dict]:
        """(the prose a model reads, the `_meta` a client reads). Two audiences, and this
        suite is the only place both are checked against one wire."""
        answer = self.call("tools/call", {"name": name, "arguments": arguments})
        result = answer["result"]
        text = "\n".join(part.get("text", "") for part in result["content"])
        return text, result.get("_meta") or {}

    @contextmanager
    def stream(self):
        """The optional server->client GET stream, held open for the block.

        Opened rather than read: what a test needs from it is what an OPEN stream means to
        the server, and nothing has to arrive on it for that to be a question.
        """
        request = urllib.request.Request(
            self.url,
            method="GET",
            headers={
                "accept": "text/event-stream",
                "mcp-session-id": self.session_id,
                "mcp-protocol-version": self.protocol,
            },
        )
        response = urllib.request.urlopen(request, timeout=30)
        try:
            yield response
        finally:
            response.close()

    def end(self) -> int:
        request = urllib.request.Request(
            self.url,
            method="DELETE",
            headers={"mcp-session-id": self.session_id, "mcp-protocol-version": self.protocol},
        )
        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                return int(response.status)
        except urllib.error.HTTPError as error:
            return int(error.code)


def _frame(body: str) -> dict:
    """A POST is answered with either JSON or one SSE event carrying the same frame."""
    text = body.strip()
    if not text:
        return {}
    if text.startswith("{") or text.startswith("["):
        return json.loads(text)
    for line in text.replace("\r\n", "\n").split("\n"):
        if line.startswith("data: "):
            return json.loads(line[len("data: ") :])
    raise AssertionError(f"neither JSON nor an SSE frame: {body!r}")
