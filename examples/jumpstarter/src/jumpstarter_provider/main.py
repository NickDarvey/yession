"""`jumpstarter-provider` — an MCP server that lends a Jumpstarter exporter to an agent.

A SEPARATE PROCESS from both halves it joins, and for the same reason the serial example is
one: the exporter owns hardware and hands it out once, so somebody has to arbitrate, and
that somebody cannot be a thing that comes and goes with a session.

It knows nothing about the client that will call it. Point any MCP client at the control url
below and it works.
"""

from __future__ import annotations

import os
import sys

import uvicorn

from . import __version__
from .provider import DEFAULT_CLAIM_TTL_SECONDS, create

USAGE = f"""jumpstarter-provider {__version__}

An MCP server offering a Jumpstarter exporter as eight tools.

Configured entirely by the environment:
  JUMPSTARTER_PROVIDER_PORT     port to bind          (default 7334; 0 = OS-assigned)
  JUMPSTARTER_PROVIDER_HOST     interface to bind     (default 127.0.0.1)
  JUMPSTARTER_HOST              the exporter's gRPC address (default 127.0.0.1:8815)
  JUMPSTARTER_PROVIDER_CONSOLE  which driver is the serial console (default serial)
  JUMPSTARTER_PROVIDER_TTL      seconds of client silence that release a claim \
(default {int(DEFAULT_CLAIM_TTL_SECONDS)})
  JUMPSTARTER_PROVIDER_ORIGIN   the ws:// origin clients should attach to, when the
                                provider sits behind something (default: what it bound)"""


def main() -> None:
    # `--version` and `--help` answer before anything is read: a running provider should
    # never be anonymous, and this is the one thing it will do without a port. Everything
    # else is refused rather than ignored — a flag that silently does nothing is how a
    # deployment ends up not being what its operator wrote down.
    match sys.argv[1:]:
        case ["--version"] | ["-v"]:
            print(__version__)
            return
        case ["--help"] | ["-h"]:
            print(USAGE)
            return
        case []:
            pass
        case unknown:
            print(f"jumpstarter-provider: unexpected argument '{unknown[0]}'\n", file=sys.stderr)
            print(USAGE, file=sys.stderr)
            raise SystemExit(2)

    host = os.environ.get("JUMPSTARTER_PROVIDER_HOST", "127.0.0.1")
    port = int(os.environ.get("JUMPSTARTER_PROVIDER_PORT", "7334"))
    exporter = os.environ.get("JUMPSTARTER_HOST", "127.0.0.1:8815")
    console = os.environ.get("JUMPSTARTER_PROVIDER_CONSOLE", "serial")
    ttl = float(os.environ.get("JUMPSTARTER_PROVIDER_TTL", DEFAULT_CLAIM_TTL_SECONDS))

    # The data leg's address, resolved LATE — port 0 is a legitimate ask (every test makes
    # it) and a provider does not know its own address until it is listening. An operator
    # who puts this behind something says so, exactly as the serial example allows.
    bound_origin = os.environ.get("JUMPSTARTER_PROVIDER_ORIGIN", "")
    origins: list[str] = [bound_origin]

    provider = create(
        host=exporter,
        console=console,
        ttl_seconds=ttl,
        version=__version__,
        origin=lambda: origins[0],
    )

    # One line, printed once the port is BOUND rather than before, because
    # JUMPSTARTER_PROVIDER_PORT=0 is a legitimate ask (every test does it) and a line
    # printed early would name port 0. It carries the control url and the exporter behind
    # it, which is all an operator declaring this to a client needs.
    class _Announce(uvicorn.Server):
        async def startup(self, sockets: list | None = None) -> None:  # type: ignore[override]
            await super().startup(sockets)
            bound = self.servers[0].sockets[0].getsockname()[1]
            if not origins[0]:
                origins[0] = f"ws://{host}:{bound}"
            print(
                f"jumpstarter-provider {__version__}: "
                f"MCP at http://{host}:{bound}/mcp, streams at {origins[0]}/attach/<token>, "
                f"exporter at {exporter}",
                flush=True,
            )

    config = uvicorn.Config(provider.app(), host=host, port=port, log_level="warning")
    _Announce(config).run()
