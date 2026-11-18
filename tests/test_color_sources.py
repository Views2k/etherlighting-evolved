import copy
import json
from datetime import datetime
from pathlib import Path

import pytest

from etherlighting_evolved import timing
from etherlighting_evolved.color import Color
from etherlighting_evolved.effects import EFFECTS, create_effect, list_effects
from etherlighting_evolved.effects.base import RenderContext
from etherlighting_evolved.engine import Engine, to_output
from etherlighting_evolved.links import PortLink
from etherlighting_evolved.switch import EtherlightSwitch
from etherlighting_evolved.transport import SimulatedTransport

# fixed_effects.json records earlier engine output on purpose. It has no generator and must not be regenerated.
BASELINE = json.loads((Path(__file__).parent / "fixtures" / "fixed_effects.json").read_text("utf-8"))


def delta(left, right):
    return max(abs(a - b) for a, b in zip((left.r, left.g, left.b), (right.r, right.g, right.b), strict=True))


@pytest.mark.parametrize("case", BASELINE["cases"], ids=lambda case: f'{case["id"]}-{case["ports"]}')
def test_fixed_and_legacy_output_matches_pre_color_source_frames_exactly(case):
    count = case["ports"]
    effect = create_effect(case["id"], count, case["params"], seed=7)
    context = RenderContext(count, now=datetime(2026, 1, 1, 12),
                            links={i: PortLink(i, True, True, 1000, True) for i in range(1, count + 1)},
                            traffic_bps={i: i * 1e8 for i in range(1, count + 1)})
    actual = [[color.to_hex() for color in effect.render(t, context)] for t in BASELINE["times"]]
    assert actual == case["frames"]


def test_catalog_exposes_one_independent_companion_for_every_explicit_color():
    for description in list_effects():
        params = {p["key"]: p for p in description["params"]}
        colors = [p for p in params.values() if p["type"] == "color"]
        for color in colors:
            companion = params[color["random_param"]]
            assert companion["randomizes"] == color["key"]
            assert companion["type"] == "bool" and companion["default"] is False
        if colors:
            assert params["color_transition"]["default"] == 8
            assert params["color_transition"]["min"] == 1
            assert params["color_transition"]["max"] == 30
        else:
            assert "color_transition" not in params


@pytest.mark.parametrize("random_primary,random_background", [(False, True), (True, False), (True, True)])
def test_marquee_randomizes_primary_and_background_independently(random_primary, random_background):
    params = {"color": "#ffffff", "background": "#000000", "speed": 1, "spacing": 4,
              "random_color": random_primary, "random_background": random_background}
    effect = create_effect("marquee", 8, params)
    first, later = effect.render(0, RenderContext(8)), effect.render(4, RenderContext(8))
    assert (first[0] != later[0]) is random_primary
    assert (first[2] != later[2]) is random_background
    if not random_primary:
        assert first[0] == later[0] == Color(255, 255, 255)
    if not random_background:
        assert first[2] == later[2] == Color(0, 0, 0)
    assert effect.params_json()["color"] == "#ffffff"
    assert effect.params_json()["background"] == "#000000"


@pytest.mark.parametrize("effect_id", list(EFFECTS))
def test_color_resolution_preserves_saved_values_rng_and_motion_phase(effect_id):
    effect = create_effect(effect_id, 8, seed=7)
    flags = {f"random_{p.key}": True for p in effect.params if p.type == "color"}
    effect.update(flags)
    saved = copy.deepcopy(effect.params_json())
    rng = effect.rng.getstate()
    phase = effect._phase_offset, effect._phase_rate
    for time in (0, 8, 3, 4, 100, -1):
        effect.colors(time)
    assert effect.params_json() == saved
    assert effect.rng.getstate() == rng
    assert (effect._phase_offset, effect._phase_rate) == phase


def test_random_palette_is_deterministic_independent_of_render_order_and_rng_seed():
    params = {"random_color": True, "random_background": True}
    first = create_effect("marquee", 8, params, seed=1)
    second = create_effect("marquee", 8, params, seed=99)
    samples = {time: first.colors(time) for time in (0, 3, 9, 100, -1)}
    for time in (100, -1, 0, 9, 3):
        assert second.colors(time) == samples[time]
    assert len({first.colors(time)["background"] for time in range(0, 160, 8)}) >= 15


