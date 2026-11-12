import asyncio

import pytest
from test_native_switch import Clock, frame, ready
from test_sfp_precision_transport import SfpPrecisionTransport

from etherlighting_evolved import device_playback, timing
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


@pytest.mark.anyio
@pytest.mark.parametrize("failure", [None, "offline", "identity", "grant"])
async def test_quit_with_resident_scene_and_lost_sfp_connection(tmp_path, clock, monkeypatch, failure):
    switch, transport = await ready(SfpPrecisionTransport())
    transport.host_key_fingerprint = PEER
    owner = "a" * 48
    lease = OutputJournal(tmp_path).player_lease("fixture.invalid", 22, PEER, owner)
    lease.keep_for_player = True
    switch.output_lease = lease
    await switch.write(frame(()), sfp={9: PortOutput(Color(0, 0, 255), 100)})
    assert switch.sfp_status(9)["owned"] and not switch.native_uncertain
    engine = Engine(switch, rules=[])
    engine.connected = True
    engine._lost(TransportError("connection lost"))
    assert switch.sfp_status(9)["state"] == "uncertain" and not switch.native_uncertain
    transport.events.clear()

    async def reconnect():
        if failure == "offline":
            raise TransportError("offline")
        transport.connected = True
        transport.lighting_generation = object()
        if failure == "identity":
            transport.host_key_fingerprint = PEER + "-different"

    async def player_command(_, command):
        if "mv -f desktop.next desktop.request" in command:
            transport.events.append("grant")
            return "" if failure == "grant" else "granted"
        assert "rm -f desktop.request" in command
        transport.events.append("release")
        return ""

    transport.connect = reconnect
    transport.desktop_output = device_playback.DesktopOutput(transport, owner)
    monkeypatch.setattr(device_playback, "_run", player_command)
    if failure:
        with pytest.raises(TransportError):
            await engine.stop()
        assert lease.uncertain and not lease.closed
        assert transport.events == (["grant"] if failure == "grant" else [])
    else:
        await engine.stop()
        assert lease.closed and not lease.uncertain
        assert transport.events == ["grant", ("behavior", "steady"), ("rgb", 9, "000000", 100),
                                    ("mode", 9, False), "release"]
    assert all(lease.owns(path) for path in lease.paths)
    assert not transport.connected
