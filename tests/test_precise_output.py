import asyncio
import math

import pytest
from test_ubus import (
    HELLO,
    ID_ATTRIBUTE,
    LOOKUP_OK,
    OBJECT_ID,
    PATH_ATTRIBUTE,
    Connection,
    acknowledge,
    blob,
    named,
    response,
)

from etherlighting_evolved.color import Color
from etherlighting_evolved.engine import Engine, to_output
from etherlighting_evolved.hardware import PRO_XG8
from etherlighting_evolved.switch import PRECISE_DAEMON_HASH, RUNNING_DAEMON_HASH, EtherlightSwitch, PortOutput
from etherlighting_evolved.transport import SimulatedTransport, TransportError
from etherlighting_evolved.ubus import (
    EtherlightUbus,
    UbusError,
    _named_attributes,
    _precise_port_data,
    _supports_port_rgb,
)


def signature(pwm_kind=3):
    fields = named(5, b"port_rgb", (3).to_bytes(4, "big"))
    fields += named(5, b"port_pwm", pwm_kind.to_bytes(4, "big"))
    return named(2, b"set", fields)


def test_precise_fields_match_the_reviewed_five_field_native_parser():
    fields = list(_named_attributes(_precise_port_data(8, (65280, 257, 1))))
    assert fields == [(b"port_pwm", 3, b"8 65280 257 1 0\0")]
    assert _supports_port_rgb(signature(), b"port_pwm")
    assert not _supports_port_rgb(signature(5), b"port_pwm")


@pytest.mark.parametrize("port,channels", [
    (0, (1, 2, 3)), (9, (1, 2, 3)), (10, (1, 2, 3)), (True, (1, 2, 3)),
    (1.0, (1, 2, 3)), ("1", (1, 2, 3)), (1, (65281, 0, 0)), (1, (-1, 0, 0)),
    (1, (True, 0, 0)), (1, (1.0, 0, 0)), (1, (math.nan, 0, 0)), (1, (math.inf, 0, 0)),
    (1, [1, 2, 3]), (1, (1, 2)), (1, (1, 2, 3, 4)), (1, ("1 0", 0, 0)),
])
def test_precise_parser_never_receives_invalid_or_out_of_scope_input(port, channels):
    with pytest.raises(ValueError):
        _precise_port_data(port, channels)


async def precise_client():
    connection = Connection()
    reply = response(2, 1, 0, PATH_ATTRIBUTE + ID_ATTRIBUTE + blob(6, signature()))
    reader, writer = connection.channel(HELLO + reply + LOOKUP_OK)
    client = await EtherlightUbus.open(connection)
    client.bind_profile(PRO_XG8)
    return client, reader, writer


@pytest.mark.anyio
async def test_precise_write_uses_verified_object_and_strict_ack():
    client, reader, writer = await precise_client()
    acknowledge(reader)
    await client.set_precise_port(8, (1, 257, 65280))
    expected_data = named(3, b"port_pwm", b"8 1 257 65280 0\0")
    expected = response(5, 2, OBJECT_ID, ID_ATTRIBUTE + blob(4, b"set\0") + blob(7, expected_data))
    assert writer.writes[-1] == expected
    bad_reply = ID_ATTRIBUTE + blob(7, named(3, b"set", b"error\0"))
    reader.feed_data(response(2, 3, OBJECT_ID, bad_reply))
    reader.feed_data(response(1, 3, OBJECT_ID, bytes.fromhex("0100000800000000")))
    with pytest.raises(UbusError, match="rejected"):
        await client.set_precise_port(1, (0, 0, 0))
    assert writer.closed and not client.precise_rgb_supported


@pytest.mark.anyio
async def test_schema_without_precise_input_sends_no_precise_request():
    client, _, writer = await precise_client()
    client.precise_rgb_supported = False
    with pytest.raises(UbusError, match="advertised"):
        await client.set_precise_port(1, (0, 0, 0))
    assert len(writer.writes) == 1
    await client.close()


class PreciseDevice(SimulatedTransport):
    digest = PRECISE_DAEMON_HASH + "  /proc/123/exe"
    supported = True

    def __init__(self):
        super().__init__(latency=0)
        self.precise_writes = []
        self.rgb_writes = []
        self.fail_write = False
        self.generation = object()

    @property
    def lighting_generation(self):
        return self.generation

    @property
    def direct_rgb(self):
        return True

    @property
    def precise_rgb_supported(self):
        return self.supported

    async def run(self, command, **kwargs):
        if command == RUNNING_DAEMON_HASH:
            return self.digest
        return await super().run(command, **kwargs)

    async def write_precise_rgb(self, port, channels):
        self.precise_writes.append((port, channels))
        if self.fail_write:
            raise TransportError("fixture failure")

    async def write_port_rgb(self, port, rgb, level):
        self.rgb_writes.append((port, rgb, level))


@pytest.mark.anyio
@pytest.mark.parametrize("digest,supported,enabled", [
    (PRECISE_DAEMON_HASH + "  /proc/123/exe", True, True),
    (PRECISE_DAEMON_HASH + "  /usr/sbin/etherlightd", True, False),
    ("0" * 64 + "  /proc/123/exe", True, False),
    (PRECISE_DAEMON_HASH + "  /proc/123/exe\nextra", True, False),
    ("", True, False), (PRECISE_DAEMON_HASH + "  /proc/123/exe", False, False),
])
async def test_precision_requires_current_executable_and_registered_input(digest, supported, enabled):
    device = PreciseDevice()
    device.digest, device.supported = digest, supported
    switch = EtherlightSwitch(device)
    await switch.connect()
    await switch.probe()
    assert switch.precise_rgb is enabled
    device.digest = "changed"
    await switch.probe()
    assert not switch.precise_rgb
    assert device.precise_writes == [] and device.rgb_writes == []


