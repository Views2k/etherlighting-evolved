"""SFP-only precision checks with wire fixtures and in-memory transports."""

import asyncio
import math

import pytest
from test_native_switch import Clock, MemoryTransport, frame, ready
from test_ubus import (
    HELLO,
    ID_ATTRIBUTE,
    LOOKUP_OK,
    OBJECT_ID,
    PATH_ATTRIBUTE,
    STATUS_OK,
    Connection,
    acknowledge,
    blob,
    named,
    response,
)

from etherlighting_evolved import timing
from etherlighting_evolved.color import Color
from etherlighting_evolved.hardware import PRO_XG8
from etherlighting_evolved.switch import PortOutput
from etherlighting_evolved.transport import SSHTransport, TransportError
from etherlighting_evolved.ubus import (
    EtherlightUbus,
    UbusError,
    _named_attributes,
    _sfp_precise_data,
    validate_precise_rgb,
    validate_sfp_precise_rgb,
)


async def sfp_client(*, omitted=None):
    fields = b"".join(named(5, name, (3).to_bytes(4, "big"))
                      for name in (b"port_rgb", b"port_pwm", b"mcu_mode", b"behavior")
                      if name != omitted)
    connection = Connection()
    reply = response(2, 1, 0, PATH_ATTRIBUTE + ID_ATTRIBUTE + blob(6, named(2, b"set", fields)))
    reader, writer = connection.channel(HELLO + reply + LOOKUP_OK)
    client = await EtherlightUbus.open(connection)
    client.bind_profile(PRO_XG8)
    return client, reader, writer


@pytest.fixture
def clock(monkeypatch):
    value = Clock()
    monkeypatch.setattr(timing, "monotonic", lambda: value.now)
    monkeypatch.setattr(timing, "wait", value.wait)
    return value


class SfpPrecisionTransport(MemoryTransport):
    async def write_sfp_precise(self, port, channels):
        validate_sfp_precise_rgb(port, channels)
        await self.emit(("sfp_pwm", port, channels))


@pytest.mark.parametrize("port", [9, 10])
def test_exact_five_field_sfp_request_preserves_zero_white_component(port):
    assert list(_named_attributes(_sfp_precise_data(port, (65280, 257, 1)))) == [
        (b"port_pwm", 3, f"{port} 65280 257 1 0\0".encode("ascii"))]
    with pytest.raises(ValueError, match="copper"):
        validate_precise_rgb(port, (1, 2, 3))


@pytest.mark.parametrize("port, channels", [
    (0, (0, 0, 0)), (1, (1, 2, 3)), (8, (1, 2, 3)), (11, (1, 2, 3)), (64, (1, 2, 3)),
    (True, (1, 2, 3)), (9.0, (1, 2, 3)), ("9", (1, 2, 3)),
    (9, (-1, 0, 0)), (9, (65281, 0, 0)), (9, (65535, 0, 0)), (9, (True, 0, 0)),
    (9, (1.0, 0, 0)), (9, (math.nan, 0, 0)), (9, (math.inf, 0, 0)),
    (9, [1, 2, 3]), (9, (1, 2)), (9, (1, 2, 3, 4)), (9, ("1 0", 0, 0)), (9, None),
])
def test_sfp_validator_rejects_copper_broadcast_masks_and_malformed_components(port, channels):
    with pytest.raises(ValueError):
        _sfp_precise_data(port, channels)


@pytest.mark.anyio
async def test_sfp_client_uses_verified_object_and_acknowledged_single_request():
    client, reader, writer = await sfp_client()
    try:
        acknowledge(reader)
        await client.set_sfp_precise_port(10, (1, 257, 65280))
        data = named(3, b"port_pwm", b"10 1 257 65280 0\0")
        assert writer.writes[-1] == response(5, 2, OBJECT_ID, ID_ATTRIBUTE + blob(4, b"set\0") + blob(7, data))
        assert len(writer.writes) == 2
    finally:
        await client.close()


