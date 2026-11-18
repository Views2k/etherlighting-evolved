import asyncio
from types import SimpleNamespace

import pytest

from etherlighting_evolved.color import Color
from etherlighting_evolved.engine import Engine, to_output
from etherlighting_evolved.switch import EtherlightSwitch
from etherlighting_evolved.transport import SimulatedTransport, TransportError


class NativeSwitch(EtherlightSwitch):
    def __init__(self, *, supported=True):
        super().__init__(SimpleNamespace(direct_rgb=True, revoke_lighting_profile=lambda: None,
                                         host_key_fingerprint=None, desktop_output=None))
        self.supported = supported
        self.uncertain = False
        self.fail_quiesce = False
        self.calls = []
        self.initializing = asyncio.Event()
        self.initialization_gate = asyncio.Event()
        self.initialization_gate.set()
        self.quiesced = []
        self.write_started = asyncio.Event()
        self.write_gate = asyncio.Event()
        self.write_gate.set()
        self.sfp_ports = (9, 10)
        self.sfp_applied = {}
        self.sfp_uncertain = set()
        self.ack_sfp = True
        self.release_sfp_gate = asyncio.Event()
        self.release_sfp_gate.set()
        self.release_sfp_started = asyncio.Event()
        self.fail_sfp_release = False

    @property
    def native_breathing(self):
        return self.supported

    @property
    def native_uncertain(self):
        return self.uncertain

    @native_uncertain.setter
    def native_uncertain(self, value):
        self.uncertain = value

    @property
    def precise_rgb(self):
        return self.supported

    async def connect(self):
        self.calls.append("connect")

    async def probe(self):
        self.calls.append("probe")
        return {"port_rgb": True, "native_breathing": self.supported}

    async def initialize_output(self):
        self.calls.append("initialize-start")
        self.initializing.set()
        await self.initialization_gate.wait()
        self.calls.append("initialize-ack")

    async def quiesce_native(self, outputs):
        self.calls.append("quiesce")
        self.quiesced.append(outputs)
        if self.fail_quiesce:
            self.uncertain = True
            raise TransportError("native cleanup was not acknowledged")

    async def close(self):
        self.calls.append("close")

    async def write(self, outputs, **kwargs):
        self.calls.append("write-start")
        self.write_started.set()
        await self.write_gate.wait()
        self.calls.append("write-ack")
        self.stats.attempted_commands += len(outputs)
        if self.ack_sfp:
            self.sfp_applied.update(kwargs.get("sfp", {}))
        return len(outputs)

    def sfp_status(self, port):
        owned = port in self.sfp_applied
        return {"state": "uncertain" if port in self.sfp_uncertain else "active" if owned else "idle",
                "owned": owned}

    def sfp_acknowledged(self, port, output):
        return port not in self.sfp_uncertain and self.sfp_applied.get(port) == output

    async def release_sfp(self, port):
        if port not in self.sfp_applied:
            return
        self.calls.append(f"release-sfp-{port}")
        self.release_sfp_started.set()
        await self.release_sfp_gate.wait()
        if self.fail_sfp_release:
            self.sfp_uncertain.add(port)
            raise TransportError("SFP release was not acknowledged")
        self.sfp_applied.pop(port, None)

    async def quiesce_sfp(self):
        for port in list(self.sfp_applied):
            await self.release_sfp(port)


def native_engine(**kwargs):
    return Engine(NativeSwitch(), effect="breathing", effect_params={"playback": "synchronized"},
                  rules=[], **kwargs)


def group(name, ports, *, pace=2, synchronized=True):
    return {"id": name, "name": name, "ports": ports,
            "effect": {"id": "breathing", "params": {
                "playback": "synchronized" if synchronized else "custom", "pace": pace}}}


@pytest.mark.parametrize("pace,selector", [(1, "breath_3s"), (2, "breath_2s"), (3, "breath_1s")])
def test_native_output_is_a_constant_plan_with_a_separate_animated_preview(pace, selector):
    engine = native_engine(brightness=50, gamma=1, transition=3)
    engine.update_params({"pace": pace, "color": "#ff8040", "brightness_color": 50,
                          "random_color": True, "floor": 0.9, "ripple": True})
    frames = [engine.compose(t=at) for at in (0, 0.25, 0.65, 1.1)]
    assert len({tuple(outputs) for _, outputs in frames}) == 1
    assert len({tuple(colors) for colors, _ in frames}) > 1
    expected = to_output(Color.parse("#ff8040").scaled(0.5), 50, gamma=1, precise=True)
    for _, outputs in frames:
        assert all(output.breath == selector for output in outputs)
        assert all(output.precise_rgb == expected.precise_rgb for output in outputs)
    assert engine.zone().effect.p["random_color"] is True
    assert not engine._fades


