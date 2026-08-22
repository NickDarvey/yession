"""The tools, against a real exporter.

What these assert is the PROMISE of each tool — that it reaches the hardware, that it says
what happened, and that a refusal is legible. Not the wording of an answer, except where
the wording is the promise: a refusal that does not name the holder has not refused
usefully, and a timeout that does not carry what was seen has thrown the answer away.
"""

from __future__ import annotations

from conftest import Client

EXPECTED_TOOLS = {
    "status",
    "acquire",
    "release",
    "power",
    "driver_call",
    "serial_send",
    "serial_read",
    "serial_expect",
}


def test_the_handshake_is_the_version_the_client_asked_for(provider: str):
    client = Client(provider)
    # A client that speaks 2025-06-18 and is answered with something else records the
    # server as unreachable rather than negotiating, so this is the whole compatibility
    # question in one line.
    assert client.server_protocol == "2025-06-18"
    assert client.session_id


def test_every_tool_is_listed_with_a_description_and_a_schema(provider: str):
    client = Client(provider)
    tools = client.call("tools/list")["result"]["tools"]
    assert {tool["name"] for tool in tools} == EXPECTED_TOOLS
    for tool in tools:
        # A tool description is the only documentation its caller will ever see.
        assert tool.get("description", "").strip(), f"{tool['name']} has no description"
        assert tool["inputSchema"]["type"] == "object"


def test_status_needs_no_claim_and_names_the_drivers(provider: str):
    client = Client(provider)
    answer = client.tool("status")
    assert "power" in answer and "serial" in answer
    assert "nobody holds it" in answer


def test_the_hardware_is_out_of_reach_until_acquired(provider: str):
    client = Client(provider)
    for name, arguments in [
        ("power", {"action": "on"}),
        ("serial_send", {"data": "x"}),
        ("serial_read", {"timeout_seconds": 0.2}),
        ("driver_call", {"driver": "power", "method": "on"}),
    ]:
        assert "acquire" in client.tool(name, **arguments), name


def test_power_reaches_the_driver(provider: str):
    client = Client(provider)
    client.tool("acquire")
    assert "done" in client.tool("power", action="on")
    assert "done" in client.tool("power", action="cycle")
    # An action nobody exports is refused in the caller's own words, not a traceback.
    assert "not a power action" in client.tool("power", action="reboot")


def test_the_console_carries_bytes_both_ways(provider: str):
    client = Client(provider)
    client.tool("acquire")
    client.tool("serial_send", data="hello console\n")
    # `loop://` echoes, so what comes back is proof the whole path is live: MCP, the claim,
    # the SDK's stream, gRPC, the driver.
    assert "hello console" in client.tool("serial_read", timeout_seconds=2)


def test_a_read_does_not_say_again_what_it_already_said(provider: str):
    client = Client(provider)
    client.tool("acquire")
    client.tool("serial_send", data="said once\n")
    assert "said once" in client.tool("serial_read", timeout_seconds=2)
    # "Since you last read it" is the tool's promise, and a read that does not CONSUME what
    # it returns breaks it silently: every answer grows, and the caller cannot tell what is
    # new from what it has already been handed.
    assert "said once" not in client.tool("serial_read", timeout_seconds=2)


def test_expect_does_not_match_what_a_read_already_returned(provider: str):
    client = Client(provider)
    client.tool("acquire")
    client.tool("serial_send", data="login: root\n")
    assert "login: " in client.tool("serial_read", timeout_seconds=2)
    # The dangerous half of the same fault: output already handed over must not satisfy a
    # later wait. An agent that power-cycles and waits for a boot prompt would otherwise
    # match the one from before the reboot, instantly, and carry on as if the board were up.
    answer = client.tool("serial_expect", pattern="login: ", timeout_seconds=1)
    assert "did not appear" in answer


def test_expect_returns_as_soon_as_it_matches(provider: str):
    client = Client(provider)
    client.tool("acquire")
    client.tool("serial_send", data="login: ")
    assert "login: " in client.tool("serial_expect", pattern="login: ", timeout_seconds=10)


def test_a_timeout_still_says_what_was_seen(provider: str):
    client = Client(provider)
    client.tool("acquire")
    client.tool("serial_send", data="not the prompt you wanted")
    answer = client.tool("serial_expect", pattern="never-appears", timeout_seconds=1)
    assert "not the prompt you wanted" in answer
    assert "never-appears" in answer


def test_driver_call_is_the_general_case(provider: str):
    client = Client(provider)
    client.tool("acquire")
    # With no method, it says what the driver offers — and offers what the exporter
    # exports rather than what every driver client inherits from the SDK.
    offered = client.tool("driver_call", driver="power", method="")
    assert "on" in offered and "off" in offered
    assert "call_async" not in offered

    assert "power.on returned" in client.tool("driver_call", driver="power", method="on")
    # A driver that is not there is a fact about this exporter, said as one.
    assert "no driver" in client.tool("driver_call", driver="hdmi", method="capture")


def test_a_refusal_offers_what_the_listing_offers(provider: str):
    client = Client(provider)
    client.tool("acquire")
    listing = client.tool("driver_call", driver="power", method="")
    refusal = client.tool("driver_call", driver="power", method="definitely_not_a_method")
    # One answer to "what does this driver offer". Two of them is how a caller ends up
    # invoking the SDK's own plumbing: the refusal used to advertise every name on the
    # object, including the async ones the listing deliberately hides.
    for name in ("on", "off", "read"):
        assert name in refusal, name
    for plumbing in ("call_async", "stream", "wait_for_lease_ready"):
        assert plumbing not in refusal, plumbing
    assert listing.split(": ", 1)[-1].strip() in refusal


def test_a_property_is_refused_as_a_property(provider: str):
    client = Client(provider)
    client.tool("acquire")
    # `status` is a property on the SDK's client — asking for it as a method used to answer
    # "no method 'status'" and then list `status` among what the driver has.
    answer = client.tool("driver_call", driver="power", method="status")
    assert "property" in answer
    assert "no method 'status'" not in answer


def test_an_async_method_is_refused_rather_than_half_called(provider: str):
    client = Client(provider)
    client.tool("acquire")
    answer = client.tool("driver_call", driver="power", method="get_status_async")
    assert "asynchronous" in answer
    # Not a coroutine object handed back as text, which is what a caller used to receive.
    assert "coroutine" not in answer


def test_a_streaming_method_answers_with_what_it_streamed(provider: str):
    client = Client(provider)
    client.tool("acquire")
    # `read` is the only measurement jumpstarter's power interface offers, and it arrives as
    # a generator — which is what makes it the answer to "what is the board's power state".
    answer = client.tool("driver_call", driver="power", method="read")
    assert "PowerReading" in answer
    assert "voltage" in answer and "current" in answer
    assert "generator object" not in answer
    # And the readings are readings, not one quoted string: the exporter shapes the answer,
    # so repr-ing it a second time here wrapped the whole stream in quotes.
    assert "returned 'PowerReading" not in answer


def test_releasing_hands_the_console_back_clean(provider: str):
    first = Client(provider, name="first")
    first.tool("acquire")
    first.tool("serial_send", data="left over\n")
    first.tool("release")

    second = Client(provider, name="second")
    second.tool("acquire")
    # The console the next holder gets is a new one: whatever the last holder left in the
    # buffer is not their problem to read past.
    assert "left over" not in second.tool("serial_read", timeout_seconds=1)
