"""What a driver offers, answered once.

Two rules, and every defect this file exists to fix was a violation of one of them.

**Classify on the CLASS, call on the instance.** Asking `getattr(client, name)` whether
something is a method RUNS it when it is a property — `PowerClient.status` is a property, so
merely looking for a method called `status` evaluated a live driver's getter and then, because
its value was not callable, reported that no such name existed. A class attribute answers the
same question without touching the driver.

**One list, two readers.** The listing a caller asks for and the list a refusal quotes are the
same call to `offered`. They used to be computed separately — the listing subtracted the SDK's
base class, the refusal did not — so a refusal named thirty names the listing hid, including
the async plumbing that produced an unresolved coroutine when somebody took the suggestion.
"""

from __future__ import annotations

import inspect
from dataclasses import dataclass
from typing import Any, Callable


@dataclass(frozen=True)
class Refusal:
    """Why a name is not something this provider can call, in the caller's words."""

    reason: str


def _base_names() -> set[str]:
    """Everything every driver client carries, which is not what this exporter offers.

    Imported here rather than at module scope so the pure tests can exercise this file
    without the SDK installed.
    """
    from jumpstarter.client import DriverClient

    return set(dir(DriverClient))


def offered(cls: type, *, base: set[str] | None = None) -> list[str]:
    """The methods of `cls` a caller can actually invoke through `driver_call`.

    Excluded, and each for its own reason: anything the SDK's `DriverClient` base carries
    (plumbing — `call_async`, `stream`, `wait_for_lease_ready`); anything asynchronous (this
    provider calls synchronous methods, and returning an un-awaited coroutine is worse than
    refusing); and properties (a value, and evaluating one to find out is a side effect on
    live hardware).
    """
    base = _base_names() if base is None else base
    names = []
    for name in dir(cls):
        if name.startswith("_") or name in base:
            continue
        attribute = inspect.getattr_static(cls, name, None)
        if isinstance(attribute, (staticmethod, classmethod)):
            attribute = attribute.__func__
        if not inspect.isfunction(attribute):
            continue
        if inspect.iscoroutinefunction(attribute) or inspect.isasyncgenfunction(attribute):
            continue
        names.append(name)
    return sorted(names)


def resolve(cls: type, name: str, driver: str, *, base: set[str] | None = None) -> Callable[..., Any] | Refusal:
    """The function to call, or why there is not one.

    A refusal says what the name IS, not merely that it is not what you wanted: "it is a
    property" and "it is asynchronous" are the two answers that would otherwise send a caller
    round the loop that produced this function.

    The function comes back UNBOUND, off the class. Callers bind it with an ordinary
    `getattr` on the instance once this has agreed there is something to bind — which is safe
    precisely because it has: a `getattr` only surprises you when the name turns out to be a
    property.
    """
    attribute = inspect.getattr_static(cls, name, None)

    if isinstance(attribute, (staticmethod, classmethod)):
        attribute = attribute.__func__

    if isinstance(attribute, property):
        return Refusal(
            f"'{name}' is a property of the SDK's driver client, not a driver method — "
            f"there is nothing to call. {driver} offers: {', '.join(offered(cls, base=base))}"
        )

    if inspect.iscoroutinefunction(attribute) or inspect.isasyncgenfunction(attribute):
        return Refusal(
            f"'{name}' is asynchronous, and this provider calls synchronous driver methods. "
            f"{driver} offers: {', '.join(offered(cls, base=base))}"
        )

    if not inspect.isfunction(attribute):
        return Refusal(f"no method '{name}' on {driver} — it offers: {', '.join(offered(cls, base=base))}")

    return attribute
