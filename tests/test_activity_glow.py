import copy
import math
from dataclasses import replace

import pytest
from fastapi.testclient import TestClient

from etherlighting_evolved.api import create_app
from etherlighting_evolved.color import BLACK, Color
from etherlighting_evolved.effects import create_effect
from etherlighting_evolved.effects.base import RenderContext
from etherlighting_evolved.engine import Engine
from etherlighting_evolved.links import PortLink
from etherlighting_evolved.switch import EtherlightSwitch
from etherlighting_evolved.transport import SimulatedTransport


def context(rates, *, uplinks=(), offline=(), unknown=()):
    links = {port: PortLink(port, True, port not in offline, 10000, True, uplink=port in uplinks)
             for port in range(1, len(rates) + 1) if port not in unknown}
    return RenderContext(len(rates), links=links,
                         traffic_bps={port: rate for port, rate in enumerate(rates, 1) if rate is not None})


def glow(count, **params):
    return create_effect("activity_glow", count, {"idle_color": "black", "active_color": "white", **params})


def test_activity_ranks_device_ports_and_ignores_the_busy_uplink():
    effect = glow(4)
    ctx = context([1e10, 2e6, 1e6, 0], uplinks=(1,))
    assert effect.render(0, ctx) == [BLACK] * 4
    assert effect.render(10, ctx) == [BLACK, Color(255, 255, 255), Color(128, 128, 128), BLACK]


def test_activity_floor_keeps_tiny_traffic_from_becoming_full_brightness():
    effect = glow(2, minimum_mbps=10)
    ctx = context([1e6, 0.5e6])
    effect.render(0, ctx)
    assert effect.render(10, ctx) == [Color(26, 26, 26), Color(13, 13, 13)]


@pytest.mark.parametrize("rate", [None, -1, float("nan"), float("inf"), "unknown"])
def test_missing_or_invalid_rates_do_not_illuminate_ports_or_set_the_scale(rate):
    effect = glow(2)
    ctx = context([rate, 1e6])
    effect.render(0, ctx)
    assert effect.render(10, ctx) == [BLACK, Color(255, 255, 255)]


def test_unknown_and_offline_links_do_not_set_the_scale():
    effect = glow(3)
    ctx = context([1e10, 1e10, 1e6], unknown=(1,), offline=(2,))
    effect.render(0, ctx)
    assert effect.render(10, ctx) == [BLACK, BLACK, Color(255, 255, 255)]


def test_activity_uses_soft_attack_and_longer_release_without_a_color_cut():
    effect = glow(1)
    busy, idle = context([1e6]), context([0])
    assert effect.render(0, busy) == [BLACK]
    peak = effect.render(0.25, busy)
    assert peak == [Color(161, 161, 161)]
    assert effect.render(0.25, idle) == peak
    assert effect.render(1.45, idle) == [Color(59, 59, 59)]


@pytest.mark.parametrize("fps", [4, 20, 120])
def test_activity_response_is_based_on_elapsed_time_not_render_frequency(fps):
    effect = glow(1)
    ctx = context([1e6])
    for step in range(fps + 1):
        frame = effect.render(step / fps, ctx)
    assert frame == [Color(250, 250, 250)]


def test_repeated_or_rewound_time_does_not_jump_or_double_advance():
    effect = glow(1)
    ctx = context([1e6])
    effect.render(0, ctx)
    before = effect.render(0.25, ctx)
    assert effect.render(0.25, ctx) == before
    assert effect.render(0.1, ctx) == before
    assert effect.render(0.1, ctx) == before
    expected = 1 - math.exp(-0.5 / 0.25)
    assert effect.render(0.35, ctx) == [Color(255, 255, 255).scaled(expected)]


def test_speed_change_advances_old_interval_before_using_the_new_speed():
    effect, reference = glow(1), glow(1)
    ctx = context([1e6])
    effect.render(0, ctx)
    reference.render(0, ctx)
    effect.update_at({"speed": 4}, 0.25)
    expected = reference.render(0.25, ctx)
    reference.update_at({"speed": 4}, 0.25)
    assert effect.render(0.25, ctx) == expected
    assert effect.render(0.5, ctx) == reference.render(0.5, ctx)


def test_invalid_param_update_keeps_activity_state_unchanged():
    effect = glow(1)
    effect.render(0, context([1e6]))
    before = copy.deepcopy((effect.p, effect._level, effect._target, effect._last))
    with pytest.raises(ValueError):
        effect.update_at({"speed": "invalid"}, 1)
    assert (effect.p, effect._level, effect._target, effect._last) == before


