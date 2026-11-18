import asyncio
from copy import deepcopy
from datetime import datetime

import httpx
import pytest
from test_engine_topology import engine_for_layout
from test_sfp_engine import engine_for_sfp, start_renderer
from test_sfp_precision import PrecisionSwitch

from etherlighting_evolved import timing
from etherlighting_evolved.api import create_app
from etherlighting_evolved.color import BLACK, Color
from etherlighting_evolved.effects import EFFECTS, RenderContext
from etherlighting_evolved.engine import Engine
from etherlighting_evolved.links import PortLink


def group(ports, effect="static", **params):
    return {"id": "mixed", "name": "Group", "ports": ports, "effect": {"id": effect, "params": params}}


@pytest.mark.parametrize("effect_id", EFFECTS)
def test_catalog_renders_each_optical_member_at_its_own_group_position(effect_id):
    engine = engine_for_layout(8, (9, 10))
    engine.set_zones([group([2, 7, 10], effect_id)])
    reference = deepcopy(engine.zone().effect)
    now = datetime(2026, 1, 12, 12, 15)
    for t in (0.0, 0.37, 2.5):
        expected = reference.render(t, RenderContext(3, now=now))
        colors, copper = engine.compose(t=t, now=now)
        assert colors[1] == expected[0] and colors[6] == expected[1]
        assert engine.sfp_indicators.color(10, t) == (expected[2], 100)
        assert len(copper) == 8 and set(engine.sfp_indicators.outputs()) == {10}
    assert [item["effect_zone"] for item in engine.sfp_indicators.state()] == [None, "mixed"]


def test_activity_uses_the_selected_optical_ports_own_counters_and_link():
    engine = engine_for_layout(8, (9, 10))
    engine.set_zones([group([4, 9, 10], "activity_glow", idle_color="black", active_color="white")])
    engine.links = {p: PortLink(p, True, p != 9, 10000, True) for p in (4, 9, 10)}
    engine.traffic = {4: 0.5e6, 9: 20e6, 10: 2e6}
    engine.compose(t=0)
    copper, _ = engine.compose(t=10)
    assert copper[3] == Color(64, 64, 64)
    assert engine.sfp_indicators.outputs()[9].color == BLACK
    assert engine.sfp_indicators.outputs()[10].color == Color(255, 255, 255)


def test_optical_zone_assignment_roundtrips_and_dormant_settings_resume_on_removal():
    engine = engine_for_layout(8, (9, 10), native=True)
    engine.set_sfp(9, {"mode": "off", "brightness": 25})
    engine.set_sfp(10, {"mode": "color", "brightness": 80, "color": "blue"})
    engine.set_sfp_sync({"mode": "together"})
    engine.set_brightness(40)
    original = engine.snapshot()
    scene = deepcopy(original)
    scene["zones"] = [group([1, 9, 10], color="red")]
    assert engine.apply_snapshot(scene, preserve_unchanged=True) == {9, 10}
    saved = engine.snapshot()
    assert saved["sfp"] == original["sfp"] and saved["sfp_sync"] == original["sfp_sync"]
    assert engine.validate_snapshot(saved) == saved
    assert engine.apply_snapshot(saved, preserve_unchanged=True) == set()
    engine.compose(t=0)
    assert engine.sfp_indicators.color(9, 0) == (Color(255, 0, 0), 10)
    assert engine.sfp_indicators.color(10, 0) == (Color(255, 0, 0), 32)
    with pytest.raises(ValueError, match="effect group"):
        engine.set_sfp(9, {"mode": "color"})
    engine.set_rules([{"name": "Selected", "ports": [10], "conditions": [],
                       "look": {"style": "solid", "color": "#123456", "brightness": 50}}])
    assert engine.sfp_indicators.color(10, 0) == (Color.parse("#123456"), 40)
    assert engine.apply_snapshot(original, preserve_unchanged=True) == {9, 10}
    assert engine.sfp_indicators.color(10, 0)[0] == BLACK
    engine.set_sfp_sync({"mode": "independent"})
    assert engine.sfp_indicators.color(10, 0) == (Color(0, 0, 255), 32)


