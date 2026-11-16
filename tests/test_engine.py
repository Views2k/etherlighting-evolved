import asyncio
from datetime import datetime, timedelta, timezone

import pytest

from etherlighting_evolved.color import Color
from etherlighting_evolved.engine import Engine, NotFound, to_output
from etherlighting_evolved.links import PortLink
from etherlighting_evolved.poe import PoePort
from etherlighting_evolved.switch import EtherlightSwitch, PortOutput
from etherlighting_evolved.transport import SimulatedTransport, TransportError

NOON = datetime(2026, 6, 3, 12, 0)


def make_engine(**kw) -> Engine:
    # Most tests check which color a port gets, so they turn off the brightness curve and crossfades.
    kw.setdefault("gamma", 1.0)
    kw.setdefault("transition", 0)
    engine = Engine(EtherlightSwitch(SimulatedTransport(latency=0)), **kw)
    engine.links = {p: PortLink(p, True, True, 10000, True) for p in range(1, 9)}
    return engine


def hexes(colors):
    return [c.to_hex() for c in colors]


def test_fresh_install_is_white_online_red_offline():
    engine = make_engine()
    engine.links[3] = PortLink(3, True, False, 0, False)
    colors, _ = engine.compose(t=0, now=NOON)
    assert hexes(colors) == ["#ffffff"] * 2 + ["#ff0000"] + ["#ffffff"] * 5
    assert engine.last_sources[2]["name"] == "Offline"
    assert engine.last_sources[0]["kind"] == "zone"


def test_anomaly_beats_offline():
    engine = make_engine()
    engine.links[4] = PortLink(4, True, False, 0, False, anomaly=1)
    colors, _ = engine.compose(t=0.1, now=NOON)  # the blink is on for the first half of each cycle
    assert colors[3].to_hex() == "#ff9f0a"


def test_override_beats_rules_and_brightness():
    engine = make_engine(brightness=50)
    engine.links[1] = PortLink(1, True, False, 0, False)
    engine.set_override(1, "#00ff00", 80)
    colors, outputs = engine.compose(t=0, now=NOON)
    assert colors[0].to_hex() == "#00ff00"
    assert outputs[0] == PortOutput(Color(0, 204, 0), 100)
    assert outputs[1] == PortOutput(Color(128, 128, 128), 100)


def test_zones_run_their_own_effects():
    engine = make_engine(rules=[])
    engine.set_zones([
        {"name": "Left", "ports": [1, 2, 3, 4], "effect": {"id": "static", "params": {"color": "blue"}}},
        {"name": "Right", "ports": [5, 6], "effect": "static"},
    ])
    colors, _ = engine.compose(t=0, now=NOON)
    assert hexes(colors) == ["#0000ff"] * 4 + ["#ffffff"] * 2 + ["#000000"] * 2
    assert [z.id for z in engine.zones] == ["left", "right"]
    engine.update_params({"color": "red"}, zone="right")
    assert engine.compose(t=0, now=NOON)[0][4].to_hex() == "#ff0000"


def test_zones_cannot_overlap():
    engine = make_engine()
    with pytest.raises(ValueError, match="more than one zone"):
        engine.set_zones([{"name": "a", "ports": [1, 2]}, {"name": "b", "ports": [2, 3]}])
    with pytest.raises(NotFound):
        engine.set_effect("static", zone="nope")


def test_poe_and_time_rules():
    engine = make_engine(rules=[
        {"name": "Night", "conditions": [{"field": "time", "op": "between", "value": ["22:00", "07:00"]}],
         "look": {"style": "effect", "brightness": 10}},
        {"name": "PoE+++", "conditions": [{"field": "poe", "op": "is", "value": "poe+++"}],
         "look": {"color": "#ff375f"}},
    ])
    engine.poe = {7: PoePort(7, True, True, pd_class=8, power_w=70)}
    colors, _ = engine.compose(t=0, now=NOON)
    assert colors[6].to_hex() == "#ff375f" and colors[0].to_hex() == "#ffffff"
    _, outputs = engine.compose(t=0, now=datetime(2026, 6, 3, 23, 0))
    assert set(outputs) == {PortOutput(Color(26, 26, 26), 100)}


def test_rule_editing():
    engine = make_engine()
    rule = engine.add_rule({"name": "Uplink", "conditions": [{"field": "uplink", "op": "is", "value": True}],
                            "look": {"color": "blue"}}, position=0)
    assert engine.rules[0].id == rule.id
    engine.update_rule(rule.id, {"look": {"color": "#00ffff"}})
    assert engine.rules[0].look.color.to_hex() == "#00ffff"
    ids = [r.id for r in engine.rules]
    engine.reorder_rules(list(reversed(ids)))
    assert [r.id for r in engine.rules] == list(reversed(ids))
    with pytest.raises(ValueError):
        engine.reorder_rules(ids[:1])
    engine.delete_rule(rule.id)
    with pytest.raises(NotFound):
        engine.delete_rule(rule.id)


