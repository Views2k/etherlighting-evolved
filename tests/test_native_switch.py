"""Native/SFP ownership tests with an in-memory transport and controlled pacing."""

import asyncio

import pytest

from etherlighting_evolved import timing
from etherlighting_evolved.color import Color
from etherlighting_evolved.switch import (
    BOARD_LIGHTING_INFO,
    HOSTNAME_COMMAND,
    MCU_SCHEMA_COMMAND,
    MCU_SCHEMA_EXIT,
    PORT_FIRMWARE_HASH,
    PORT_FIRMWARE_HASH_COMMAND,
    PRECISE_DAEMON_HASH,
    RUNNING_DAEMON_HASH,
    EtherlightSwitch,
    PortOutput,
)
from etherlighting_evolved.transport import NativeOutputUncertain, TransportError


class Clock:
    def __init__(self):
        self.now = 100.0
        self.on_wait = None

    async def wait(self, delay, stop=None):
        self.now += delay + 1e-9
        if self.on_wait:
            callback, self.on_wait = self.on_wait, None
            callback()
        await asyncio.sleep(0)


@pytest.fixture
def clock(monkeypatch):
    value = Clock()
    monkeypatch.setattr(timing, "monotonic", lambda: value.now)
    monkeypatch.setattr(timing, "wait", value.wait)
    return value


class MemoryTransport:
    def __init__(self):
        self.connected = False
        self.direct_rgb = True
        self.precise_rgb_supported = True
        self.behavior_supported = True
        self.sfp_mode_supported = True
        self.lighting_generation = object()
        self.board = "board.shortname=USPXG8P\nboard.name=USW-Pro-XG-8-PoE\nboard.sysid=0xed76"
        self.daemon = PRECISE_DAEMON_HASH + "  /proc/123/exe"
        self.firmware = PORT_FIRMWARE_HASH + "  /usr/share/firmware/port_led_fw.bin"
        self.events = []
        self.reads = []
        self.hook = self.read_hook = None
        self.failure = None

    async def connect(self):
        self.connected = True

    async def close(self):
        self.connected = False

    async def prepare_rgb(self):
        pass

    def configure_lighting_profile(self, profile):
        profile.require_output()
        self.profile = profile

    def revoke_lighting_profile(self):
        self.profile = None

    async def run(self, command, **kwargs):
        self.reads.append(command)
        if self.read_hook:
            await self.read_hook(command)
        if command == BOARD_LIGHTING_INFO:
            return self.board
        if command == RUNNING_DAEMON_HASH:
            return self.daemon
        if command == PORT_FIRMWARE_HASH_COMMAND:
            return self.firmware
        if command == HOSTNAME_COMMAND:
            return ""
        assert command == MCU_SCHEMA_COMMAND
        return "'etherlight.mcu' @deadbeef\n'set': {\"port_rgb\":\"String\"}" + f"\n{MCU_SCHEMA_EXIT}0\n"

    async def emit(self, event):
        self.events.append(event)
        if self.hook:
            await self.hook(event)
        if self.failure == event:
            raise TransportError("fixture acknowledgment failure")

    async def write_behavior(self, behavior):
        await self.emit(("behavior", behavior))

    async def write_precise_rgb(self, port, rgb):
        await self.emit(("pwm", port, rgb))

    async def write_sfp_precise(self, port, rgb):
        await self.emit(("sfp_pwm", port, rgb))

    async def write_port_rgb(self, port, color, level):
        await self.emit(("rgb", port, color, level))

    async def write_sfp_mode(self, port, direct):
        await self.emit(("mode", port, direct))


async def ready(transport=None):
    transport = transport or MemoryTransport()
    switch = EtherlightSwitch(transport)
    await switch.connect()
    await switch.probe()
    await switch.initialize_output()
    transport.events.clear()
    return switch, transport


def frame(native_ports=(1,), behavior="breath_1s"):
    outputs = [PortOutput.off() for _ in range(8)]
    for port in native_ports:
        outputs[port - 1] = PortOutput(Color(0, 0, 64), 100, (0, 0, 16384), behavior)
    return outputs


@pytest.mark.anyio
async def test_verified_native_and_sfp_gates_initialize_once(clock):
    transport = MemoryTransport()
    switch = EtherlightSwitch(transport)
    await switch.connect()
    info = await switch.probe()
    assert info["native_breathing"] and info["sfp_direct"] and info["sfp_ports"] == [9, 10]
    assert transport.events == []
    await switch.initialize_output()
    await switch.initialize_output()
    assert transport.events == [("behavior", "steady")]