@pytest.mark.anyio
async def test_full_frame_validation_blocks_uninitialized_sfp_and_failures_do_not_fallback():
    device = PreciseDevice()
    switch = EtherlightSwitch(device)
    await switch.connect()
    await switch.probe()
    output = PortOutput(Color(0, 0, 0), 100, (1, 0, 0))
    with pytest.raises(TransportError, match="initialized"):
        await switch.write([output] * 8, sfp={9: output})
    assert device.precise_writes == [] and device.rgb_writes == []
    device.fail_write = True
    with pytest.raises(TransportError):
        await switch.write([output] * 8)
    assert device.precise_writes == [(1, (1, 0, 0))]
    assert device.rgb_writes == [] and switch._last == {}


@pytest.mark.anyio
async def test_precise_changes_below_one_rgb_byte_are_not_deduplicated():
    device = PreciseDevice()
    switch = EtherlightSwitch(device, keepalive=60)
    await switch.connect()
    await switch.probe()
    a = PortOutput(Color(0, 0, 0), 100, (1, 0, 0))
    b = PortOutput(Color(0, 0, 0), 100, (2, 0, 0))
    assert await switch.write([a] * 8, max_commands=200) == 8
    assert await switch.write([b] + [a] * 7, max_commands=200) == 1
    assert await switch.write([b] + [a] * 7, max_commands=200) == 0
    stop = asyncio.Event()
    stop.set()
    assert await switch.write([a] * 8, stop=stop) == 0
    assert device.precise_writes[-1] == (1, (2, 0, 0))


def test_precise_encoding_preserves_fades_below_the_legacy_black_floor():
    blue = Color(0, 0, 255)
    legacy, fine = set(), set()
    previous = 0
    for step in range(1001):
        color = blue.scaled(step / 1000)
        legacy.add(to_output(color, 20).color.b)
        output = to_output(color, 20, precise=True)
        fine.add(output.precise_rgb[2])
        assert output.precise_rgb[:2] == (0, 0)
        assert previous <= output.precise_rgb[2] <= 65280
        previous = output.precise_rgb[2]
    assert len(legacy) == 8
    assert len(fine) > 700
    assert to_output(blue.scaled(0.04), 20).color.is_off
    assert to_output(blue.scaled(0.04), 20, precise=True).precise_rgb[2] > 0
    assert to_output(blue, 0, precise=True).precise_rgb == (0, 0, 0)
    assert to_output(blue, 100, precise=True).precise_rgb == (0, 0, 65280)


@pytest.mark.anyio
async def test_effect_brightness_and_sfp_use_separate_encodings():
    device = PreciseDevice()
    switch = EtherlightSwitch(device)
    await switch.connect()
    await switch.probe()
    engine = Engine(switch, brightness=20, effect="breathing", transition=0)
    engine.set_sfp(9, {"mode": "color", "color": "blue", "brightness": 100})
    _, frame = engine.compose(t=0.75)
    assert all(o.precise_rgb is not None for o in frame)
    assert len({o.precise_rgb for o in frame}) == 1
    assert engine.sfp_outputs()[9].precise_rgb is None


@pytest.mark.anyio
@pytest.mark.parametrize("change", ["revoke", "replacement", "new_authorization"])
async def test_waiting_frame_cannot_use_a_replaced_precision_authorization(monkeypatch, change):
    from etherlighting_evolved import switch as switch_module

    device = PreciseDevice()
    switch = EtherlightSwitch(device)
    await switch.connect()
    await switch.probe()
    clock = [100.0]
    monkeypatch.setattr(switch_module.timing, "monotonic", lambda: clock[0])
    switch._last_attempt_at = clock[0]

    async def paced_wait(delay, stop=None):
        clock[0] += delay
        if change == "revoke":
            switch.revoke_precision()
        else:
            device.generation = object()
            if change == "new_authorization":
                await switch.probe()

    monkeypatch.setattr(switch_module.timing, "wait", paced_wait)
    assert not await switch._send_port(1, Color(0, 0, 0), 100, precise_rgb=(1, 0, 0))
    assert device.precise_writes == [] and switch.stats.attempted_commands == 0


@pytest.mark.anyio
async def test_lost_session_halts_its_captured_frame_and_revokes_precision():
    device = PreciseDevice()
    switch = EtherlightSwitch(device)
    await switch.connect()
    await switch.probe()
    engine = Engine(switch)
    engine.connected = True
    captured_halt = engine._output_halt
    engine._lost(TransportError("fixture disconnect"))
    assert captured_halt.is_set() and not switch.precise_rgb
    assert engine.connected is False


@pytest.mark.anyio
async def test_precision_is_revoked_before_probe_or_connect_can_await():
    device = PreciseDevice()
    switch = EtherlightSwitch(device)
    await switch.connect()
    await switch.probe()
    observed = []
    original_run = device.run

    async def observing_run(command, **kwargs):
        observed.append(switch.precise_rgb)
        return await original_run(command, **kwargs)

    device.run = observing_run
    await switch.probe()
    assert observed and not any(observed)
    await switch.connect()
    assert not switch.precise_rgb
