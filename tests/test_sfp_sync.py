import asyncio
from copy import deepcopy

import httpx
import pytest
from test_sfp_engine import engine_for_sfp, start_renderer
from test_sfp_precision import PrecisionSwitch
from test_sfp_rules import alert, anomaly

from etherlighting_evolved.api import create_app
from etherlighting_evolved.color import BLACK, Color
from etherlighting_evolved.engine import Engine, to_output
from etherlighting_evolved.sfp import SfpSync
from etherlighting_evolved.transport import TransportError


def test_sync_defaults_preserve_existing_independent_behavior():
    engine = engine_for_sfp(gamma=1)
    engine.set_sfp(9, {"mode": "color", "color": "red"})
    engine.set_sfp(10, {"mode": "color", "color": "blue"})
    assert engine.state()["sfp_sync"] == {"mode": "independent", "source_port": 1}
    assert engine.sfp_outputs()[9].color == Color(255, 0, 0)
    assert engine.sfp_outputs()[10].color == Color(0, 0, 255)
    assert [item["effective_settings"] for item in engine.sfp_state()] == [
        engine.sfp[9].to_dict(), engine.sfp[10].to_dict(),
    ]


def test_together_uses_sfp_nine_settings_and_clock_without_overwriting_ten():
    engine = engine_for_sfp(gamma=1, brightness=60)
    engine.set_sfp(9, {"mode": "color", "color": "blue", "effect": "pulse", "period": 4,
                       "brightness": 40, "color_brightness": 50})
    engine.set_sfp(10, {"mode": "off", "color": "red", "brightness": 10})
    original_ten = engine.sfp[10]
    engine.set_sfp_sync({"mode": "together"})
    for at in (0, 0.25, 0.8, 1.75, 2.5):
        assert engine._sfp_color(9, at) == engine._sfp_color(10, at)
    assert engine._sfp_color(10, 0) == (Color(0, 0, 128), 24)
    assert engine.sfp_state()[1]["effective_settings"] == engine.sfp[9].to_dict()
    revisions = dict(engine._sfp_revisions)
    engine.set_sfp(9, {"color": "green"})
    assert all(engine._sfp_revisions[p] > revisions[p] for p in (9, 10))
    assert engine._sfp_color(9, 1) == engine._sfp_color(10, 1)
    assert engine.sfp[10] == original_ten
    engine.set_sfp_sync({"mode": "independent"})
    assert engine.sfp_outputs()[10].color == BLACK
    assert engine.sfp_state()[1]["effective_settings"] == original_ten.to_dict()


def test_ethernet_follows_final_composed_source_and_applies_gamma_and_brightness_once():
    engine = engine_for_sfp(gamma=2.2, brightness=37, transition=0)
    engine.set_effect("static", {"color": "#c08040", "brightness_color": 70})
    engine.set_sfp(9, {"mode": "off", "brightness": 2})
    engine.set_sfp(10, {"mode": "unmanaged", "brightness": 3})
    engine.set_sfp_sync({"mode": "ethernet", "source_port": 4})
    engine.compose(t=0)
    expected = to_output(*engine._shown[3], engine.dimming, engine.gamma, precise=True)
    for output in engine.sfp_outputs().values():
        assert output.color == expected.color
        assert output.level == expected.level
    assert all(engine._sfp_color(p, 2) == engine._shown[3] for p in (9, 10))
    assert all(item["effective_settings"] is None for item in engine.sfp_state())
    engine.set_override(4, "#1234ab", brightness=81)
    engine.compose(t=0.1)
    assert engine._sfp_color(9, 0.1) == (Color.parse("#1234ab"), 81)
    engine.clear_override(4)
    engine.set_rules([{"name": "Source", "ports": [4], "conditions": [],
                       "look": {"color": "#aabbcc", "brightness": 62}}])
    engine.compose(t=0.2)
    assert engine._sfp_color(10, 0.2) == (Color.parse("#aabbcc"), 62)


def test_ethernet_tracks_effect_and_crossfade_without_a_second_envelope():
    engine = engine_for_sfp(gamma=1, brightness=70, transition=1)
    engine.set_sfp_sync({"mode": "ethernet", "source_port": 8})
    engine.set_effect("breathing", {"playback": "custom", "color": "blue"})
    seen = []
    for at in (0, 0.2, 0.6, 1.2):
        engine.compose(t=at, clock=at)
        followed = engine._sfp_color(9, at)
        assert followed == engine._shown[7] == engine._sfp_color(10, at)
        seen.append(followed)
    assert len(set(seen)) > 1
    engine.set_effect("static", {"color": "red"})
    engine.compose(t=1.3, clock=1.3)
    engine.compose(t=1.8, clock=1.8)
    color, brightness = engine._sfp_color(10, 1.8)
    assert (color, brightness) == engine._shown[7]
    assert color.r > 0 and color.b > 0


