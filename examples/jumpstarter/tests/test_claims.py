"""Who holds the exporter — the rule, and then the rule over HTTP.

The first half needs no exporter and no server: a claim is a decision about time, and a
fake clock is how a five-minute timeout is tested in a millisecond.
"""

from __future__ import annotations

import time

from conftest import Client

from jumpstarter_provider.provider import Claims


class Clock:
    def __init__(self) -> None:
        self.now = 1000.0

    def __call__(self) -> float:
        return self.now

    def advance(self, seconds: float) -> None:
        self.now += seconds


def test_one_holder_at_a_time():
    claims = Claims(ttl_seconds=60, now=Clock())
    assert claims.acquire("alice") == (True, None)
    # The refusal carries WHO, because that is the difference between "wait" and "debug".
    assert claims.acquire("bob") == (False, "alice")
    # Acquiring twice is not an error: a client that lost track of its own claim still has it.
    assert claims.acquire("alice") == (True, None)


def test_a_claim_is_released_only_by_its_holder():
    claims = Claims(ttl_seconds=60, now=Clock())
    claims.acquire("alice")
    assert claims.release("bob") is False
    assert claims.holder == "alice"
    assert claims.release("alice") is True
    assert claims.holder is None


def test_silence_releases_a_claim_and_talking_keeps_it():
    clock = Clock()
    released = []
    claims = Claims(ttl_seconds=60, now=clock, on_release=lambda: released.append(True))
    claims.acquire("alice")

    # A client that keeps talking keeps its claim, however long it works for.
    for _ in range(10):
        clock.advance(59)
        claims.touch("alice")
    assert claims.holder == "alice"
    assert released == []

    # A client that stops — crashed, killed, unplugged — loses it without having to say so.
    clock.advance(61)
    assert claims.holder is None
    assert released == [True]


def test_somebody_elses_traffic_does_not_keep_a_claim_alive():
    clock = Clock()
    claims = Claims(ttl_seconds=60, now=clock)
    claims.acquire("alice")
    clock.advance(61)
    # Bob's request arrives after Alice went quiet: it must not renew Alice's claim, and
    # the exporter must be free for whoever asks next.
    claims.touch("bob")
    assert claims.holder is None
    assert claims.acquire("bob") == (True, None)


def test_an_open_stream_keeps_a_claim_alive():
    clock = Clock()
    claims = Claims(ttl_seconds=60, now=clock)
    claims.acquire("alice")
    with claims.streaming("alice"):
        # Alice says nothing at all for far longer than the timeout. She is holding a
        # stream open, which is the same statement made by listening instead of asking.
        clock.advance(600)
        assert claims.holder == "alice"


def test_a_claim_expires_once_the_stream_is_gone():
    clock = Clock()
    claims = Claims(ttl_seconds=60, now=clock)
    claims.acquire("alice")
    with claims.streaming("alice"):
        pass
    clock.advance(61)
    assert claims.holder is None


def test_a_closed_stream_leaves_a_whole_deadline_behind_it():
    clock = Clock()
    claims = Claims(ttl_seconds=60, now=clock)
    claims.acquire("alice")
    with claims.streaming("alice"):
        clock.advance(600)
    # The deadline runs from the stream CLOSING, not from the last request before it. A
    # client whose stream outlived one timeout is still entitled to the whole of the next.
    clock.advance(59)
    assert claims.holder == "alice"


def test_one_of_two_streams_closing_does_not_end_a_claim():
    clock = Clock()
    claims = Claims(ttl_seconds=60, now=clock)
    claims.acquire("alice")
    with claims.streaming("alice"):
        with claims.streaming("alice"):
            pass
        clock.advance(600)
        assert claims.holder == "alice"


def test_somebody_elses_stream_does_not_keep_a_claim_alive():
    clock = Clock()
    claims = Claims(ttl_seconds=60, now=clock)
    claims.acquire("alice")
    with claims.streaming("bob"):
        clock.advance(61)
        assert claims.holder is None


def test_two_clients_over_the_wire(provider: str):
    alice = Client(provider, name="alice")
    bob = Client(provider, name="bob")
    assert alice.session_id and bob.session_id != alice.session_id

    assert "yours" in alice.tool("acquire")
    refused = bob.tool("acquire")
    assert alice.session_id[:8] in refused
    assert "in use, not broken" in refused

    # And bob cannot reach the hardware around the refusal.
    assert alice.session_id[:8] in bob.tool("power", action="on")

    assert "released" in alice.tool("release")
    assert "yours" in bob.tool("acquire")


def test_a_client_that_goes_quiet_loses_the_exporter(provider: str):
    alice = Client(provider, name="alice")
    alice.tool("acquire")

    # The provider under test holds a five-second claim (conftest), so this is the real
    # timeout expiring rather than a mocked one — bob asks after alice has gone quiet.
    time.sleep(5.5)
    bob = Client(provider, name="bob")
    assert "yours" in bob.tool("acquire")


def test_a_client_holding_a_stream_open_keeps_the_exporter(provider: str):
    alice = Client(provider, name="alice")
    alice.tool("acquire")

    with alice.stream():
        # Alice sends nothing for longer than the provider's five-second claim (conftest).
        # A client measured by requests alone would have lost the board here; she is
        # holding a stream open, and that is the same statement made by listening.
        time.sleep(5.5)
        bob = Client(provider, name="bob")
        assert alice.session_id[:8] in bob.tool("acquire")


def test_ending_a_session_is_not_a_way_to_keep_the_hardware(provider: str):
    alice = Client(provider, name="alice")
    alice.tool("acquire")
    assert alice.end() in (200, 202, 204)

    # The session is gone from the server's point of view, and the claim it held goes with
    # it once it stops talking — which is what stops a crashed client owning a board.
    time.sleep(5.5)
    bob = Client(provider, name="bob")
    assert "yours" in bob.tool("acquire")