@pytest.mark.anyio
@pytest.mark.parametrize("fault, native, sfp", [
    ("wrong_model", False, False), ("wrong_daemon", False, False),
    ("wrong_firmware", False, False), ("missing_firmware", False, False), ("missing_sysid", False, False),
    ("missing_behavior", False, False), ("missing_mode", True, False),
    ("missing_pwm", False, True), ("daemon_generation", False, False),
    ("firmware_generation", False, False),
])
async def test_capability_failures_preserve_copper_and_limit_native_scope(clock, fault, native, sfp):
    transport = MemoryTransport()
    if fault == "wrong_model":
        transport.board = "board.shortname=Other"
    elif fault == "wrong_daemon":
        transport.daemon = "0" * 64 + "  /proc/123/exe"
    elif fault == "wrong_firmware":
        transport.firmware = "0" * 64 + "  /usr/share/firmware/port_led_fw.bin"
    elif fault == "missing_firmware":
        transport.firmware = ""
    elif fault == "missing_sysid":
        transport.board = "board.shortname=USPXG8P"
    elif fault == "missing_behavior":
        transport.behavior_supported = False
    elif fault == "missing_mode":
        transport.sfp_mode_supported = False
    elif fault == "missing_pwm":
        transport.precise_rgb_supported = False
    else:
        async def replace(command):
            expected = RUNNING_DAEMON_HASH if fault == "daemon_generation" else PORT_FIRMWARE_HASH_COMMAND
            if command == expected:
                transport.lighting_generation = object()
        transport.read_hook = replace
    switch = EtherlightSwitch(transport)
    await switch.connect()
    info = await switch.probe()
    assert info["port_rgb"] and switch.native_breathing is native and switch.scoped_sfp is sfp
    assert bool(switch.sfp_ports) is sfp
    assert not transport.events
    if fault in {"wrong_firmware", "missing_firmware", "missing_mode"}:
        assert switch.precise_rgb and info["output_precision"] == "rgb16"
    if fault == "missing_sysid":
        assert not switch.precise_rgb and "output_precision" not in info


@pytest.mark.anyio
async def test_initialization_failure_blocks_output_without_replay(clock):
    transport = MemoryTransport()
    transport.failure = ("behavior", "steady")
    switch = EtherlightSwitch(transport)
    await switch.connect()
    await switch.probe()
    with pytest.raises(NativeOutputUncertain):
        await switch.initialize_output()
    assert switch.native_uncertain and transport.events == [("behavior", "steady")]
    with pytest.raises(NativeOutputUncertain):
        await switch.write(frame(()))
    assert transport.events == [("behavior", "steady")]


@pytest.mark.anyio
async def test_native_frame_enrollment_is_not_repeated_by_force_keepalive_or_forget(clock):
    switch, transport = await ready()
    outputs = frame((1, 8))
    assert await switch.write(outputs) == 8
    assert [e for e in transport.events if e[0] == "behavior"] == [
        ("behavior", "breath_1s"), ("behavior", "steady"),
        ("behavior", "breath_1s"), ("behavior", "steady")]
    transport.events.clear()
    clock.now += 10
    switch.forget()
    await switch.write(outputs, force=True)
    assert all(e[0] == "rgb" and e[1] not in (1, 8) for e in transport.events)


@pytest.mark.anyio
@pytest.mark.parametrize("invalid", [
    PortOutput(Color(0, 0, 64), 100, None, "breath_1s"),
    PortOutput(Color(0, 0, 64), 100, (0, 0, 16384), "unrecognized"),
    PortOutput(Color(0, 0, 64), 99, (0, 0, 16384), "breath_1s"),
])
async def test_whole_frame_validation_precedes_first_color_write(clock, invalid):
    switch, transport = await ready()
    outputs = frame(())
    outputs[7] = invalid
    with pytest.raises(ValueError):
        await switch.write(outputs)
    assert not transport.events


@pytest.mark.anyio
async def test_sfp_write_cannot_interleave_inside_breath_selector_transaction(clock):
    switch, transport = await ready()
    selected, proceed = asyncio.Event(), asyncio.Event()

    async def pause(event):
        if event == ("behavior", "breath_1s"):
            selected.set()
            await proceed.wait()

    transport.hook = pause
    native = asyncio.create_task(switch._send_port(1, Color(0, 0, 64), 100,
                                                 precise_rgb=(0, 0, 16384), breath="breath_1s"))
    await asyncio.wait_for(selected.wait(), 1)
    sfp = asyncio.create_task(switch._send_port(9, Color(255, 0, 0), 100))
    await asyncio.sleep(0)
    assert not sfp.done() and transport.events == [("behavior", "breath_1s")]
    proceed.set()
    assert await native and await sfp
    assert transport.events == [
        ("behavior", "breath_1s"), ("pwm", 1, (0, 0, 16384)), ("behavior", "steady"),
        ("rgb", 9, "000000", 100), ("mode", 9, True), ("rgb", 9, "ff0000", 100)]


