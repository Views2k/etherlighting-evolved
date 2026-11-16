import copy
import json
from datetime import datetime
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from etherlighting_evolved.api import create_app
from etherlighting_evolved.color import BLACK, Color
from etherlighting_evolved.effects import EFFECTS, create_effect, list_effects
from etherlighting_evolved.effects.base import Effect, Param, RenderContext
from etherlighting_evolved.engine import Engine, to_output
from etherlighting_evolved.links import PortLink
from etherlighting_evolved.switch import EtherlightSwitch
from etherlighting_evolved.transport import SimulatedTransport

# pre_color_brightness.json records earlier engine output on purpose. It has no generator and must not be regenerated.
BASELINE = json.loads((Path(__file__).parent / "fixtures/pre_color_brightness.json").read_text("utf-8"))
COLOR_KEYS = [(effect.id, param.key) for effect in EFFECTS.values() for param in effect.params if param.type == "color"]


def context(count=8, *, online=True, traffic=0, poe_power=None):
    return RenderContext(count, now=datetime(2026, 10, 30, 12),
                         links={port: PortLink(port, True, online, 1000, True) for port in range(1, count + 1)},
                         traffic_bps={port: traffic * port for port in range(1, count + 1)},
                         poe_power_w={port: poe_power for port in range(1, count + 1)} if poe_power else {})


@pytest.mark.parametrize("case", BASELINE["cases"], ids=lambda case: case["id"] + str(case["params"]))
def test_default_and_explicit_full_brightness_match_prechange_output(case):
    spec = EFFECTS[case["id"]]
    full = {f"brightness_{param.key}": 100 for param in spec.params if param.type == "color"}
    for additions in ({}, full):
        effect = create_effect(case["id"], BASELINE["ports"], case["params"] | additions, seed=7)
        frames = [[color.to_hex() for color in effect.render(t, context(traffic=1e8))] for t in BASELINE["times"]]
        assert frames == case["frames"]


def test_each_color_advertises_one_brightness_companion():
    for effect in list_effects():
        params = {param["key"]: param for param in effect["params"]}
        assert len(params) == len(effect["params"])
        for param in params.values():
            if param["type"] == "color":
                brightness = params[param["brightness_param"]]
                assert brightness["dims"] == param["key"]
                assert brightness["type"] == "float"
                assert (brightness["default"], brightness["min"], brightness["max"], brightness["unit"]) == (
                    100, 0, 100, "%")


@pytest.mark.parametrize("effect_id,key", COLOR_KEYS)
@pytest.mark.parametrize("randomized", [False, True])
def test_each_source_dims_independently_and_preserves_its_chosen_color(effect_id, key, randomized):
    effect = create_effect(effect_id, 8, {key: "#c8a064", f"random_{key}": randomized}, seed=7)
    before = {time: effect.colors(time) for time in (0, 2.7, 9)}
    saved = copy.deepcopy(effect.params_json())
    effect.update({f"brightness_{key}": 25})
    for time in (0, 2.7, 9):
        actual = effect.colors(time)
        assert actual[key] == before[time][key].scaled(0.25)
        assert {k: c for k, c in actual.items() if k != key} == {
            k: c for k, c in before[time].items() if k != key}
    effect.update({f"brightness_{key}": 0})
    assert effect.colors(9)[key] == BLACK
    assert effect.params_json()[key] == saved[key]
    assert effect.p[f"random_{key}"] == randomized
    effect.update({f"brightness_{key}": 100})
    assert effect.colors(9) == before[9]


@pytest.mark.parametrize("effect_id,key", COLOR_KEYS)
def test_every_color_brightness_reaches_rendered_output(effect_id, key):
    colors = {param.key: "#c8a064" for param in EFFECTS[effect_id].params if param.type == "color"}
    contexts = [context(), context(traffic=1e9), context(online=False), context(poe_power=45)]
    if effect_id == "traffic" and key == "uplink_color":
        colors["custom_uplink"] = True
        uplink = context()
        uplink.links[1] = PortLink(1, True, True, 1000, True, uplink=True)
        contexts.append(uplink)
    changed = False
    for ctx in contexts:
        reference = create_effect(effect_id, 8, colors, seed=7)
        dimmed = create_effect(effect_id, 8, colors | {f"brightness_{key}": 25}, seed=7)
        for t in (0, 0.4, 1, 2, 3, 7, 15, 30):
            changed |= reference.render(t, ctx) != dimmed.render(t, ctx)
    assert changed