@pytest.mark.parametrize("ids", [("duplicate", "duplicate"), ("x" * 32 + "a", "x" * 32 + "b")])
def test_rule_replacement_rejects_duplicate_normalized_ids_without_changing_scene(ids):
    specs = [{"id": value, "name": name, "ports": [port]} for value, name, port in
             zip(ids, ("First", "Second"), (1, 2), strict=True)]
    with pytest.raises(ValueError, match="unique ID"):
        make_engine(rules=specs)
    engine = make_engine()
    before, revision = engine.snapshot(), engine.revision
    with pytest.raises(ValueError, match="unique ID"):
        engine.set_rules(specs)
    assert engine.snapshot() == before and engine.revision == revision
    with pytest.raises(ValueError, match="unique ID"):
        engine.apply_snapshot({**before, "brightness": 11, "rules": specs})
    assert engine.snapshot() == before and engine.revision == revision


def test_reorder_rejects_duplicate_ids_in_an_existing_invalid_list_without_replacing_rules():
    engine = make_engine()
    first, second = engine.rules
    second.id = first.id
    revision = engine.revision
    with pytest.raises(ValueError, match="exactly once"):
        engine.reorder_rules([first.id, first.id])
    assert engine.rules == [first, second] and engine.rules[0] is first
    assert engine.revision == revision


def test_state_and_frames_report_host_clock_without_persisting_it(monkeypatch):
    instant = datetime(2026, 10, 31, 23, 59, 58, tzinfo=timezone(timedelta(hours=-5), "Fixture local zone"))
    class HostDatetime:
        @staticmethod
        def now():
            return instant
    monkeypatch.setattr("etherlighting_evolved.engine.datetime", HostDatetime)
    engine = make_engine()
    clock = {"local_iso": "2026-10-31T23:59:58-05:00", "zone_name": "Fixture local zone"}
    # Keep the fixture independent of the operating system's own timezone.
    class LocalDatetime(datetime):
        def astimezone(self, tz=None):
            return self if tz is None else super().astimezone(tz)
    instant = LocalDatetime.fromisoformat(clock["local_iso"]).replace(tzinfo=instant.tzinfo)
    assert engine.state()["schedule_clock"] == clock
    assert engine.state_message()["state"]["schedule_clock"] == clock
    assert engine.frame_message()["schedule_clock"] == clock
    assert "schedule_clock" not in engine.snapshot()
    instant += timedelta(seconds=3)
    assert engine.frame_message()["schedule_clock"]["local_iso"] == "2026-11-01T00:00:01-05:00"


def test_snapshot_round_trip():
    engine = make_engine(brightness=40)
    engine.set_zones([{"name": "All", "ports": list(range(1, 9)), "effect": {"id": "rainbow", "params": {"speed": 1}}}])
    engine.set_override(2, "#abcdef")
    scene = engine.snapshot()

    other = make_engine()
    other.apply_snapshot(scene)
    assert other.snapshot() == scene


def test_old_single_effect_scenes_still_load():
    engine = make_engine()
    engine.apply_snapshot({"effect": "static", "params": {"color": "#112233"}, "brightness": 30})
    assert engine.zones[0].effect.params_json() == {
        "color": "#112233", "random_color": False, "color_transition": 8.0, "brightness_color": 100.0,
    }
    assert engine.brightness == 30
    for elapsed in (0, 8, 17):
        colors, outputs = engine.compose(t=elapsed, now=NOON)
        assert colors == [Color(17, 34, 51)] * 8
        assert outputs == [PortOutput(Color(5, 10, 15), 100)] * 8

    migrated = engine.snapshot()
    restored = make_engine()
    restored.apply_snapshot(migrated)
    assert restored.snapshot() == migrated
    assert restored.compose(t=17, now=NOON) == engine.compose(t=17, now=NOON)


def test_a_bad_scene_changes_nothing():
    engine = make_engine()
    before = engine.snapshot()
    with pytest.raises(ValueError):
        engine.apply_snapshot({"zones": [{"name": "x", "ports": [1], "effect": "static"}],
                               "rules": [{"look": {"style": "wobble"}}]})
    assert engine.snapshot() == before


def test_brightness_stays_in_the_color_bytes_at_full_hardware_level():
    assert to_output(Color(128, 0, 0), 100, gamma=1.0) == PortOutput(Color(128, 0, 0), 100)
    assert to_output(Color(255, 255, 255), 0) == PortOutput(Color(0, 0, 0), 100)
    assert to_output(Color(255, 255, 255), 0).preview().is_off