@pytest.mark.parametrize("gamma", [1, 2.2, 3])
def test_ethernet_follower_keeps_fractional_intensity_in_precise_sfp_output(gamma):
    engine = Engine(PrecisionSwitch(), gamma=gamma, brightness=7, transition=0, rules=[])
    engine.set_effect("static", {"color": "#1983bd", "brightness_color": 11})
    engine.set_sfp_sync({"mode": "ethernet", "source_port": 2})
    engine.compose(t=0)
    expected = to_output(*engine._shown[1], engine.dimming, gamma, precise=True)
    for output in engine.sfp_outputs().values():
        assert output.precise_rgb == expected.precise_rgb
        assert output.level == expected.level
        assert output.breath is None


@pytest.mark.parametrize("mode", ["together", "ethernet"])
def test_explicit_sfp_rules_keep_priority_and_targeting_over_sync(mode):
    engine = engine_for_sfp(gamma=1, brightness=40, transition=0)
    engine.set_effect("static", {"color": "blue"})
    engine.set_sfp(9, {"mode": "color", "color": "blue", "brightness": 50})
    engine.set_sfp(10, {"mode": "off", "brightness": 1})
    engine.set_sfp_sync({"mode": mode})
    engine.compose(t=0)
    first = engine.add_rule(alert([10], brightness=80))
    engine.add_rule(alert([10], color="green", brightness=20))
    anomaly(engine, 10, True)
    engine._sync_sfp_rules()
    assert engine._sfp_color(10, 0) == (Color(255, 0, 0), 80 if mode == "ethernet" else 40)
    assert engine._sfp_color(9, 0)[0] == Color(0, 0, 255)
    assert engine.sfp_state()[1]["rule"]["id"] == first.id
    engine.delete_rule(first.id)
    assert engine._sfp_color(10, 0)[0] == Color(0, 255, 0)
    anomaly(engine, 10, False)
    assert engine._sfp_color(9, 0) == engine._sfp_color(10, 0)


@pytest.mark.parametrize("changes", [{}, {"mode": "invalid"}, {"source_port": 9}, {"source_port": 0},
                                      {"source_port": True}, {"source_port": "1"}, {"source_port": 1.0},
                                      {"mode": None}, {"unexpected": 1}])
def test_invalid_sync_changes_are_atomic(changes):
    engine = engine_for_sfp()
    before = deepcopy((engine.snapshot(), engine._sfp_revisions, engine._sfp_release_ports))
    with pytest.raises(ValueError):
        engine.set_sfp_sync(changes)
    assert (engine.snapshot(), engine._sfp_revisions, engine._sfp_release_ports) == before


def test_sync_requires_verified_indicators_and_available_source():
    engine = engine_for_sfp()
    engine.switch.sfp_ports = ()
    with pytest.raises(ValueError, match="unavailable"):
        engine.set_sfp_sync({"mode": "together"})
    engine.switch.sfp_ports = (9,)
    with pytest.raises(ValueError, match="unavailable"):
        engine.set_sfp_sync({"mode": "ethernet"})
    engine.switch.sfp_ports = (9, 10)
    engine.num_ports = 4
    with pytest.raises(ValueError, match="available Ethernet"):
        engine.set_sfp_sync({"source_port": 8})


def test_scene_roundtrip_preserves_sync_and_old_scenes_restore_independent():
    engine = engine_for_sfp()
    engine.set_sfp_sync({"mode": "ethernet", "source_port": 6})
    engine.set_sfp(9, {"mode": "color", "effect": "two_color"})
    saved = engine.snapshot()
    restored = engine_for_sfp()
    restored.apply_snapshot(saved)
    assert restored.snapshot() == saved
    assert restored.frame_message()["sfp_sync"] == saved["sfp_sync"]
    assert all(item["control"]["state"] == "pending" for item in restored.sfp_state())
    invalid = deepcopy(saved)
    invalid["sfp_sync"]["source_port"] = 10
    with pytest.raises(ValueError):
        restored.apply_snapshot(invalid)
    assert restored.snapshot() == saved
    del saved["sfp_sync"]
    restored.apply_snapshot(saved)
    assert restored.sfp_sync == SfpSync()
    assert restored.sfp[9].effect == "two_color"


