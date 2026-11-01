import copy
import math

import pytest
from fastapi.testclient import TestClient

from etherlighting_evolved.api import create_app
from etherlighting_evolved.color import BLACK, WHITE, Color
from etherlighting_evolved.effects import create_effect
from etherlighting_evolved.effects.base import RenderContext
from etherlighting_evolved.engine import Engine
from etherlighting_evolved.links import PortLink
from etherlighting_evolved.poe import PoePort
from etherlighting_evolved.switch import EtherlightSwitch
from etherlighting_evolved.transport import SimulatedTransport


def usage(count=1, **params):
    return create_effect("poe_usage", count, {
        "low_color": "black", "high_color": "white", "no_power_color": "black", **params,
    })


def power(*watts):
    return RenderContext(len(watts), poe_power_w=dict(enumerate(watts, 1)))


def test_power_scale_is_automatic_logarithmic_and_caps_at_sixty_watts():
    effect = usage(5)
    ctx = power(0, 1, 15, 60, 100)
    assert effect.render(0, ctx) == [BLACK] * 5
    colors = effect.render(20, ctx)
    for color, watts in zip(colors, (0, 1, 15, 60, 100), strict=True):
        expected = 255 * min(1.0, math.log1p(watts) / math.log1p(60))
        assert color.precise_channels == pytest.approx((expected,) * 3, abs=1e-10)
    assert "full_scale" not in effect.params_json()


@pytest.mark.parametrize("watts", [None, 0, -1, True, "12", float("nan"), float("inf")])
def test_invalid_or_missing_power_uses_configurable_no_power_color(watts):
    effect = usage(no_power_color="#123456")
    effect.render(0, power(watts))
    assert effect.render(20, power(watts)) == [Color(0x12, 0x34, 0x56)]


def test_both_power_changes_and_power_loss_fade_without_an_instant_color_cut():
    effect = usage(low_color="blue", no_power_color="red")
    busy, idle = power(60), power(None)
    assert effect.render(0, busy) == [Color(255, 0, 0)]
    peak = effect.render(0.6, busy)[0]
    assert peak.precise_channels == pytest.approx((255, 255 * (1 - math.exp(-1)), 255 * (1 - math.exp(-1))))
    assert effect.render(0.6, idle) == [peak]
    released = effect.render(1.2, idle)[0]
    assert released.r == 255 and 0 < released.g < peak.g
    assert effect.render(30, idle) == [Color(255, 0, 0)]


@pytest.mark.parametrize("fps", [4, 20, 120])
def test_power_fades_depend_on_elapsed_time_and_retain_fractional_channels(fps):
    effect = usage()
    for step in range(fps + 1):
        color = effect.render(step / fps, power(60))[0]
    expected = 255 * (1 - math.exp(-1 / 0.6))
    assert color.precise_channels == pytest.approx((expected,) * 3, abs=1e-10)
    assert color.precise_channels[0] != color.r


def test_repeated_time_and_speed_update_preserve_the_existing_fade():
    effect, reference = usage(), usage()
    ctx = power(60)
    effect.render(0, ctx)
    reference.render(0, ctx)
    effect.update_at({"speed": 4}, 0.25)
    expected = reference.render(0.25, ctx)
    reference.update_at({"speed": 4}, 0.25)
    assert effect.render(0.25, ctx) == expected
    assert effect.render(0.25, ctx) == expected
    assert effect.render(0.5, ctx) == reference.render(0.5, ctx)
    before = copy.deepcopy((effect.p, effect._weights, effect._targets, effect._last))
    with pytest.raises(ValueError):
        effect.update_at({"speed": "invalid"}, 2)
    assert (effect.p, effect._weights, effect._targets, effect._last) == before


def test_per_color_brightness_and_randomization_use_the_shared_palette():
    effect = usage(2, high_color="#40ff80", brightness_high_color=25,
                   no_power_color="#ff8040", brightness_no_power_color=50, random_high_color=True)
    ctx = power(60, 0)
    effect.render(0, ctx)
    colors = effect.render(30, ctx)
    assert colors == [effect.colors(30)["high_color"], effect.colors(30)["no_power_color"]]
    assert colors[1] == Color(128, 64, 32)
    assert colors[0] != Color.parse("#40ff80").scaled(0.25)


def test_zone_mapping_uses_delivered_power_and_does_not_exclude_uplinks_or_down_links():
    engine = Engine(EtherlightSwitch(SimulatedTransport(latency=0)), rules=[], transition=0, gamma=1)
    engine.poe = {
        7: PoePort(7, True, True, power_w=60),
        2: PoePort(2, True, False, power_w=60),
        5: PoePort(5, True, True, power_w=15),
    }
    engine.links = {7: PortLink(7, True, True, 10000, True, uplink=True),
                    5: PortLink(5, True, False, 0, False)}
    engine.set_zones([{"name": "Power", "ports": [7, 2, 5, 3], "effect": {
        "id": "poe_usage", "params": {"low_color": "black", "high_color": "white", "no_power_color": "red"},
    }}])
    engine.compose(t=0)
    colors = engine.compose(t=30)[0]
    assert colors[6] == WHITE
    assert colors[1] == Color(255, 0, 0)
    assert colors[2] == Color(255, 0, 0)
    expected = 255 * math.log1p(15) / math.log1p(60)
    assert colors[4].precise_channels == pytest.approx((expected,) * 3)
    engine.poe = {}
    assert engine.compose(t=30)[0] == colors
    assert all(engine.compose(t=60)[0][port - 1] == Color(255, 0, 0) for port in (7, 2, 5, 3))


def test_power_api_and_preset_preserve_all_custom_colors_and_fade_controls(config):
    with TestClient(create_app(config, autoconnect=False), base_url="http://127.0.0.1") as client:
        assert client.post("/api/session/login", json={"host": "demo"}).status_code == 200
        params = {"low_color": "#101020", "high_color": "#ef78c7", "no_power_color": "#020304",
                  "brightness_high_color": 61, "brightness_no_power_color": 20,
                  "random_low_color": True, "speed": 2.5}
        applied = client.put("/api/effect", json={"id": "poe_usage", "params": params})
        assert applied.status_code == 200
        saved = applied.json()["zones"][0]["effect"]
        assert all(saved["params"][key] == value for key, value in params.items())
        assert client.post("/api/presets", json={"name": "Power"}).status_code == 201
        assert client.put("/api/effect", json={"id": "static"}).status_code == 200
        restored = client.post("/api/presets/Power/apply")
        assert restored.status_code == 200
        assert restored.json()["zones"][0]["effect"] == saved
