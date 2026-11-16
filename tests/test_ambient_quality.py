import json

import pytest

from etherlighting_evolved import timing
from etherlighting_evolved.color import Color
from etherlighting_evolved.effects import EFFECTS, create_effect
from etherlighting_evolved.effects.base import RenderContext
from etherlighting_evolved.engine import Engine
from etherlighting_evolved.links import PortLink
from etherlighting_evolved.switch import EtherlightSwitch
from etherlighting_evolved.transport import SimulatedTransport

NEW_EFFECTS = (
    "aurora", "lava_lamp", "plasma", "ocean_swell", "eclipse", "meteor_shower", "counterflow",
    "color_dissolve", "ink_spread", "alternating_fade", "color_tide", "palette_morph", "ember_bed",
    "fireflies", "traffic_trails",
)


def context(ports, traffic=0):
    return RenderContext(ports, links={p: PortLink(p, True, True, 1000, True) for p in range(1, ports + 1)},
                         traffic_bps={p: traffic for p in range(1, ports + 1)})


def channel_delta(before, after):
    return max(abs(a - b) for left, right in zip(before, after, strict=True)
               for a, b in zip((left.r, left.g, left.b), (right.r, right.g, right.b), strict=True))


def test_new_effect_inventory_has_unique_names_and_working_speed_controls():
    assert set(NEW_EFFECTS) <= set(EFFECTS)
    names = [EFFECTS[key].name for key in NEW_EFFECTS]
    assert len(set(names)) == len(NEW_EFFECTS)
    for key in NEW_EFFECTS:
        parameters = EFFECTS[key].param_map()
        speed = parameters["speed"]
        assert speed.min < speed.default < speed.max
        assert speed.min >= 0 and speed.step > 0


@pytest.mark.parametrize("effect_id", NEW_EFFECTS)
@pytest.mark.parametrize("ports", [1, 2, 8, 16, 64])
def test_defaults_render_the_whole_group_without_unintended_black_frames(effect_id, ports):
    effect = create_effect(effect_id, ports, seed=7)
    ctx = context(ports)
    for tick in range(121):
        frame = effect.render(tick / 4, ctx)
        assert len(frame) == ports
        assert all(isinstance(color, Color) for color in frame)
        assert any(color.max_channel > 0 for color in frame), (effect_id, ports, tick)


@pytest.mark.parametrize("effect_id", NEW_EFFECTS)
def test_default_animation_changes_every_port_over_time(effect_id):
    effect = create_effect(effect_id, 8, seed=7)
    ctx = context(8)
    samples = [effect.render(tick / 25, ctx) for tick in range(1501)]
    for port in range(8):
        colors = [frame[port] for frame in samples]
        spread = max(max(getattr(c, channel) for c in colors) - min(getattr(c, channel) for c in colors)
                     for channel in ("r", "g", "b"))
        assert spread >= 12, (effect_id, port + 1, spread)
        assert len(set(colors)) >= 12, (effect_id, port + 1)


@pytest.mark.parametrize("effect_id", NEW_EFFECTS)
@pytest.mark.parametrize("ports", [1, 2, 8, 16])
def test_phase_boundaries_do_not_cut_between_frames(effect_id, ports):
    effect = create_effect(effect_id, ports, seed=7)
    ctx = context(ports)
    rate = effect.p["speed"]
    for phase in [0, 0.25, 0.5, 0.75, 1, 2, 3, 5, 8, 13, 21, 32]:
        time = phase / rate
        before = effect.render(max(0, time - 0.00001), ctx)
        after = effect.render(time + 0.00001, ctx)
        assert channel_delta(before, after) <= 1, (effect_id, ports, phase)


@pytest.mark.parametrize("effect_id", NEW_EFFECTS)
def test_speed_edits_preserve_position_and_change_subsequent_motion(effect_id):
    effect = create_effect(effect_id, 8, seed=7)
    ctx = context(8)
    time = 13.271
    before = effect.render(time, ctx)
    new_speed = effect.param_map()["speed"].max
    effect.update_at({"speed": new_speed}, time)
    assert channel_delta(before, effect.render(time, ctx)) <= 1
    control = create_effect(effect_id, 8, seed=7)
    control.render(time, ctx)
    assert any(effect.render(time + offset, ctx) != control.render(time + offset, ctx)
               for offset in (0.17, 0.41, 0.83))