@pytest.mark.anyio
@pytest.mark.parametrize("omitted", [b"port_pwm", b"mcu_mode"])
async def test_unadvertised_sfp_capability_refuses_command_before_writing(omitted):
    client, _, writer = await sfp_client(omitted=omitted)
    try:
        with pytest.raises(UbusError, match="advertised"):
            await client.set_sfp_precise_port(9, (0, 0, 1))
        assert len(writer.writes) == 1
    finally:
        await client.close()


@pytest.mark.anyio
@pytest.mark.parametrize("fault", ["rejected", "wrong_object", "wrong_sequence", "duplicate_ack"])
async def test_ambiguous_sfp_ack_closes_client_without_retry(fault):
    client, reader, writer = await sfp_client()
    sequence = 3 if fault == "wrong_sequence" else 2
    object_attribute = blob(3, (OBJECT_ID + 1).to_bytes(4, "big")) if fault == "wrong_object" else ID_ATTRIBUTE
    value = b"error\0" if fault == "rejected" else b"ok\0"
    fields = named(3, b"set", value)
    if fault == "duplicate_ack":
        fields += named(3, b"set", b"ok\0")
    reader.feed_data(response(2, sequence, OBJECT_ID, object_attribute + blob(7, fields)))
    reader.feed_data(response(1, sequence, OBJECT_ID, STATUS_OK))
    with pytest.raises(UbusError):
        await client.set_sfp_precise_port(9, (0, 0, 1))
    assert writer.closed and not client.precise_rgb_supported and len(writer.writes) == 2


class PrecisionClient:
    def __init__(self, failure=None):
        self.writes = []
        self.closed = False
        self.failure = failure

    async def set_sfp_precise_port(self, port, channels):
        self.writes.append((port, channels))
        if self.failure:
            raise self.failure

    async def close(self):
        self.closed = True


@pytest.mark.anyio
@pytest.mark.parametrize("failure", [None, TransportError("fixture rejection"), asyncio.CancelledError()])
async def test_ssh_transport_uses_only_the_typed_sfp_operation_without_fallback(failure):
    transport = SSHTransport("fixture.invalid")
    client = PrecisionClient(failure)
    transport._rgb_client = client
    if failure:
        with pytest.raises(type(failure)):
            await transport.write_sfp_precise(9, (1, 2, 3))
        assert client.closed and transport._rgb_client is None
    else:
        await transport.write_sfp_precise(9, (1, 2, 3))
        assert not client.closed and transport._rgb_client is client
    assert client.writes == [(9, (1, 2, 3))]
    assert not transport._channels


@pytest.mark.anyio
async def test_sfp_precision_acquires_with_known_rgb_black_and_keeps_sub_byte_steps(clock):
    switch, transport = await ready(SfpPrecisionTransport())
    copper = frame(())
    await switch.write(copper)
    transport.events.clear()
    a = PortOutput(Color(0, 0, 0), 100, (1, 0, 0))
    b = PortOutput(Color(0, 0, 0), 100, (2, 0, 0))
    assert switch.sfp_precise
    assert await switch.write(copper, sfp={9: a}) == 1
    assert transport.events == [("rgb", 9, "000000", 100), ("mode", 9, True), ("sfp_pwm", 9, (1, 0, 0))]
    assert switch.sfp_acknowledged(9, a)
    transport.events.clear()
    assert await switch.write(copper, sfp={9: b}) == 1
    assert await switch.write(copper, sfp={9: b}) == 0
    assert transport.events == [("sfp_pwm", 9, (2, 0, 0))]
    transport.events.clear()
    await switch.release_sfp(9)
    assert transport.events == [("rgb", 9, "000000", 100), ("mode", 9, False)]


@pytest.mark.anyio
@pytest.mark.parametrize("fault", ["missing_pwm", "wrong_firmware", "missing_mode", "revoked"])
async def test_sfp_precision_gate_failure_prevents_every_frame_write(clock, fault):
    transport = SfpPrecisionTransport()
    if fault == "missing_pwm":
        transport.precise_rgb_supported = False
    elif fault == "wrong_firmware":
        transport.firmware = "unverified"
    elif fault == "missing_mode":
        transport.sfp_mode_supported = False
    switch, transport = await ready(transport)
    if fault == "revoked":
        switch.revoke_precision()
    assert not switch.sfp_precise
    with pytest.raises((ValueError, TransportError)):
        await switch.write(frame(()), sfp={9: PortOutput(Color(0, 0, 0), 100, (1, 0, 0))})
    assert not transport.events


