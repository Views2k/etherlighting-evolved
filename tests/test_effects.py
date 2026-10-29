from datetime import datetime

import pytest

from etherlighting_evolved.color import Color
from etherlighting_evolved.effects import EFFECTS, create_effect, list_effects
from etherlighting_evolved.effects.base import RenderContext
from etherlighting_evolved.links import PortLink


def test_there_are_fifteen_lighting_effects_plus_traffic():
    assert len(EFFECTS) == 16
    assert "traffic" in EFFECTS


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
    assert effect.p["period"] == 20.0
    assert effect.params_json()["color"] == "#ff0000"
    with pytest.raises(ValueError):
        effect.update({"nonsense": 1})
    with pytest.raises(ValueError):
        create_effect("police", 8, {"pattern": "disco"})


def test_unknown_effect():
    with pytest.raises(ValueError, match="unknown effect"):
        create_effect("lava_lamp", 8)


def test_static_is_exactly_the_color():
    effect = create_effect("static", 8, {"color": "#123456"})
    assert {c.to_hex() for c in effect.render(5, RenderContext(8))} == {"#123456"}