@pytest.mark.parametrize("effect_id", NEW_EFFECTS)
def test_all_explicit_color_choices_reach_rendered_output(effect_id):
    effect_type = EFFECTS[effect_id]
    colors = {p.key: "#0000ff" for p in effect_type.params if p.type == "color"}
    assert colors
    effect = create_effect(effect_id, 8, colors, seed=7)
    ctx = context(8, traffic=500e6)
    for time in (0, 0.37, 1.13, 7.2, 19.9):
        frame = effect.render(time, ctx)
        assert all(c.r == 0 and c.g == 0 for c in frame), (effect_id, time)
        assert any(c.b > 0 for c in frame), (effect_id, time)


@pytest.mark.parametrize("effect_id", NEW_EFFECTS)
def test_new_effect_scene_round_trip_preserves_colors_randomization_and_speed(effect_id):
    effect_type = EFFECTS[effect_id]
    first_color = next(p.key for p in effect_type.params if p.type == "color")
    params = {first_color: "#124578", f"random_{first_color}": True, "color_transition": 5.5,
              "speed": effect_type.param_map()["speed"].max}
    engine = Engine(EtherlightSwitch(SimulatedTransport(latency=0)), rules=[], transition=0, brightness=63)
    engine.set_effect(effect_id, params)
    snapshot = json.loads(json.dumps(engine.snapshot()))
    restored = Engine(EtherlightSwitch(SimulatedTransport(latency=0)), rules=[], transition=0)
    restored.apply_snapshot(snapshot)
    assert restored.snapshot() == snapshot
    assert restored.zone().effect.params_json()[first_color] == "#124578"
    assert restored.zone().effect.params_json()[f"random_{first_color}"] is True
    _, lit = restored.compose(t=1.37)
    restored.set_brightness(0)
    _, dark = restored.compose(t=1.37)
    assert any(output.color.max_channel > 0 for output in lit)
    assert all(output.color.is_off and output.level == 100 for output in dark)


@pytest.mark.parametrize("params", [{"speed": 1.1}, {"color_transition": 3.0}])
def test_data_effect_edits_use_its_render_clock_when_transport_slows_animation(monkeypatch, params):
    clock = [100.0]
    monkeypatch.setattr(timing, "monotonic", lambda: clock[0])
    engine = Engine(EtherlightSwitch(SimulatedTransport(latency=0)), rules=[], transition=0,
                    max_commands=15, effect="traffic_trails", effect_params={"random_idle_color": True})
    clock[0] += 13.271
    before = engine.compose()[0]
    assert engine.budget.animation_time - engine.zone().started_at < clock[0] - engine.zone().wall_started_at
    engine.update_params(params)
    assert channel_delta(before, engine.compose()[0]) <= 1


@pytest.mark.parametrize("bound", ["min", "max"])
@pytest.mark.parametrize("effect_id", NEW_EFFECTS)
def test_parameter_extremes_stay_continuous_and_bounded(effect_id, bound):
    params = {p.key: getattr(p, bound) for p in EFFECTS[effect_id].params
              if p.type in {"int", "float"} and getattr(p, bound) is not None}
    effect = create_effect(effect_id, 2, params, seed=7)
    ctx = context(2)
    for phase in (0.5, 1, 2, 8, 32):
        time = phase / effect.p["speed"]
        before = effect.render(time - 0.000001, ctx)
        after = effect.render(time + 0.000001, ctx)
        assert len(after) == 2 and channel_delta(before, after) <= 1, (effect_id, bound, phase)


def test_traffic_trails_smoothing_has_the_same_time_response_at_different_sample_rates():
    outputs = []
    for fps in (4, 25):
        effect = create_effect("traffic_trails", 8)
        busy = context(8, traffic=1e9)
        idle = context(8)
        for step in range(fps * 2 + 1):
            frame = effect.render(step / fps, busy)
        busy_output = frame
        for step in range(fps * 3 + 1):
            frame = effect.render(2 + step / fps, idle)
        outputs.append((busy_output, frame))
    assert channel_delta(outputs[0][0], outputs[1][0]) <= 1
    assert channel_delta(outputs[0][1], outputs[1][1]) <= 1
    assert outputs[0][0] != outputs[0][1]


def test_traffic_trails_link_changes_and_interrupted_fades_do_not_cut():
    effect = create_effect("traffic_trails", 8, {"offline_color": "#000000"})
    online, offline = context(8), context(8)
    offline.links[1] = PortLink(1, True, False, 0, False)
    effect.render(0, online)
    before = effect.render(2, online)
    assert effect.render(2, offline) == before
    halfway = effect.render(2.3, offline)
    assert halfway[0] != before[0] and not halfway[0].is_off
    assert effect.render(2.3, online) == halfway
    restored = effect.render(3, online)
    assert not restored[0].is_off
    assert effect.render(3, offline) == restored
    assert effect.render(3.7, offline)[0].is_off
    assert effect.render(3.7, online)[0].is_off
    assert not effect.render(4.4, online)[0].is_off