def test_custom_breathing_keeps_software_frames_and_no_native_selector():
    engine = Engine(NativeSwitch(), effect="breathing", rules=[], transition=0)
    before = engine.compose(t=0)[1]
    after = engine.compose(t=1)[1]
    assert before != after
    assert all(output.breath is None for output in before + after)


def test_native_entry_and_parameter_edits_do_not_create_a_streamed_crossfade():
    engine = Engine(NativeSwitch(), rules=[], transition=3)
    engine.compose(t=0, clock=100)
    engine.set_effect("breathing", {"playback": "synchronized", "color": "blue"})
    outputs = engine.compose(t=0, clock=100)[1]
    assert not engine._fades
    assert all(output.breath == "breath_2s" for output in outputs)
    engine.update_params({"color": "green", "pace": 3})
    assert not engine._fades
    assert all(output.breath == "breath_1s" for output in engine.compose(t=0, clock=100)[1])


def test_rules_and_manual_colors_supersede_native_plans_per_port():
    engine = native_engine(transition=3)
    engine.set_rules([{"name": "Fault", "ports": [1], "look": {"color": "red"}}])
    engine.set_override(2, "green")
    outputs = engine.compose(t=0.3)[1]
    assert outputs[0].breath is None and outputs[0].color == Color(255, 0, 0)
    assert outputs[1].breath is None and outputs[1].color == Color(0, 255, 0)
    assert all(output.breath == "breath_2s" for output in outputs[2:])


def test_shared_pace_updates_every_synchronized_group_and_retains_effect_identity():
    engine = native_engine()
    engine.set_zones([group("left", [1, 2]), group("right", [3, 4]),
                      group("custom", [5, 6, 7, 8], pace=1, synchronized=False)])
    effects = [zone.effect for zone in engine.zones]
    engine.update_params({"pace": 3}, "left")
    assert [zone.effect.p["pace"] for zone in engine.zones] == [3, 3, 1]
    assert all(before is zone.effect for before, zone in zip(effects, engine.zones, strict=True))
    assert not engine._fades


def test_new_native_group_adopts_the_existing_shared_pace():
    engine = native_engine()
    engine.set_zones([group("left", [1, 2], pace=1), group("right", [3, 4], synchronized=False)])
    engine.set_effect("breathing", {"playback": "synchronized", "pace": 3}, "right")
    assert [zone.effect.p["pace"] for zone in engine.zones] == [1, 1]


def test_switching_playback_without_a_pace_adopts_existing_native_pace():
    engine = native_engine()
    engine.set_zones([group("left", [1, 2], pace=3), group("right", [3, 4], synchronized=False)])
    engine.update_params({"playback": "synchronized"}, "right")
    assert [zone.effect.p["pace"] for zone in engine.zones] == [3, 3]


@pytest.mark.parametrize("scene", [False, True])
def test_mixed_native_paces_are_rejected_without_changing_scene(scene):
    engine = native_engine()
    before = engine.snapshot()
    zones = [group("left", [1, 2], pace=1), group("right", [3, 4], pace=3)]
    with pytest.raises(ValueError, match="same pace"):
        if scene:
            engine.apply_snapshot({"zones": zones, "brightness": 10})
        else:
            engine.set_zones(zones, clear_overrides=True)
    assert engine.snapshot() == before


def test_invalid_parameter_edit_does_not_change_any_group_pace():
    engine = native_engine()
    engine.set_zones([group("left", [1, 2]), group("right", [3, 4])])
    before = engine.snapshot()
    with pytest.raises(ValueError):
        engine.update_params({"pace": 3, "color": "bad color"}, "left")
    assert engine.snapshot() == before


def test_real_unsupported_device_rejects_native_enabling_atomically():
    engine = Engine(NativeSwitch(supported=False), rules=[])
    before = engine.snapshot()
    with pytest.raises(ValueError, match="unavailable"):
        engine.set_effect("breathing", {"playback": "synchronized"})
    assert engine.snapshot() == before


def test_simulator_previews_synchronized_mode_without_claiming_native_output():
    engine = Engine(EtherlightSwitch(SimulatedTransport()), rules=[], transition=0)
    engine.set_effect("breathing", {"playback": "synchronized"})
    first = engine.compose(t=0)[1]
    later = engine.compose(t=0.5)[1]
    assert first != later
    assert all(output.breath is None for output in first + later)


