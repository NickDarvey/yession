"""The data leg: the console as a stream somebody can attach to.

What is worth pinning here is not "a WebSocket works". It is the three properties that make
a console safe to share between a person at a terminal and an agent holding a claim:

  * the offer is on the RESULT, where a client reads it, and the address in it opens;
  * one drain, two readers — a terminal being open must not blind `serial_read`, which is
    the whole reason the drain writes to a buffer as well as to the socket;
  * one writer — while a terminal is attached, `serial_send` sends the caller there, because
    two doors onto one console is exactly what the terminal's lease exists to prevent.
"""

from __future__ import annotations

import json

import pytest
from websockets.sync.client import connect as _connect

from conftest import Client

# The exporter's serial line is `loop://`: everything written to it comes back. That is what
# makes a round trip assertable without hardware, and it is the same line the tool-level
# suite leans on.
PROMPT = "hello-stream\n"


def connect(url: str, **kwargs):
    """The stream, dialled directly. `proxy=None` because this address is loopback and a
    developer box may well have a proxy in the environment for everything else — a test that
    picked one up would be measuring the proxy."""
    return _connect(url, proxy=None, **kwargs)


def _offer(client: Client) -> dict:
    text, meta = client.result("acquire")
    assert "yours" in text
    offer = meta.get("dev.yession/stream")
    assert offer is not None, f"acquire offered no stream: {meta}"
    return offer


def test_acquire_offers_a_stream_a_client_can_open(provider: str) -> None:
    client = Client(provider)
    offer = _offer(client)
    assert offer["url"].startswith("ws://")
    assert "/attach/" in offer["url"]
    # Capabilities are absent, which is the conservative reading and the honest one: a
    # console has no size, no prompt of ours to bootstrap and no exit code.
    assert "capabilities" not in offer
    assert offer["renewable"] is True
    client.end()


def test_bytes_go_both_ways_and_the_tools_still_see_them(provider: str) -> None:
    client = Client(provider)
    offer = _offer(client)
    with connect(offer["url"], open_timeout=30) as socket:
        socket.send(PROMPT.encode())
        # The socket sees it, because `loop://` echoes.
        seen = ""
        while PROMPT.strip() not in seen:
            frame = socket.recv(timeout=30)
            if isinstance(frame, bytes):
                seen += frame.decode()
        # And so does the tool, from the same drain — a terminal being open must not take
        # the console away from the agent that holds the claim.
        assert PROMPT.strip() in client.tool("serial_read", timeout_seconds=2.0)
    client.end()


def test_while_a_terminal_is_attached_writes_have_one_door(provider: str) -> None:
    client = Client(provider)
    offer = _offer(client)
    with connect(offer["url"], open_timeout=30) as socket:
        answer = client.tool("serial_send", data="ignored\n")
        assert "attached to a terminal" in answer
        assert "write_terminal" in answer
        # Refused BEFORE the write: a refusal that had already sent the bytes would be worse
        # than no refusal at all.
        socket.send(b"x\n")
        assert "ignored" not in client.tool("serial_read", timeout_seconds=1.0)
    client.end()


def test_expect_still_answers_from_the_stream(provider: str) -> None:
    client = Client(provider)
    offer = _offer(client)
    with connect(offer["url"], open_timeout=30) as socket:
        socket.send(b"BANNER v1\n")
        assert "BANNER" in client.tool("serial_expect", pattern="BANNER v[0-9]", timeout_seconds=20.0)
    client.end()


def test_a_kill_ends_the_stream_in_band(provider: str) -> None:
    client = Client(provider)
    offer = _offer(client)
    with connect(offer["url"], open_timeout=30) as socket:
        socket.send(json.dumps({"type": "kill"}))
        ending = None
        while ending is None:
            frame = socket.recv(timeout=30)
            if isinstance(frame, str):
                ending = json.loads(frame)
        # An in-band frame and THEN a close, because an abnormal closure carries nothing and
        # a client cannot tell "it ended" from "the network did".
        assert ending == {"type": "exited", "code": 0}
    client.end()


def test_a_spent_token_cannot_be_used_twice(provider: str) -> None:
    client = Client(provider)
    offer = _offer(client)
    with connect(offer["url"], open_timeout=30) as first:
        first.send(b"first\n")
        with connect(offer["url"], open_timeout=30) as second:
            # The upgrade succeeds — the refusal is what the provider SAYS, because by then
            # the socket is open and that is the only thing it can say.
            frame = second.recv(timeout=30)
            assert json.loads(frame) == {"type": "exited", "code": 1}
    client.end()


def test_asking_again_after_a_stream_ends_gets_one_that_works(provider: str) -> None:
    client = Client(provider)
    first = _offer(client)
    with connect(first["url"], open_timeout=30) as socket:
        socket.send(json.dumps({"type": "kill"}))
        with pytest.raises(Exception):
            while True:
                socket.recv(timeout=30)

    # `renewable` is a promise about what asking again does. A spent token makes it easy to
    # break, so it is checked by USING the second one.
    again = _offer(client)
    assert again["url"] != first["url"]
    with connect(again["url"], open_timeout=30) as socket:
        socket.send(PROMPT.encode())
        seen = ""
        while PROMPT.strip() not in seen:
            frame = socket.recv(timeout=30)
            if isinstance(frame, bytes):
                seen += frame.decode()
    client.end()


def test_a_stream_is_not_offered_while_one_is_open(provider: str) -> None:
    client = Client(provider)
    offer = _offer(client)
    with connect(offer["url"], open_timeout=30):
        text, meta = client.result("acquire")
        assert "already attached" in text
        assert "dev.yession/stream" not in meta
    client.end()
