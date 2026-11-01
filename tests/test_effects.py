from datetime import datetime

import pytest

from etherlighting_evolved.color import Color
from etherlighting_evolved.effects import EFFECTS, create_effect, list_effects
from etherlighting_evolved.effects.base import RenderContext
from etherlighting_evolved.links import PortLink


def test_effect_catalog_includes_ambient_motion_and_data_effects():
    assert len(EFFECTS) == 33
    assert {"traffic", "traffic_trails", "activity_glow", "aurora", "lava_lamp", "plasma"} <= EFFECTS.keys()


@pytest.mark.parametrize("effect_id", sorted(EFFECTS))
@pytest.mark.parametrize("num_ports", [1, 3, 8])
def test_every_effect_renders_one_color_per_port(effect_id, num_ports):
    effect = create_effect(effect_id, num_ports, seed=1)
    links = {p: PortLink(p, True, True, 10000, True, rx_bytes=p * 1000) for p in range(1, num_ports + 1)}
    ctx = RenderContext(num_ports=num_ports, links=links, traffic_bps={p: p * 1e8 for p in links},
                        now=datetime(2026, 6, 1, 21, 30))
    for t in (0.0, 0.05, 0.5, 1.7, 33.3, 1000.0):
        frame = effect.render(t, ctx)
        assert len(frame) == num_ports
        assert all(isinstance(c, Color) for c in frame)


def test_descriptions_are_complete():
    for effect in list_effects():
        assert effect["name"] and effect["description"]
        assert effect["category"] in ("static", "animated", "time", "data")
        for param in effect["params"]:
            assert {"key", "label", "type", "default"} <= set(param)


def test_params_are_validated_and_clamped():
    effect = create_effect("breathing", 8, {"period": 999, "color": "red"})
    assert effect.p["period"] == 30.0
    assert effect.params_json()["color"] == "#ff0000"
    effect.update({"period": 0})
    assert effect.p["period"] == 1.0
    assert create_effect("breathing", 8).p["period"] == 5.0
    with pytest.raises(ValueError):
        effect.update({"nonsense": 1})
    with pytest.raises(ValueError):
        create_effect("police", 8, {"pattern": "disco"})


def test_unknown_effect():
    with pytest.raises(ValueError, match="unknown effect"):
        create_effect("unknown_effect", 8)


def test_static_is_exactly_the_color():
    effect = create_effect("static", 8, {"color": "#123456"})
    assert {c.to_hex() for c in effect.render(5, RenderContext(8))} == {"#123456"}


# Effects meant to flow. The flashing ones (police, strobe, lightning) jump on purpose.
SMOOTH = ["breathing", "color_cycle", "rainbow", "comet", "scanner", "marquee", "starry_night", "fire", "gradient",
          "ripple", "daylight", "traffic"]


@pytest.mark.parametrize("effect_id", SMOOTH)
def test_moving_light_blends_between_ports_at_the_switchs_update_rate(effect_id):
    """At 4 updates per second (8 ports within the default command budget), no port may jump by more than about
    a third of full brightness between updates; bigger steps read as flicker instead of motion."""
    effect = create_effect(effect_id, 8, seed=3)
    ctx = RenderContext(8, links={p: PortLink(p, True, True, 10000, True) for p in range(1, 9)},
                        now=datetime(2026, 6, 1, 21, 30))
    brightness = [None]
    worst = 0.0
    for k in range(4 * 40):
        frame = [max(c.r, c.g, c.b) / 255 for c in effect.render(k / 4, ctx)]
        if brightness[0] is not None:
            worst = max(worst, max(abs(a - b) for a, b in zip(frame, brightness[0], strict=True)))
        brightness[0] = frame
    assert worst <= 0.35
