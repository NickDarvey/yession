"""The data leg: the console as a stream somebody can attach to.

What is worth pinning here is not "a WebSocket works". It is the three properties that make
a console safe to share between a person at a terminal and an agent holding a claim:

  * the offer is on the RESULT, where a client reads it, and the address in it opens;
  * the console is reached through the stream and nowhere else, so a claim ending on the
    control leg is visible on the data leg — one console, one door, and no second reader to
    keep in step.
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


def test_bytes_go_both_ways(provider: str) -> None:
    client = Client(provider)
    offer = _offer(client)
    with connect(offer["url"], open_timeout=30) as socket:
        socket.send(PROMPT.encode())
        # `loop://` echoes, so a round trip proves the whole path: the token, the drain, the
        # SDK's console, gRPC, the driver.
        seen = ""
        while PROMPT.strip() not in seen:
            frame = socket.recv(timeout=30)
            if isinstance(frame, bytes):
                seen += frame.decode()
    client.end()


def test_releasing_hands_the_console_back_clean(provider: str) -> None:
    """A claim ending closes the console, so the next holder does not inherit a stream
    mid-sentence.

    This used to be asserted through `serial_read`. The console is reached as a terminal now,
    so the claim is asserted where it actually lives — on the stream leg — which is a better
    test of the same invariant: it goes through the door a caller really uses.
    """
    first = Client(provider, name="first")
    offer = _offer(first)
    with connect(offer["url"], open_timeout=30) as socket:
        socket.send(b"left over\n")
        seen = ""
        while "left over" not in seen:
            frame = socket.recv(timeout=30)
            if isinstance(frame, bytes):
                seen += frame.decode()
        # Ended in band and waited for, rather than by walking out of the `with`. The offer
        # is suppressed while a stream is attached, and a closed socket is not the same
        # instant as a server that has noticed — the `exited` frame is sent after the stream
        # has let go, so seeing it is the only thing that makes the next acquire deterministic.
        socket.send(json.dumps({"type": "kill"}))
        while True:
            frame = socket.recv(timeout=30)
            if isinstance(frame, str) and json.loads(frame)["type"] == "exited":
                break
    first.tool("release")
    first.end()

    second = Client(provider, name="second")
    later = _offer(second)
    with connect(later["url"], open_timeout=30) as socket:
        # Nothing from the last holder: a fresh console has nothing to say until something
        # is said to it.
        try:
            frame = socket.recv(timeout=2)
            carried = frame.decode() if isinstance(frame, bytes) else frame
        except TimeoutError:
            carried = ""
        assert "left over" not in carried
    second.end()


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
