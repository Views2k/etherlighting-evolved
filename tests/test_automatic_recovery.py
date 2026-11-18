"""Automatic recovery after a lost connection leaves lighting output unconfirmed, for example when the switch restarts
while EE writes an animation frame. These tests use the real switch recovery and output ownership records."""

import asyncio

import pytest
from test_connection_recovery import PEER, interrupted
from test_native_switch import Clock, ready
from test_sfp_precision_transport import SfpPrecisionTransport

from etherlighting_evolved import engine as engine_module
from etherlighting_evolved import timing
from etherlighting_evolved.engine import HOST_KEY_CHANGED_ERROR, LOST_OUTPUT_ERROR, Engine
from etherlighting_evolved.ownership import OutputJournal
from etherlighting_evolved.transport import HostKeyMismatchError, TransportError, UnreachableError

RECOVERY_UNCONFIRMED = "Lighting recovery is not confirmed. Check the connection and try Reconnect switch again."


@pytest.fixture
def clock(monkeypatch):
    clock = Clock()
    monkeypatch.setattr(timing, "monotonic", lambda: clock.now)
    monkeypatch.setattr(timing, "wait", clock.wait)


def loop_sleeps(monkeypatch, *, until, on_wait=None):
    """Replace asyncio.sleep so the connection loop's waits are recorded instead of taken.

    Zero-second sleeps (command pacing) pass through. Once `until()` is true the loop ends with CancelledError;
    otherwise `on_wait(count)` runs during the wait.
    """
    waits, sleep = [], asyncio.sleep

    async def recorded(seconds):
        if seconds == 0:
            return await sleep(0)
        waits.append(seconds)
        if until():
            raise asyncio.CancelledError
        if on_wait is not None:
            on_wait(len(waits))
        await sleep(0)

    monkeypatch.setattr(engine_module.asyncio, "sleep", recorded)
    monkeypatch.setattr(engine_module.random, "uniform", lambda low, high: 1.0)
    return waits


def restarting(transport, failure):
    """Make the switch fail each attempt until the returned function brings it back."""
    connect, attempts = transport.connect, []

    async def reconnect():
        attempts.append(failure)
        if failure == "unreachable" and not online:
            raise UnreachableError("Can't reach fixture.invalid:22 over SSH (timed out)")
        if failure == "network" and not online:
            raise OSError("fixture network is unreachable")
        await connect()
        transport.lighting_generation = object()

    def answer():
        nonlocal online
        online = True
        transport.failure = None

    online = False
    transport.connect = reconnect
    if failure == "baseline":
        transport.failure = ("behavior", "steady")
    return attempts, answer


@pytest.mark.anyio
@pytest.mark.parametrize("failure", ["unreachable", "network", "baseline"])
async def test_switch_restart_during_a_frame_is_recovered_and_lighting_resumes(tmp_path, clock, monkeypatch,
                                                                               failure):
    switch, transport = await interrupted(tmp_path)
    engine = Engine(switch, rules=[], transition=0)
    engine.set_effect("comet")
    engine.connected = True
    scene = engine.snapshot()
    attempts, answer = restarting(transport, failure)
    engine._lost(TransportError("the lighting socket stopped answering"))
    assert engine.mode == "released" and not engine.connected
    assert not engine.state()["reconnect_required"] and engine.error == LOST_OUTPUT_ERROR
    revision, seen = engine.revision, []

    def on_wait(count):
        seen.append((engine.state()["reconnect_required"], engine.error, engine.mode, engine.revision))
        if count == 7:
            answer()

    waits = loop_sleeps(monkeypatch, until=lambda: engine.connected, on_wait=on_wait)
    with pytest.raises(asyncio.CancelledError):
        await engine._keep_connected()
    # Seven failed attempts, more than the five that used to end automatic reconnect, with the delay capped at a
    # minute. Failed attempts change no scene revision.
    assert waits == [3, 6, 12, 24, 48, 60, 60, 1]
    assert seen == [(False, LOST_OUTPUT_ERROR, "released", revision)] * 7
    assert len(attempts) == 8
    assert engine.connected and engine.mode == "running" and engine.error is None
    assert not engine.state()["reconnect_required"] and engine.snapshot() == scene
    assert not switch.output_lease.uncertain and not switch.native_uncertain