@pytest.mark.anyio
@pytest.mark.parametrize("failure", [("behavior", "breath_1s"), ("pwm", 1, (0, 0, 16384))])
async def test_failed_native_setup_attempts_steady_once_and_does_not_replay_target(clock, failure):
    switch, transport = await ready()
    transport.failure = failure
    with pytest.raises(TransportError):
        await switch.write(frame())
    assert transport.events[-1] == ("behavior", "steady")
    assert transport.events.count(failure) == 1 and not switch.native_uncertain
    assert 1 not in switch._last


@pytest.mark.anyio
async def test_stop_after_selector_restores_steady_without_target(clock):
    switch, transport = await ready()
    stop = asyncio.Event()

    async def cancel(event):
        if event == ("behavior", "breath_1s"):
            stop.set()

    transport.hook = cancel
    assert await switch.write(frame(), stop=stop) == 0
    assert transport.events == [("behavior", "breath_1s"), ("behavior", "steady")]
    assert not switch._native_active and not switch.native_uncertain


@pytest.mark.anyio
async def test_native_cleanup_failure_latches_output_uncertainty(clock):
    switch, transport = await ready()
    transport.failure = ("behavior", "steady")
    with pytest.raises(NativeOutputUncertain):
        await switch.write(frame())
    attempts = list(transport.events)
    assert switch.native_uncertain
    with pytest.raises(NativeOutputUncertain):
        await switch.write(frame(()))
    assert transport.events == attempts


@pytest.mark.anyio
async def test_quiesce_stops_only_native_ports_and_preserves_requested_output(clock):
    switch, transport = await ready()
    await switch.write(frame((1, 8)))
    transport.events.clear()
    captured = [PortOutput(Color(12, 34, 56), 100, (3072, 8704, 14336))] * 8
    await switch.quiesce_native(captured)
    assert transport.events == [("pwm", 1, (3072, 8704, 14336)), ("pwm", 8, (3072, 8704, 14336))]
    assert not switch._native_active


@pytest.mark.anyio
async def test_stale_native_cleanup_cannot_write_to_reverified_connection(clock):
    switch, transport = await ready()
    await switch.write(frame((1, 8)))
    transport.lighting_generation = object()
    await switch.probe()
    await switch.initialize_output()
    transport.events.clear()
    with pytest.raises(NativeOutputUncertain):
        await switch.quiesce_native(frame(()))
    assert not transport.events


@pytest.mark.anyio
async def test_sfp_acquire_hold_off_and_release_keep_one_record_scope(clock):
    switch, transport = await ready()
    copper = frame(())
    await switch.write(copper)
    transport.events.clear()
    color = PortOutput(Color(255, 255, 255), 25)
    assert await switch.write(copper, sfp={9: color}) == 1
    assert transport.events == [("rgb", 9, "000000", 100), ("mode", 9, True), ("rgb", 9, "ffffff", 25)]
    assert switch.sfp_acknowledged(9, color) and switch.sfp_status(9)["state"] == "active"
    transport.events.clear()
    assert await switch.write(copper, sfp={9: color}) == 0
    assert not transport.events
    await switch.write(copper, sfp={9: PortOutput.off()})
    assert transport.events == [("rgb", 9, "000000", 100)]
    transport.events.clear()
    await switch.release_sfp(9)
    assert transport.events == [("rgb", 9, "000000", 100), ("mode", 9, False)]
    assert switch.sfp_status(9)["state"] == "idle" and not switch.sfp_acknowledged(9, color)
    transport.events.clear()
    await switch.quiesce_sfp()
    assert not transport.events