@pytest.mark.anyio
@pytest.mark.parametrize("invalid", [
    PortOutput(Color(0, 0, 0), 99, (1, 0, 0)),
    PortOutput(Color(0, 0, 0), 100, (65281, 0, 0)),
    PortOutput(Color(0, 0, 0), 100, (1, 0, 0), "breath_1s"),
])
async def test_invalid_sfp_precision_does_not_partially_write_copper_frame(clock, invalid):
    switch, transport = await ready(SfpPrecisionTransport())
    with pytest.raises(ValueError):
        await switch.write(frame(()), sfp={9: invalid})
    assert not transport.events


@pytest.mark.anyio
async def test_sfp_precision_failure_is_not_replayed_as_rgb_or_acknowledged(clock):
    switch, transport = await ready(SfpPrecisionTransport())
    a = PortOutput(Color(0, 0, 0), 100, (1, 0, 0))
    transport.failure = ("sfp_pwm", 9, (1, 0, 0))
    with pytest.raises(TransportError):
        await switch.write(frame(()), sfp={9: a})
    sfp_events = [event for event in transport.events if event[1] == 9]
    assert sfp_events == [("rgb", 9, "000000", 100), ("mode", 9, True), ("sfp_pwm", 9, (1, 0, 0))]
    assert switch.sfp_status(9)["state"] == "uncertain" and not switch.sfp_acknowledged(9, a)


@pytest.mark.anyio
@pytest.mark.parametrize("change", ["revoke", "replacement", "reauthorize"])
async def test_sfp_precision_rechecks_generation_after_pacing(clock, change):
    switch, transport = await ready(SfpPrecisionTransport())
    await switch._send_port(9, Color(0, 0, 0), 100, precise_rgb=(1, 0, 0))
    transport.events.clear()
    switch._last_attempt_at = clock.now

    def replace():
        if change == "revoke":
            switch.revoke_precision()
        else:
            transport.lighting_generation = object()
            if change == "reauthorize":
                switch._precise_generation = transport.lighting_generation
                switch._sfp_generation = transport.lighting_generation
                switch._steady_generation = transport.lighting_generation

    clock.on_wait = replace
    assert not await switch._send_port(9, Color(0, 0, 0), 100, precise_rgb=(2, 0, 0))
    assert not transport.events


@pytest.mark.anyio
async def test_sfp_precision_waits_for_the_complete_copper_behavior_transaction(clock):
    switch, transport = await ready(SfpPrecisionTransport())
    selected, proceed = asyncio.Event(), asyncio.Event()

    async def hold(event):
        if event == ("behavior", "breath_1s"):
            selected.set()
            await proceed.wait()

    transport.hook = hold
    native = asyncio.create_task(switch._send_port(1, Color(0, 0, 64), 100,
                                                 precise_rgb=(0, 0, 16384), breath="breath_1s"))
    sfp = None
    try:
        await asyncio.wait_for(selected.wait(), 1)
        sfp = asyncio.create_task(switch._send_port(9, Color(0, 0, 0), 100, precise_rgb=(1, 2, 3)))
        await asyncio.sleep(0)
        assert not sfp.done() and transport.events == [("behavior", "breath_1s")]
        proceed.set()
        assert await native and await sfp
        assert transport.events == [
            ("behavior", "breath_1s"), ("pwm", 1, (0, 0, 16384)), ("behavior", "steady"),
            ("rgb", 9, "000000", 100), ("mode", 9, True), ("sfp_pwm", 9, (1, 2, 3))]
    finally:
        proceed.set()
        await asyncio.gather(native, *([sfp] if sfp is not None else []), return_exceptions=True)
