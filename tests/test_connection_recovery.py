import asyncio

import pytest
from test_native_switch import Clock, frame, ready
from test_sfp_precision_transport import SfpPrecisionTransport

from etherlighting_evolved import timing
from etherlighting_evolved.color import Color
from etherlighting_evolved.engine import Engine
from etherlighting_evolved.ownership import OutputJournal, OutputOwnershipError
from etherlighting_evolved.switch import PortOutput
from etherlighting_evolved.transport import NativeOutputUncertain, TransportError

PEER = "SHA256:connection-recovery-fixture"


@pytest.fixture
def clock(monkeypatch):
    clock = Clock()
    monkeypatch.setattr(timing, "monotonic", lambda: clock.now)
    monkeypatch.setattr(timing, "wait", clock.wait)


async def interrupted(tmp_path):
    transport = SfpPrecisionTransport()
    transport.host_key_fingerprint = PEER
    switch, _ = await ready(transport)
    switch.output_lease = OutputJournal(tmp_path).claim("fixture.invalid", 22, PEER)
    await switch.write(frame((1, 8)), sfp={9: PortOutput(Color(0, 0, 255), 100)})
    switch.native_uncertain = True
    switch._sfp_uncertain.add(9)
    switch.output_lease.uncertain = True
    switch.revoke_precision()
    transport.events.clear()
    return switch, transport


@pytest.mark.anyio
async def test_recovery_confirms_old_close_then_same_device_before_scoped_cleanup(tmp_path, clock):
    switch, transport = await interrupted(tmp_path)
    old = transport.lighting_generation
    calls = []

    async def close():
        calls.append("closed")
        transport.connected = False

    async def connect():
        assert calls == ["closed"]
        calls.append("connected")
        transport.connected = True
        transport.lighting_generation = object()

    transport.close, transport.connect = close, connect
    await switch.recover_output(frame(()))
    assert transport.lighting_generation is not old
    assert transport.events == [("behavior", "steady"), ("rgb", 1, "000000", 100),
                                ("rgb", 8, "000000", 100), ("rgb", 9, "000000", 100), ("mode", 9, False)]
    assert not switch.output_lease.uncertain
    assert all(switch.output_lease.owns(path) for path in switch.output_lease.paths)


@pytest.mark.anyio
@pytest.mark.parametrize("failure", ["old_close", "identity", "capability", "journal", "baseline", "sfp_release"])
async def test_failed_recovery_keeps_owner_and_output_blocked(tmp_path, clock, failure):
    switch, transport = await interrupted(tmp_path)
    if failure == "old_close":
        async def close():
            raise TransportError("unconfirmed close")
        transport.close = close
    if failure == "identity":
        async def connect():
            transport.connected = True
            transport.host_key_fingerprint = PEER + "changed"
        transport.connect = connect
    if failure == "capability":
        transport.daemon = "unverified"
    if failure == "journal":
        switch.output_lease.paths[0].write_text("{}")
    if failure == "baseline":
        transport.failure = ("behavior", "steady")
    if failure == "sfp_release":
        transport.failure = ("mode", 9, False)
    with pytest.raises((TransportError, ValueError)):
        await switch.recover_output(frame(()))
    assert switch.output_lease.uncertain
    assert all(path.exists() for path in switch.output_lease.paths)
    if failure in {"old_close", "identity", "capability", "journal"}:
        assert not transport.events


@pytest.mark.anyio
async def test_recovery_does_not_adopt_an_unknown_previous_owner(tmp_path, clock):
    switch, transport = await interrupted(tmp_path)
    switch.output_lease = None
    with pytest.raises(OutputOwnershipError):
        await switch.recover_output(frame(()))
    assert transport.events == []


@pytest.mark.anyio
async def test_engine_recovery_preserves_scene_and_resumes_only_after_ack(tmp_path, clock):
    switch, transport = await interrupted(tmp_path)
    engine = Engine(switch, rules=[], transition=0)
    engine.set_effect("comet")
    engine.connected = True
    before = engine.snapshot()
    engine._lost(TransportError("cable disconnected"))
    assert engine.state()["reconnect_required"]
    await engine.recover_connection()
    assert engine.connected and engine.mode == "running"
    assert engine.snapshot() == before
    assert not engine.state()["reconnect_required"]


@pytest.mark.anyio
async def test_canceled_recovery_preserves_uncertainty(tmp_path, clock):
    switch, transport = await interrupted(tmp_path)

    async def canceled(event):
        raise asyncio.CancelledError()

    transport.hook = canceled
    with pytest.raises(NativeOutputUncertain):
        await switch.recover_output(frame(()))
    assert switch.output_lease.uncertain and switch.native_uncertain


@pytest.mark.anyio
async def test_handoff_recovers_only_owned_output_and_closes_without_resuming(tmp_path, clock):
    switch, transport = await interrupted(tmp_path)
    engine = Engine(switch, rules=[], transition=0)
    engine._native_release_pending = engine._native_blocked = True
    engine._sfp_release_pending = True
    await engine.stop()
    assert engine.mode == 'released' and not engine.connected
    assert switch.output_lease.closed
    assert not transport.connected
    assert not any(path.exists() for path in switch.output_lease.paths)


@pytest.mark.anyio
async def test_handoff_failed_recovery_retains_ownership(tmp_path, clock):
    switch, transport = await interrupted(tmp_path)
    transport.daemon = 'unverified'
    engine = Engine(switch, rules=[], transition=0)
    engine._native_release_pending = engine._native_blocked = True
    with pytest.raises(TransportError):
        await engine.stop()
    assert engine.mode == 'released' and not engine.connected
    assert not switch.output_lease.closed
    assert all(path.exists() for path in switch.output_lease.paths)