@pytest.mark.anyio
@pytest.mark.parametrize("fail_mode_ack", [False, True])
async def test_inflight_sfp_acquisition_is_pending_until_native_ack(clock, fail_mode_ack):
    switch, transport = await ready()
    mode_started, allow_ack = asyncio.Event(), asyncio.Event()

    async def hold_mode_ack(event):
        if event == ("mode", 9, True):
            mode_started.set()
            await allow_ack.wait()

    transport.hook = hold_mode_ack
    if fail_mode_ack:
        transport.failure = ("mode", 9, True)
    acquiring = asyncio.create_task(switch._send_port(9, Color(255, 0, 0), 100))
    try:
        await asyncio.wait_for(mode_started.wait(), 1)
        status = switch.sfp_status(9)
        assert status["state"] == "pending" and status["owned"] is False
        assert status["error"] is None and not acquiring.done()
        allow_ack.set()
        if fail_mode_ack:
            with pytest.raises(TransportError):
                await asyncio.wait_for(acquiring, 1)
            status = switch.sfp_status(9)
            assert status["state"] == "uncertain" and status["owned"] is False
            assert status["error"]
        else:
            assert await asyncio.wait_for(acquiring, 1)
            status = switch.sfp_status(9)
            assert status["state"] == "active" and status["owned"] is True
            assert status["error"] is None
    finally:
        allow_ack.set()
        await asyncio.gather(acquiring, return_exceptions=True)


@pytest.mark.anyio
@pytest.mark.parametrize("failure", [("mode", 9, True), ("rgb", 9, "ff0000", 100)])
async def test_failed_sfp_acquisition_is_recoverable_only_on_original_connection(clock, failure):
    switch, transport = await ready()
    transport.failure = failure
    with pytest.raises(TransportError):
        await switch._send_port(9, Color(255, 0, 0), 100)
    assert switch.sfp_status(9)["state"] == "uncertain"
    transport.failure = None
    transport.events.clear()
    await switch.release_sfp(9)
    assert transport.events == [("rgb", 9, "000000", 100), ("mode", 9, False)]
    assert switch.sfp_status(9)["state"] == "idle"


@pytest.mark.anyio
async def test_a_color_ack_cannot_clear_unconfirmed_sfp_mode_acquisition(clock):
    switch, transport = await ready()
    transport.failure = ("mode", 9, True)
    with pytest.raises(TransportError):
        await switch._send_port(9, Color(255, 0, 0), 100)
    transport.failure = None
    transport.events.clear()
    try:
        await switch._send_port(9, Color(255, 0, 0), 100)
    except TransportError:
        assert switch.sfp_status(9)["state"] == "uncertain"
    else:
        assert ("mode", 9, True) in transport.events
        assert switch.sfp_status(9)["state"] == "active"


@pytest.mark.anyio
@pytest.mark.parametrize("ambiguous", [False, True])
async def test_stale_sfp_release_rejects_even_if_prior_mode_ack_was_lost(clock, ambiguous):
    switch, transport = await ready()
    if ambiguous:
        transport.failure = ("mode", 9, True)
        with pytest.raises(TransportError):
            await switch._send_port(9, Color(255, 0, 0), 100)
        transport.failure = None
    else:
        await switch._send_port(9, Color(255, 0, 0), 100)
    transport.lighting_generation = object()
    await switch.probe()
    await switch.initialize_output()
    transport.events.clear()
    with pytest.raises(TransportError):
        await switch.release_sfp(9)
    assert not transport.events and switch.sfp_status(9)["state"] == "uncertain"


@pytest.mark.anyio
async def test_generation_change_during_sfp_release_prevents_mode_write(clock):
    switch, transport = await ready()
    await switch._send_port(9, Color(255, 0, 0), 100)
    transport.events.clear()

    async def replace(event):
        if event == ("rgb", 9, "000000", 100):
            transport.lighting_generation = object()

    transport.hook = replace
    with pytest.raises(TransportError):
        await switch.release_sfp(9)
    assert transport.events == [("rgb", 9, "000000", 100)]
    assert switch.sfp_status(9)["state"] == "uncertain"


@pytest.mark.anyio
async def test_generation_change_during_native_quiesce_does_not_clean_next_port(clock):
    switch, transport = await ready()
    await switch.write(frame((1, 8)))
    transport.events.clear()

    async def replace(event):
        if event == ("rgb", 1, "000000", 100):
            transport.lighting_generation = object()
            await switch.probe()

    transport.hook = replace
    with pytest.raises((TransportError, NativeOutputUncertain)):
        await switch.quiesce_native(frame(()))
    assert transport.events == [("rgb", 1, "000000", 100)]


@pytest.mark.anyio
async def test_revoked_capability_during_initialization_cannot_write(clock):
    transport = MemoryTransport()
    switch = EtherlightSwitch(transport)
    await switch.connect()
    await switch.probe()
    switch._last_attempt_at = clock.now
    clock.on_wait = switch.revoke_precision
    with pytest.raises(NativeOutputUncertain):
        await switch.initialize_output()
    assert not transport.events