@pytest.mark.parametrize("effect_id,flag", [("starry_night", "rainbow"), ("strobe", "random_colors")])
def test_legacy_generated_primary_colors_obey_brightness(effect_id, flag):
    reference = create_effect(effect_id, 8, {flag: True}, seed=7)
    dimmed = create_effect(effect_id, 8, {flag: True, "brightness_color": 25}, seed=7)
    muted = create_effect(effect_id, 8, {flag: True, "brightness_color": 0}, seed=7)
    changed = False
    for time in (0, 0.3, 0.9, 1.5, 2.6, 4.9, 9.2):
        full = reference.render(time, context())
        low = dimmed.render(time, context())
        off = muted.render(time, context())
        changed |= full != low
        assert all(color == BLACK for color in off)
        assert all(a <= b for left, right in zip(low, full, strict=True)
                   for a, b in zip((left.r, left.g, left.b), (right.r, right.g, right.b), strict=True))
    assert changed


@pytest.mark.parametrize("value,expected", [(-4, 0), (120, 100), (12.5, 12.5)])
def test_brightness_numeric_range_is_coerced(value, expected):
    effect = create_effect("marquee", 8, {"brightness_background": value})
    assert effect.params_json()["brightness_background"] == expected


@pytest.mark.parametrize("value", [float("nan"), float("inf"), "invalid", None])
def test_invalid_brightness_update_is_atomic(value):
    effect = create_effect("marquee", 8)
    before = effect.params_json()
    with pytest.raises(ValueError):
        effect.update({"color": "red", "brightness_color": value})
    assert effect.params_json() == before


def test_source_brightness_companions_are_reserved():
    class Collision(Effect):
        id = "fixture"
        params = (Param("color", "Color", "color", "#ffffff"),
                  Param("brightness_color", "Existing", "float", 1))

    with pytest.raises(ValueError, match="reserved"):
        Collision.parameters()


def test_old_instances_without_brightness_values_keep_existing_output():
    effect = create_effect("marquee", 8)
    before = effect.render(1.7, context())
    for key in tuple(effect.p):
        if key.startswith("brightness_"):
            del effect.p[key]
    assert effect.render(1.7, context()) == before


def test_source_brightness_composes_with_master_brightness_and_gamma_once():
    engine = Engine(EtherlightSwitch(SimulatedTransport(latency=0)), rules=[], transition=0)
    engine.set_effect("gradient", {"color_a": "#c8a064", "color_b": "#4080c0", "brightness_color_a": 25,
                                   "brightness_color_b": 75})
    engine.set_brightness(40)
    colors, output = engine.compose(t=0)
    assert colors[0] == Color(50, 40, 25)
    assert colors[-1] == Color(48, 96, 144)
    assert output[0] == to_output(Color(50, 40, 25), 40)
    assert output[-1] == to_output(Color(48, 96, 144), 40)


def test_scene_and_api_round_trip_keep_independent_brightness_and_saved_hex(config):
    with TestClient(create_app(config, autoconnect=False), base_url="http://127.0.0.1") as client:
        assert client.post("/api/session/login", json={"host": "demo"}).status_code == 200
        values = {"color": "#c8a064", "background": "#204060", "random_background": True,
                  "brightness_color": 25, "brightness_background": 75}
        state = client.put("/api/effect", json={"id": "marquee", "params": values}).json()
        zone = state["zones"][0]["id"]
        response = client.patch(f"/api/zones/{zone}/params", json={"params": {"brightness_color": 0}})
        assert response.status_code == 200
        params = response.json()["zones"][0]["effect"]["params"]
        assert params["color"] == "#c8a064" and params["brightness_color"] == 0
        assert params["random_background"] and params["brightness_background"] == 75
        assert client.post("/api/presets", json={"name": "Color intensity"}).status_code == 201
        assert client.put("/api/effect", json={"id": "static"}).status_code == 200
        restored = client.post("/api/presets/Color%20intensity/apply")
        assert restored.status_code == 200
        assert restored.json()["zones"][0]["effect"]["params"] == params
