"""What a name IS, decided without running it.

Pure: hand-written classes, no exporter, no sockets. The interesting cases are ones a real
driver cannot demonstrate safely — a property that would be destructive to evaluate is exactly
the thing you must not evaluate to find out it is a property.
"""

from __future__ import annotations

import pytest

from jumpstarter_provider.introspect import Refusal, offered, resolve

# Stand-in for `DriverClient`: the SDK plumbing every driver carries and none of it is what an
# exporter is offering.
BASE = {"call", "call_async", "close", "stream", "wait_for_lease_ready"}


class Fake:
    """A driver client with one of everything."""

    detonated = False

    def on(self) -> None: ...

    def off(self) -> None: ...

    async def get_status_async(self) -> None: ...

    def read(self): ...

    async def stream_async(self): ...

    # Inherited plumbing, by name — the class does not need the SDK to stand in for it.
    def call(self): ...

    def stream(self): ...

    @property
    def status(self):
        # If classifying a name ever evaluates it, this records the fact instead of letting
        # the test pass quietly.
        Fake.detonated = True
        return None


@pytest.fixture(autouse=True)
def _reset():
    Fake.detonated = False


def test_offers_the_drivers_methods_and_not_the_sdks():
    assert offered(Fake, base=BASE) == ["off", "on", "read"]


def test_a_property_is_never_evaluated_to_classify_it():
    offered(Fake, base=BASE)
    resolve(Fake, "status", "fake", base=BASE)
    # The getter is the loudest possible side effect: on real hardware it is a request to a
    # board. Nothing that merely asks "is this a method?" may cause one.
    assert Fake.detonated is False


def test_a_refusal_says_what_the_name_is():
    property_refusal = resolve(Fake, "status", "fake", base=BASE)
    assert isinstance(property_refusal, Refusal)
    assert "property" in property_refusal.reason

    async_refusal = resolve(Fake, "get_status_async", "fake", base=BASE)
    assert isinstance(async_refusal, Refusal)
    assert "asynchronous" in async_refusal.reason


def test_every_refusal_quotes_the_same_list_the_listing_prints():
    listing = ", ".join(offered(Fake, base=BASE))
    for name in ("status", "get_status_async", "nonexistent"):
        refusal = resolve(Fake, name, "fake", base=BASE)
        assert isinstance(refusal, Refusal)
        # The two answers to "what does this driver offer" have to be one answer: a refusal
        # that advertises names the listing hides is how a caller ends up invoking the SDK's
        # own async plumbing and getting an unresolved coroutine back.
        assert listing in refusal.reason
        assert "status" not in refusal.reason.split(":")[-1]


def test_a_method_resolves_to_something_callable():
    found = resolve(Fake, "on", "fake", base=BASE)
    assert not isinstance(found, Refusal)
    # Unbound, off the class — the caller binds it with getattr once this has agreed there is
    # something there.
    assert found is Fake.__dict__["on"]