@pytest.mark.anyio
@pytest.mark.parametrize("released", ["before_the_loss", "during_the_outage"])
async def test_recovery_leaves_released_lighting_released(tmp_path, clock, monkeypatch, released):
    switch, transport = await interrupted(tmp_path)
    engine = Engine(switch, rules=[], transition=0)
    engine.connected = True
    _, answer = restarting(transport, "unreachable")
    if released == "before_the_loss":
        engine.release()
    engine._lost(TransportError("the lighting socket stopped answering"))

    def on_wait(count):
        if released == "during_the_outage":
            engine.release()
        answer()

    waits = loop_sleeps(monkeypatch, until=lambda: engine.connected, on_wait=on_wait)
    with pytest.raises(asyncio.CancelledError):
        await engine._keep_connected()
    assert waits == [3, 1]
    assert engine.connected and engine.mode == "released" and engine.error is None
    assert not engine.state()["reconnect_required"] and not switch.output_lease.uncertain
    engine.resume()
    assert engine.mode == "running"


@pytest.mark.anyio
@pytest.mark.parametrize("failure,message", [("host_key", HOST_KEY_CHANGED_ERROR),
                                             ("ownership", RECOVERY_UNCONFIRMED)])
async def test_failure_that_needs_the_user_stops_automatic_recovery(tmp_path, clock, monkeypatch, failure, message):
    switch, transport = await interrupted(tmp_path)
    engine = Engine(switch, rules=[], transition=0)
    engine.connected = True
    restarting(transport, "unreachable")

    async def changed_key():
        raise HostKeyMismatchError("fixture changed key")

    def on_wait(count):
        # The switch answers with another SSH key, or the lighting ownership record no longer names this session.
        if failure == "host_key":
            transport.connect = changed_key
        else:
            switch.output_lease.paths[0].write_text("{}")

    engine._lost(TransportError("the lighting socket stopped answering"))
    waits = loop_sleeps(monkeypatch, until=lambda: False, on_wait=on_wait)
    await engine._keep_connected()
    assert waits == [3]
    assert engine.error == message and engine.state()["reconnect_required"]
    assert not engine.connected and engine.mode == "released"
    assert switch.output_lease.uncertain and all(path.exists() for path in switch.output_lease.paths)
    with pytest.raises(TransportError, match="Reconnect before resuming"):
        engine.resume()
    await engine._keep_connected()
    assert waits == [3]


@pytest.mark.anyio
async def test_interrupted_initialization_on_reconnect_continues_with_recovery(tmp_path, clock, monkeypatch):
    transport = SfpPrecisionTransport()
    transport.host_key_fingerprint = PEER
    switch, _ = await ready(transport)
    switch.output_lease = OutputJournal(tmp_path).claim("fixture.invalid", 22, PEER)
    engine = Engine(switch, rules=[], transition=0)
    engine.connected = True
    engine._lost(TransportError("fixture connection reset"))
    # Nothing was in flight, so this is a plain reconnect.
    assert engine.mode == "running" and not engine.state()["reconnect_required"]
    _, answer = restarting(transport, "baseline")
    seen = []

    def on_wait(count):
        seen.append((engine.state()["reconnect_required"], engine.error, engine.mode))
        answer()

    waits = loop_sleeps(monkeypatch, until=lambda: engine.connected, on_wait=on_wait)
    with pytest.raises(asyncio.CancelledError):
        await engine._keep_connected()
    assert waits == [3, 1]
    assert seen == [(False, LOST_OUTPUT_ERROR, "released")]
    assert engine.connected and engine.mode == "running" and not switch.output_lease.uncertain