def test_gamma_encodes_effect_amplitude_and_global_brightness_in_rgb():
    white = Color(255, 255, 255)
    assert to_output(white, 50) == PortOutput(Color(55, 55, 55), 100)  # 0.5 ** 2.2
    assert to_output(Color(128, 128, 128), 100) == PortOutput(Color(56, 56, 56), 100)
    assert to_output(white, 100) == PortOutput(white, 100)
    assert to_output(Color(128, 64, 0), 100) == PortOutput(Color(56, 12, 0), 100)


def test_a_new_effect_crossfades_from_the_old_one():
    engine = make_engine(rules=[], transition=1.0)
    engine.compose(t=0, now=NOON, clock=100.0)
    engine.set_effect("static", {"color": "#ff0000"})
    start, _ = engine.compose(t=0, now=NOON, clock=100.0)
    middle, _ = engine.compose(t=0, now=NOON, clock=100.5)
    end, _ = engine.compose(t=0, now=NOON, clock=101.01)
    assert start[0] == Color(255, 255, 255)
    assert middle[0] == Color(255, 128, 128)
    assert end[0] == Color(255, 0, 0)


def test_a_port_going_offline_fades_to_red():
    engine = make_engine(transition=0.5)
    engine.compose(t=0, now=NOON, clock=10.0)
    engine.links[2] = PortLink(2, True, False, 0, False)
    assert engine.compose(t=0, now=NOON, clock=10.0)[0][1] == Color(255, 255, 255)
    assert engine.compose(t=0, now=NOON, clock=10.6)[0][1] == Color(255, 0, 0)


def test_manual_colors_apply_at_once():
    engine = make_engine(transition=2.0)
    engine.compose(t=0, now=NOON, clock=5.0)
    engine.set_override(1, "#00ff00")
    assert engine.compose(t=0, now=NOON, clock=5.0)[0][0] == Color(0, 255, 0)


def test_busy_switch_slows_engine_down_and_calm_switch_speeds_it_up(monkeypatch):
    clock = [100.0]
    monkeypatch.setattr("etherlighting_evolved.engine.timing.monotonic", lambda: clock[0])
    engine = make_engine()
    engine._adjust_throttle(0.10)
    assert engine.command_budget == 15
    for _ in range(10):
        clock[0] += 1
        engine._adjust_throttle(0.05)
    assert engine.command_budget == 5
    for _ in range(10):
        clock[0] += 1
        engine._adjust_throttle(0.9)
    assert engine.command_budget == 30  # shell recovery never exceeds its own ceiling


async def wait_for(condition, timeout=2.0):
    for _ in range(int(timeout / 0.02)):
        if condition():
            return True
        await asyncio.sleep(0.02)
    return condition()


@pytest.mark.anyio
async def test_engine_drives_the_simulator():
    sim = SimulatedTransport(latency=0)
    engine = Engine(EtherlightSwitch(sim), fps=30, link_poll=0.5)
    await engine.start()
    try:
        assert await wait_for(lambda: len(sim.leds) == 8)
        assert set(sim.leds.values()) == {("ffffff", 100)}

        sim.set_link(6, False)
        assert await wait_for(lambda: sim.leds[6] == ("ff0000", 100))

        engine.set_override(1, "#00ff00")
        assert await wait_for(lambda: sim.leds[1] == ("00ff00", 100), timeout=0.2)

        engine.release()
        writes = sim.command_count
        await asyncio.sleep(0.2)
        assert sim.command_count == writes
    finally:
        await engine.stop()


@pytest.mark.anyio
async def test_a_failed_loop_is_reported_and_reconnect_restarts_it(caplog):
    sim = SimulatedTransport(latency=0)
    switch = EtherlightSwitch(sim)
    engine = Engine(switch, fps=30, rules=[])
    await engine.start()
    try:
        assert await wait_for(lambda: len(sim.leds) == 8)
        write = switch.write

        async def invariant_failure(*args, **kwargs):
            raise ValueError("fixture invariant failure")

        switch.write = invariant_failure
        engine.set_override(1, "#00ff00")
        assert await wait_for(lambda: engine.state()["reconnect_required"])
        assert engine.error == "Lighting stopped unexpectedly. Reconnect the switch."
        assert "ee-render stopped" in caplog.text and "fixture invariant failure" in caplog.text
        switch.write = write

        async def recovered(outputs):
            return engine.switch_info

        switch.recover_output = recovered
        await engine.recover_connection()
        assert not engine.state()["reconnect_required"] and engine.mode == "running"
        assert len(engine._tasks) == 5 and not any(task.done() for task in engine._tasks)
        engine.set_override(1, "#0000ff")
        assert await wait_for(lambda: sim.leds[1] == ("0000ff", 100))
    finally:
        await engine.stop()