@pytest.mark.anyio
async def test_release_waits_for_the_in_flight_frame_then_quiesces_once_and_keeps_connection():
    engine = native_engine()
    engine.connected = True
    engine._native_engaged = True
    engine.compose(t=0.3)
    engine._write_idle.clear()
    engine.release()
    first = asyncio.create_task(engine.wait_for_output_idle())
    second = asyncio.create_task(engine.wait_for_output_idle())
    await asyncio.sleep(0)
    assert not engine.switch.quiesced
    with pytest.raises(ValueError, match="finishing"):
        engine.resume()
    engine._write_idle.set()
    await asyncio.gather(first, second)
    assert len(engine.switch.quiesced) == 1
    assert all(output.breath is None for output in engine.switch.quiesced[0])
    assert engine.connected and "close" not in engine.switch.calls
    engine.resume()
    assert engine.mode == "running"


@pytest.mark.anyio
async def test_unacknowledged_native_cleanup_blocks_resume_and_records_error():
    engine = native_engine()
    engine.connected = True
    engine._native_engaged = True
    engine.switch.fail_quiesce = True
    engine.release()
    with pytest.raises(TransportError, match="not acknowledged"):
        await engine.wait_for_output_idle()
    assert engine.mode == "released" and engine.error == "native cleanup was not acknowledged"
    assert engine._output_halt.is_set()
    with pytest.raises(TransportError, match="Reconnect"):
        engine.resume()


@pytest.mark.anyio
async def test_stop_drains_and_quiesces_before_canceling_tasks_or_closing_ssh():
    engine = native_engine()
    engine._native_engaged = True
    engine._write_idle.clear()

    async def background():
        try:
            await asyncio.Future()
        finally:
            engine.switch.calls.append("task-canceled")

    task = asyncio.create_task(background())
    engine._tasks = [task]
    await asyncio.sleep(0)
    stopped = asyncio.create_task(engine.stop())
    await asyncio.sleep(0)
    assert not stopped.done() and not task.done()
    engine._write_idle.set()
    await stopped
    assert engine.switch.calls == ["quiesce", "task-canceled", "close"]


@pytest.mark.anyio
async def test_connection_does_not_become_ready_until_baseline_is_acknowledged():
    engine = native_engine()
    engine.switch.initialization_gate.clear()
    task = asyncio.create_task(engine._keep_connected())
    try:
        await asyncio.wait_for(engine.switch.initializing.wait(), 1)
        assert not engine.connected
        engine.switch.initialization_gate.set()
        for _ in range(10):
            await asyncio.sleep(0)
            if engine.connected:
                break
        assert engine.connected
        assert engine.switch.calls[:4] == ["connect", "probe", "initialize-start", "initialize-ack"]
    finally:
        task.cancel()
        with pytest.raises(asyncio.CancelledError):
            await task


@pytest.mark.anyio
async def test_native_uncertainty_runs_recovery_instead_of_a_plain_reconnect():
    engine = native_engine()
    engine.connected = True
    engine.switch.uncertain = True
    recovering, recoveries = asyncio.Event(), []

    async def recover_output(outputs):
        recoveries.append(outputs)
        recovering.set()
        await asyncio.Future()

    engine.switch.recover_output = recover_output
    engine._lost(TransportError("unconfirmed native state"))
    task = asyncio.create_task(engine._keep_connected())
    try:
        await asyncio.wait_for(recovering.wait(), 1)
        assert engine.mode == "released" and not engine.connected
        # A plain reconnect would initialize output over the unconfirmed native state.
        assert not engine.switch.calls and len(recoveries) == 1
        assert not engine.state()["reconnect_required"]
        with pytest.raises(TransportError, match="after EE reconnects"):
            engine.resume()
        with pytest.raises(TransportError, match="already in progress"):
            await engine.recover_connection()
    finally:
        task.cancel()
        with pytest.raises(asyncio.CancelledError):
            await task


def loop_sleeps(monkeypatch, until):
    """Replace asyncio.sleep with a recorder that ends the loop with CancelledError once `until()` is true."""
    waits, sleep = [], asyncio.sleep

    async def recorded(seconds):
        waits.append(seconds)
        if until():
            raise asyncio.CancelledError
        await sleep(0)

    monkeypatch.setattr(asyncio, "sleep", recorded)
    return waits