def test_changing_a_color_or_its_brightness_preserves_traffic_envelope():
    effect = glow(1)
    ctx = context([1e6])
    effect.render(0, ctx)
    effect.render(0.25, ctx)
    effect.update_at({"brightness_active_color": 50, "active_color": "#00ff00"}, 0.25)
    assert effect.render(0.25, ctx) == [Color(0, 81, 0)]
    assert effect.params_json()["active_color"] == "#00ff00"


@pytest.mark.parametrize("effect_id", ["traffic", "traffic_trails", "activity_glow"])
def test_uplink_uses_normal_idle_appearance_and_releases_prior_activity(effect_id):
    effect = create_effect(effect_id, 2)
    idle = create_effect(effect_id, 2)
    params = {param.key: "#ffffff" for param in effect.params if param.type == "color"}
    params.update({f"random_{key}": True for key in list(params)})
    effect.update(params)
    idle.update(params)
    ctx = context([1e9, 1e9])
    reference = context([0, 1e9])
    effect.render(0, ctx)
    idle.render(0, reference)
    effect.render(5, ctx)
    idle.render(5, reference)
    ctx.links[1] = replace(ctx.links[1], uplink=True)
    effect.render(5, ctx)
    idle.render(5, reference)
    assert effect.render(40, ctx)[0] == idle.render(40, reference)[0]
    assert effect.render(41, ctx)[0] != BLACK
    ctx.links[1] = replace(ctx.links[1], uplink=False, link_up=False)
    effect.render(41, ctx)
    frame = effect.render(45, ctx)
    expected = effect.colors(45)["idle_color" if effect_id == "activity_glow" else "offline_color"]
    assert frame[0] == expected


@pytest.mark.parametrize("effect_id", ["traffic", "traffic_trails", "activity_glow"])
def test_engine_preserves_uplink_mapping_in_reordered_subset_groups(effect_id):
    engine = Engine(EtherlightSwitch(SimulatedTransport(latency=0)), rules=[], transition=0, gamma=1)
    engine.links = context([1e9] * 8, uplinks=(7,)).links
    engine.traffic = {port: 1e9 for port in range(1, 9)}
    engine.set_zones([{"name": "Devices", "ports": [7, 2, 5], "effect": {"id": effect_id}}])
    engine.compose(t=0)
    colors, _ = engine.compose(t=5)
    idle = create_effect(effect_id, 3)
    index = engine.zone().ports.index(7)
    ctx = context([0 if port == 7 else 1e9 for port in engine.zone().ports])
    idle.render(0, ctx)
    assert colors[6] == idle.render(5, ctx)[index]
    assert colors[1] != BLACK and colors[4] != BLACK
    engine.set_override(7, "#123456")
    assert engine.compose(t=6)[0][6] == Color(0x12, 0x34, 0x56)
    engine.clear_override()
    engine.set_effect("static", {"color": "#abcdef"})
    assert engine.compose(t=7)[0][6] == Color(0xab, 0xcd, 0xef)


@pytest.mark.parametrize("effect_id", ["traffic", "traffic_trails", "activity_glow"])
def test_opted_in_uplink_uses_its_own_rate(effect_id):
    params = {"include_uplinks": True}
    observed = create_effect(effect_id, 2, params)
    reference = create_effect(effect_id, 2, params)
    idle = create_effect(effect_id, 2, params)
    uplink = context([1e9, 1e7], uplinks=(1,))
    device = context([1e9, 1e7])
    quiet = context([0, 1e7], uplinks=(1,))
    for t in (0, 0.25, 1, 3):
        result = observed.render(t, uplink)
        assert result == reference.render(t, device)
        dark = idle.render(t, quiet)
    assert result[0] != dark[0]
    assert observed.params_json()["include_uplinks"] is True


def test_activity_api_and_saved_scene_preserve_speed_colors_and_independent_brightness(config):
    with TestClient(create_app(config, autoconnect=False), base_url="http://127.0.0.1") as client:
        assert client.post("/api/session/login", json={"host": "demo"}).status_code == 200
        values = {"active_color": "#20e090", "idle_color": "#140b22", "brightness_active_color": 65,
                  "brightness_idle_color": 10, "random_idle_color": True, "speed": 2.5, "minimum_mbps": 25,
                  "include_uplinks": True}
        applied = client.put("/api/effect", json={"id": "activity_glow", "params": values})
        assert applied.status_code == 200
        saved = applied.json()["zones"][0]["effect"]
        assert all(saved["params"][key] == value for key, value in values.items())
        assert client.post("/api/presets", json={"name": "Activity"}).status_code == 201
        assert client.put("/api/effect", json={"id": "static"}).status_code == 200
        restored = client.post("/api/presets/Activity/apply")
        assert restored.status_code == 200
        assert restored.json()["zones"][0]["effect"] == saved