def test_optical_only_group_samples_synchronized_breathing_without_copper_commands():
    engine = engine_for_layout(0, (1, 2))
    engine.switch.supported = False
    engine.connected = True
    engine.set_zones([group([2], "breathing", playback="synchronized")])
    first = engine.compose(t=0)
    initial = engine.sfp_indicators.outputs()[2]
    second = engine.compose(t=0.45)
    later = engine.sfp_indicators.outputs()[2]
    assert first == second == ([], [])
    assert initial != later and initial.breath is None and later.breath is None
    assert set(engine.sfp_indicators.outputs()) == {2}


def test_zone_requires_a_qualified_output_and_does_not_widen_manual_override_scope():
    engine = engine_for_layout(8, (10,))
    before = engine.snapshot()
    for ports in ([1, 9], [11]):
        with pytest.raises(ValueError):
            engine.set_zones([group(ports)])
        assert engine.snapshot() == before
    with pytest.raises(ValueError):
        engine.set_override(10, "red")
    engine.set_zones([group([10])])
    assert engine.zone().ports == (10,)


@pytest.mark.anyio
async def test_direct_zone_api_waits_for_output_and_restores_indicator_on_group_removal(config):
    app = create_app(config, autoconnect=False)
    engine = engine_for_sfp(gamma=1, transition=0)
    app.state.sessions.engine = engine
    engine.switch.write_gate.clear()
    start_renderer(engine)
    try:
        async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://127.0.0.1") as client:
            request = asyncio.create_task(client.put("/api/zones", json={"zones": [group([3, 10], color="red")]}))
            await asyncio.wait_for(engine.switch.write_started.wait(), 1)
            assert not request.done()
            engine.switch.write_gate.set()
            response = await asyncio.wait_for(request, 2)
            assert response.status_code == 200, response.text
            assert engine.switch.sfp_applied[10].color == Color(255, 0, 0)
            assert engine.sfp_indicators.acknowledged[10] == engine.sfp_indicators.revisions[10]
            response = await client.put("/api/effect", json={"id": "static", "params": {"color": "blue"}})
            assert response.status_code == 200, response.text
            assert response.json()["zones"][0]["ports"] == [*range(1, 9), 10]
            assert engine.switch.sfp_applied[10].color == Color(0, 0, 255)
            assert 9 not in engine.switch.sfp_applied
            response = await client.put("/api/zones", json={"zones": [group([3])]})
            assert response.status_code == 200 and 10 not in engine.switch.sfp_applied
            assert engine.sfp_indicators.acknowledged[10] == engine.sfp_indicators.revisions[10]
    finally:
        engine.switch.write_gate.set()
        await engine.stop()

@pytest.mark.anyio
async def test_effect_rule_membership_is_part_of_the_confirmed_zone_change(config):
    engine = engine_for_sfp(gamma=1, transition=0)
    engine.set_rules([{"name": "Bright group", "ports": [10], "conditions": [],
                       "look": {"style": "effect", "brightness": 50}}])
    app = create_app(config, autoconnect=False)
    app.state.sessions.engine = engine
    start_renderer(engine)
    try:
        async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://127.0.0.1") as client:
            result = await client.put("/api/zones", json={"zones": [group([4, 10], color="white")]})
            assert result.status_code == 200, result.text
            assert engine.switch.sfp_applied[10].color == Color(128, 128, 128)
    finally:
        await engine.stop()


@pytest.mark.anyio
async def test_first_write_after_enrollment_during_release_contains_the_new_group_frame(monkeypatch):
    now = [0.0]
    monkeypatch.setattr(timing, "monotonic", lambda: now[0])

    async def advance(delay, wake=None):
        if wake is None or not wake.is_set():
            now[0] += delay

    monkeypatch.setattr(timing, "wait", advance)
    switch = PrecisionSwitch()
    engine = Engine(switch, rules=[], gamma=1, transition=0, fps=1, max_commands=8)
    engine.connected = True
    engine.set_sfp(9, {"mode": "color", "effect": "pulse"})
    frames = []

    async def capture(copper, **kwargs):
        frames.append(kwargs["sfp"])
        if len(frames) == 1:
            engine.set_sfp(10, {"mode": "unmanaged"})
        else:
            engine._stopping = True
        return 0

    async def change_during_release(port):
        assert port == 10
        engine.set_zones([group([4, 10], color="red")])

    monkeypatch.setattr(switch, "write", capture)
    monkeypatch.setattr(switch, "release_sfp", change_during_release)
    await engine._render_loop()
    assert now[0] < 1 and len(frames) == 2
    assert frames[1][10].color == Color(255, 0, 0)