@pytest.mark.anyio
async def test_failed_release_while_connected_does_not_start_automatic_recovery(monkeypatch):
    engine = native_engine()
    engine.connected = True
    engine._native_engaged = True
    engine.switch.fail_quiesce = True
    recoveries = []

    async def recover_output(outputs):
        recoveries.append(outputs)
        return {}

    engine.switch.recover_output = recover_output
    engine.release()
    with pytest.raises(TransportError, match="not acknowledged"):
        await engine.wait_for_output_idle()
    waits = loop_sleeps(monkeypatch, lambda: len(waits) == 3)
    with pytest.raises(asyncio.CancelledError):
        await engine._keep_connected()
    assert waits == [1.0, 1.0, 1.0] and not recoveries and engine.switch.calls == ["quiesce"]
    assert engine.connected and engine.mode == "released" and engine.state()["reconnect_required"]
    assert engine.error == "native cleanup was not acknowledged"
    with pytest.raises(TransportError, match="Reconnect before resuming"):
        engine.resume()


@pytest.mark.anyio
async def test_release_that_failed_before_a_lost_connection_is_recovered_without_resuming(monkeypatch):
    engine = native_engine()
    engine.connected = True
    engine._native_engaged = True
    engine.switch.fail_quiesce = True
    engine.release()
    with pytest.raises(TransportError):
        await engine.wait_for_output_idle()
    recoveries = []

    async def recover_output(outputs):
        recoveries.append(outputs)
        engine.switch.uncertain = False
        return {"port_rgb": True}

    engine.switch.recover_output = recover_output
    engine._lost(TransportError("fixture connection reset"))
    assert not engine.state()["reconnect_required"]
    waits = loop_sleeps(monkeypatch, lambda: engine.connected)
    with pytest.raises(asyncio.CancelledError):
        await engine._keep_connected()
    assert waits == [1.0] and len(recoveries) == 1
    assert engine.connected and engine.mode == "released" and engine.error is None
    assert not engine.state()["reconnect_required"]
    engine.resume()
    assert engine.mode == "running"


@pytest.mark.anyio
async def test_stop_does_not_wait_for_an_automatic_recovery_attempt():
    engine = native_engine()
    engine.connected = True
    engine.switch.uncertain = True
    entered, canceled = asyncio.Event(), asyncio.Event()

    async def unreachable(outputs):
        entered.set()
        try:
            await asyncio.Future()
        finally:
            canceled.set()

    engine.switch.recover_output = unreachable
    engine._lost(TransportError("fixture connection reset"))
    engine._tasks = [engine._spawn("ee-connection")]
    await asyncio.wait_for(entered.wait(), 1)
    await asyncio.wait_for(engine.stop(), 1)
    assert canceled.is_set() and engine._tasks == [] and not engine.connected
    assert engine.switch.calls == ["quiesce", "close"]


@pytest.mark.anyio
async def test_release_drains_the_real_render_loop_before_native_quiescence():
    engine = native_engine()
    engine.connected = True
    engine.switch.write_gate.clear()
    renderer = asyncio.create_task(engine._render_loop())
    engine._tasks = [renderer]
    try:
        await asyncio.wait_for(engine.switch.write_started.wait(), 1)
        engine.release()
        released = asyncio.create_task(engine.wait_for_output_idle())
        await asyncio.sleep(0)
        assert not released.done() and not engine.switch.quiesced
        engine.switch.write_gate.set()
        await asyncio.wait_for(released, 1)
        assert engine.switch.calls[:3] == ["write-start", "write-ack", "quiesce"]
    finally:
        engine.switch.write_gate.set()
        await engine.stop()


@pytest.mark.anyio
async def test_canceled_quiescence_leaves_output_released_and_blocks_resume():
    engine = native_engine()
    engine._native_engaged = True
    entered = asyncio.Event()

    async def pending_cleanup(outputs):
        entered.set()
        await asyncio.Future()

    engine.switch.quiesce_native = pending_cleanup
    engine.release()
    task = asyncio.create_task(engine.wait_for_output_idle())
    await asyncio.wait_for(entered.wait(), 1)
    task.cancel()
    with pytest.raises(asyncio.CancelledError):
        await task
    assert engine.mode == "released" and engine.error == "lighting release was interrupted before confirmation"
    assert engine._native_blocked
    with pytest.raises(TransportError):
        engine.resume()


@pytest.mark.anyio
async def test_stop_lets_pending_baseline_initialization_finish_before_close():
    engine = native_engine()
    engine.switch.initialization_gate.clear()
    connector = asyncio.create_task(engine._keep_connected())
    engine._tasks = [connector]
    await asyncio.wait_for(engine.switch.initializing.wait(), 1)
    stopped = asyncio.create_task(engine.stop())
    await asyncio.sleep(0)
    assert not stopped.done() and "close" not in engine.switch.calls
    engine.switch.initialization_gate.set()
    await asyncio.wait_for(stopped, 1)
    assert engine.switch.calls.index("initialize-ack") < engine.switch.calls.index("close")