@pytest.mark.parametrize("period", [1, 8, 30])
@pytest.mark.parametrize("key", ["color", "background"])
def test_random_color_has_continuous_palette_boundaries(period, key):
    effect = create_effect("marquee", 8, {f"random_{key}": True, "color_transition": period})
    for index in range(-2, 30):
        boundary = index * period
        assert delta(effect.colors(boundary - 0.00001)[key], effect.colors(boundary + 0.00001)[key]) <= 1


def test_default_random_palette_has_small_encoded_steps_at_twenty_five_samples_per_second():
    effect = create_effect("static", 1, {"random_color": True})
    previous = None
    for tick in range(25 * 80):
        output = to_output(effect.render(tick / 25, RenderContext(1))[0], 100)
        if previous is not None:
            assert delta(previous.color, output.color) <= 9
        previous = output


def test_transition_duration_update_preserves_color_and_movement_position():
    effect = create_effect("marquee", 8, {"random_background": True})
    before = effect.render(18.7, RenderContext(8))
    phase = effect._phase_offset, effect._phase_rate
    effect.update_at({"color_transition": 2}, 18.7)
    assert effect.render(18.7, RenderContext(8)) == before
    assert (effect._phase_offset, effect._phase_rate) == phase
    assert effect.render(19, RenderContext(8)) != before


@pytest.mark.parametrize("effect_id,legacy", [("starry_night", "rainbow"), ("strobe", "random_colors")])
def test_legacy_and_generic_primary_modes_are_atomic_and_mutually_exclusive(effect_id, legacy):
    effect = create_effect(effect_id, 8, {legacy: True})
    assert effect.p[legacy] and not effect.p["random_color"]
    effect.update({"random_color": True})
    assert effect.p["random_color"] and not effect.p[legacy]
    effect.update({legacy: True})
    assert effect.p[legacy] and not effect.p["random_color"]
    saved = effect.params_json()
    with pytest.raises(ValueError, match="cannot both"):
        effect.update({legacy: True, "random_color": True, "color": "red"})
    assert effect.params_json() == saved
    assert ["random_color", legacy] in effect.describe()["exclusive_params"]


def test_background_randomization_can_coexist_with_per_sparkle_colors():
    effect = create_effect("starry_night", 8, {"rainbow": True, "random_background": True})
    assert effect.p["rainbow"] and effect.p["random_background"] and not effect.p["random_color"]


@pytest.mark.parametrize("period", [float("nan"), float("inf"), "invalid"])
def test_invalid_color_transition_leaves_all_parameters_unchanged(period):
    effect = create_effect("marquee", 8)
    before = effect.params_json()
    with pytest.raises(ValueError):
        effect.update({"color_transition": period, "random_background": True})
    assert effect.params_json() == before


def test_old_instances_without_new_defaults_or_phase_fields_remain_usable():
    effect = create_effect("marquee", 8)
    for key in ("random_color", "random_background", "color_transition"):
        del effect.p[key]
    del effect._color_phase_offset, effect._color_phase_rate
    before = effect.render(3, RenderContext(8))
    reference = create_effect("marquee", 8).render(3, RenderContext(8))
    assert before == reference
    effect.update_at({"random_background": True}, 3)
    assert effect.colors(3)["background"] != Color(0, 0, 0)


def test_scene_round_trip_retains_independent_sources_and_fixed_colors():
    switch = EtherlightSwitch(SimulatedTransport(latency=0))
    engine = Engine(switch, rules=[], transition=0)
    engine.set_effect("marquee", {"color": "white", "background": "#123456", "random_background": True,
                                  "color_transition": 12})
    snapshot = json.loads(json.dumps(engine.snapshot()))
    restored = Engine(switch, rules=[], transition=0)
    restored.apply_snapshot(snapshot)
    assert restored.snapshot() == snapshot
    assert restored.compose(t=3)[0] == engine.compose(t=3)[0]


def test_toggling_a_color_source_uses_the_existing_engine_crossfade(monkeypatch):
    clock = [100.0]
    monkeypatch.setattr(timing, "monotonic", lambda: clock[0])
    engine = Engine(EtherlightSwitch(SimulatedTransport(latency=0)), effect="static",
                    effect_params={"color": "white"}, rules=[], transition=1)
    before = engine.compose()[0]
    engine.update_params({"random_color": True})
    assert engine.compose()[0] == before
    clock[0] += 0.5
    halfway = engine.compose()[0]
    clock[0] += 0.5
    end = engine.compose()[0]
    assert before != halfway != end
    assert engine.zone().effect.params_json()["color"] == "#ffffff"