class FailingFans:
    def __init__(self):
        self.failing, self.polls = True, 0

    async def poll(self):
        if self.failing:
            raise ValueError("fixture fan failure")
        self.polls += 1

    def interval(self):
        return 0.05

    def to_dict(self):
        return {}


@pytest.mark.anyio
@pytest.mark.parametrize("loop", ["ee-status", "ee-fans"])
async def test_reconnect_restarts_a_failed_status_or_fan_loop(loop, caplog):
    switch = EtherlightSwitch(SimulatedTransport(latency=0))
    engine = Engine(switch, fps=30, rules=[], link_poll=0.5)
    fans, read_links = FailingFans(), switch.read_links
    engine.attach_fans(fans)
    if loop == "ee-status":
        fans.failing = False

        async def broken_links():
            raise ValueError("fixture status failure")

        switch.read_links = broken_links
    await engine.start()
    try:
        assert await wait_for(lambda: engine.state()["reconnect_required"])
        assert f"{loop} stopped" in caplog.text
        assert engine.mode == "running" and engine.error == "Lighting stopped unexpectedly. Reconnect the switch."
        fans.failing, switch.read_links = False, read_links

        async def recovered(outputs):
            return engine.switch_info

        switch.recover_output = recovered
        sequence, polls = engine._telemetry_sequence, fans.polls
        await engine.recover_connection()
        assert not engine.state()["reconnect_required"] and engine.error is None
        assert await wait_for(lambda: engine._telemetry_sequence > sequence and fans.polls > polls)
    finally:
        await engine.stop()


@pytest.mark.anyio
@pytest.mark.parametrize("attempt_succeeds", [False, True])
async def test_a_reconnect_attempt_does_not_clear_a_failed_loop(attempt_succeeds):
    switch = EtherlightSwitch(SimulatedTransport(latency=0))
    engine = Engine(switch, fps=30, rules=[], reconnect_delay=1)
    await engine.start()
    try:
        assert await wait_for(lambda: engine.connected)
        connect, attempting, finish = switch.connect, asyncio.Event(), asyncio.Event()

        async def slow_connect():
            attempting.set()
            await finish.wait()
            if not attempt_succeeds:
                raise TransportError("fixture connection refused")
            await connect()

        def broken_compose(*args, **kwargs):
            raise ValueError("fixture effect failure")

        switch.connect = slow_connect
        engine._lost(TransportError("fixture connection lost"))
        await asyncio.wait_for(attempting.wait(), 3)
        engine.compose = broken_compose
        assert await wait_for(lambda: engine.state()["reconnect_required"])
        finish.set()
        if attempt_succeeds:
            assert await wait_for(lambda: engine.connected)
        else:
            assert await wait_for(lambda: engine._reconnect_failures == 1)
        state = engine.state()
        assert state["reconnect_required"] and state["error"] == "Lighting stopped unexpectedly. Reconnect the switch."
        with pytest.raises(TransportError):
            engine.resume()

        del engine.compose
        switch.connect = connect

        async def recovered(outputs):
            return engine.switch_info

        switch.recover_output = recovered
        await engine.recover_connection()
        assert not engine.state()["reconnect_required"] and engine.error is None
        assert len(engine._tasks) == 5 and not any(task.done() for task in engine._tasks)
    finally:
        await engine.stop()


@pytest.mark.anyio
async def test_the_command_budget_holds_while_an_effect_runs():
    sim = SimulatedTransport(latency=0)
    engine = Engine(EtherlightSwitch(sim), fps=30, max_commands=20, effect="rainbow", effect_params={"speed": 2})
    await engine.start()
    try:
        await asyncio.sleep(0.3)
        before, started = sim.command_count, asyncio.get_running_loop().time()
        await asyncio.sleep(2.0)
        rate = (sim.command_count - before) / (asyncio.get_running_loop().time() - started)
        assert 5 < rate <= 24  # 20 per second, plus one frame of slack
    finally:
        await engine.stop()


@pytest.mark.anyio
async def test_a_busy_switch_gets_fewer_commands():
    sim = SimulatedTransport(latency=0)
    sim.base_cpu = 0.9
    engine = Engine(EtherlightSwitch(sim), link_poll=0.5, max_commands=30, effect="rainbow")
    await engine.start()
    try:
        for _ in range(40):
            if engine.command_budget < 30:
                break
            await asyncio.sleep(0.1)
        assert engine.command_budget < 30
        assert engine.state()["stats"]["switch_cpu_idle"] < 0.3
    finally:
        await engine.stop()