@pytest.mark.anyio
async def test_sync_waits_for_both_matching_acknowledgments_and_release_is_confirmed():
    engine = engine_for_sfp(gamma=1)
    engine.switch.write_gate.clear()
    engine.set_sfp(9, {"mode": "color", "color": "blue"})
    engine.set_sfp_sync({"mode": "together"})
    start_renderer(engine)
    try:
        await asyncio.wait_for(engine.switch.write_started.wait(), 1)
        waiting = asyncio.create_task(engine.wait_for_sfp_sync())
        await asyncio.sleep(0)
        assert not waiting.done()
        engine.switch.write_gate.set()
        await asyncio.wait_for(waiting, 1)
        assert all(engine._sfp_acknowledged[p] == engine._sfp_revisions[p] for p in (9, 10))
        assert engine.switch.sfp_applied[9] == engine.switch.sfp_applied[10]
        engine.set_sfp_sync({"mode": "independent"})
        await asyncio.wait_for(engine.wait_for_sfp_sync(), 1)
        assert 10 not in engine.switch.sfp_applied
        assert 9 in engine.switch.sfp_applied
        assert "release-sfp-10" in engine.switch.calls and "release-sfp-9" not in engine.switch.calls
    finally:
        engine.switch.write_gate.set()
        await engine.stop()


@pytest.mark.anyio
async def test_sync_cleanup_failure_retains_existing_fail_closed_behavior():
    engine = engine_for_sfp()
    engine.set_sfp_sync({"mode": "ethernet"})
    engine.switch.sfp_applied.update(engine.sfp_outputs())
    engine.switch.fail_sfp_release = True
    engine.set_sfp_sync({"mode": "independent"})
    with pytest.raises(TransportError, match="SFP release"):
        await engine.wait_for_sfp_sync()
    assert engine.mode == "released" and engine._native_blocked
    with pytest.raises(TransportError, match="Reconnect"):
        engine.resume()


@pytest.mark.anyio
async def test_new_sync_request_cannot_be_acknowledged_by_an_older_inflight_frame():
    engine = engine_for_sfp()
    engine.switch.write_gate.clear()
    engine.set_sfp_sync({"mode": "ethernet", "source_port": 1})
    waiting = asyncio.create_task(engine.wait_for_sfp_sync())
    start_renderer(engine)
    try:
        await asyncio.wait_for(engine.switch.write_started.wait(), 1)
        engine.set_sfp_sync({"source_port": 8})
        with pytest.raises(ValueError, match="superseded"):
            await asyncio.wait_for(waiting, 1)
        assert all(engine._sfp_acknowledged[p] != engine._sfp_revisions[p] for p in (9, 10))
        engine.switch.write_gate.set()
        await asyncio.wait_for(engine.wait_for_sfp_sync(), 1)
        assert all(engine._sfp_acknowledged[p] == engine._sfp_revisions[p] for p in (9, 10))
    finally:
        engine.switch.write_gate.set()
        await engine.stop()


@pytest.mark.anyio
async def test_sync_api_validates_route_body_and_returns_acknowledged_state(config):
    app = create_app(config, autoconnect=False)
    engine = engine_for_sfp()
    app.state.sessions.engine = engine
    engine.set_sfp(9, {"mode": "color", "color": "blue"})
    start_renderer(engine)
    try:
        async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://127.0.0.1") as client:
            result = await client.put("/api/sfp/sync", json={"mode": "together", "source_port": 2})
            assert result.status_code == 200, result.text
            state = result.json()
            assert state["sfp_sync"] == {"mode": "together", "source_port": 2}
            assert all(item["acknowledged_revision"] == item["revision"] for item in state["sfp"])
            assert (await client.get("/api/state")).json()["sfp_sync"] == state["sfp_sync"]
            original = engine.snapshot()
            for invalid in ({"mode": "bad"}, {"source_port": True}, {"source_port": 10}, {"extra": 1}, {}):
                result = await client.put("/api/sfp/sync", json=invalid)
                assert result.status_code == 400, result.text
                assert engine.snapshot() == original
            assert (await client.put("/api/sfp/sync", json=[])).status_code == 422
            assert (await client.post("/api/presets", json={"name": "Synced"})).status_code == 201
            assert (await client.put("/api/sfp/sync", json={"mode": "independent"})).status_code == 200
            restored = await client.post("/api/presets/Synced/apply")
            assert restored.status_code == 200
            assert restored.json()["sfp_sync"] == original["sfp_sync"]
    finally:
        await engine.stop()
