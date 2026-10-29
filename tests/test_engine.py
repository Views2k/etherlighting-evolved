import asyncio
from datetime import datetime

import pytest

from etherlighting_evolved.color import Color
from etherlighting_evolved.engine import Engine, NotFound, to_output
from etherlighting_evolved.links import PortLink
from etherlighting_evolved.poe import PoePort
from etherlighting_evolved.switch import EtherlightSwitch
from etherlighting_evolved.transport import SimulatedTransport

NOON = datetime(2026, 6, 3, 12, 0)


def make_engine(**kw) -> Engine:
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
    assert outputs[0].level == 80
    assert outputs[1].level == 50


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
    assert {o.level for o in outputs} == {10}


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
    assert engine.zones[0].effect.params_json() == {"color": "#112233"}
    assert engine.brightness == 30


def test_a_bad_scene_changes_nothing():
    engine = make_engine()
    before = engine.snapshot()
    with pytest.raises(ValueError):
        engine.apply_snapshot({"zones": [{"name": "x", "ports": [1], "effect": "static"}],
                               "rules": [{"look": {"style": "wobble"}}]})
    assert engine.snapshot() == before


def test_level_dimming_moves_brightness_into_the_hardware_level():
    out = to_output(Color(128, 0, 0), 100, "level")
    assert out.color == Color(255, 0, 0) and out.level == 50
    assert to_output(Color(128, 0, 0), 100, "rgb").color == Color(128, 0, 0)
    assert to_output(Color(255, 255, 255), 0).level == 0


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
