import math

import pytest

from etherlighting_evolved.color import Color
from etherlighting_evolved.effects import create_effect
from etherlighting_evolved.effects.base import RenderContext
from etherlighting_evolved.effects.library import TrafficMeter
from etherlighting_evolved.engine import Engine
from etherlighting_evolved.links import PortLink
from etherlighting_evolved.switch import EtherlightSwitch
from etherlighting_evolved.transport import SimulatedTransport


def test_automatic_capacity_per_port_with_existing_color_response():
    effect = create_effect("traffic", 3, {"idle_color": "black", "busy_color": "white",
                                          "scale": "linear", "smoothing": 0})
    context = RenderContext(3, links={i: PortLink(i, True, True, speed, True)
                                     for i, speed in enumerate((100, 1000, 10000), 1)},
                            traffic_bps={i: 1e3 * math.sqrt(speed * 1e3)
                                         for i, speed in enumerate((100, 1000, 10000), 1)})
    assert effect.render(0, context) == [Color(128, 128, 128)] * 3
    context.links[2] = PortLink(2, True, True, 10000, True)
    assert effect.render(1, context)[1] == Color(109, 109, 109)


def test_existing_logarithmic_response_and_unknown_capacity_fallback():
    effect = create_effect("traffic", 2, {"smoothing": 0})
    context = RenderContext(2, links={i: PortLink(i, True, True, speed, True)
                                     for i, speed in enumerate((0, 1000), 1)},
                            traffic_bps={1: 1e6, 2: 1e6})
    expected = Color.parse("#0a3cff").lerp(Color.parse("#ff3b30"), 0.5)
    assert effect.render(0, context) == [expected, expected]


def test_saved_traffic_scene_migrates_without_restoring_manual_scale():
    effect = create_effect("traffic", 1, {"max_mbps": 5000, "busy_color": "#123456"})
    assert effect.name == "Live Traffic"
    assert effect.params_json()["busy_color"] == "#123456"
    assert "max_mbps" not in effect.params_json()
    assert "max_mbps" not in TrafficMeter.param_map()
    effect.update({"max_mbps": 1000, "scale": "linear"})
    assert "scale" not in effect.p
    assert "scale" not in TrafficMeter.param_map()
    assert "max_mbps" not in effect.p
    with pytest.raises(ValueError):
        effect.update({"max_mbps": float("nan")})


@pytest.mark.parametrize("scale", ["linear", "log"])
def test_legacy_scale_uses_logarithmic_output_and_is_retired(scale):
    effect = create_effect("traffic", 1, {"scale": scale, "smoothing": 0})
    current = create_effect("traffic", 1, {"smoothing": 0})
    context = RenderContext(1, links={1: PortLink(1, True, True, 1000, True)}, traffic_bps={1: 1e6})
    assert effect.render(0, context) == current.render(0, context)
    assert "scale" not in effect.params_json()


@pytest.mark.parametrize("scale", ["LOG", "invalid", None, 0])
def test_invalid_legacy_scale_still_rejected(scale):
    with pytest.raises(ValueError):
        create_effect("traffic", 1, {"scale": scale})


@pytest.mark.parametrize("brightness", [0, 25, 100])
def test_custom_uplink_uses_its_color_and_brightness_without_changing_device_traffic(brightness):
    settings = {"custom_uplink": True, "uplink_color": "#80c0ff",
                "brightness_uplink_color": brightness, "smoothing": 0}
    effect = create_effect("traffic", 3, settings)
    ordinary = create_effect("traffic", 3, {"smoothing": 0})
    context = RenderContext(3, links={
        1: PortLink(1, True, True, 1000, True),
        2: PortLink(2, True, True, 10000, True, uplink=True),
        3: PortLink(3, True, False, 0, True, uplink=True),
    }, traffic_bps={1: 1e6, 2: 10e9})
    for time in (0, 0.5, 3):
        output = effect.render(time, context)
        expected = ordinary.render(time, context)
        assert output[0] == expected[0]
        assert output[1] == Color.parse("#80c0ff").scaled(brightness / 100)
        assert output[2] == expected[2]


def test_custom_uplink_can_be_disabled_and_follows_detected_role_changes():
    effect = create_effect("traffic", 1, {"custom_uplink": True, "uplink_color": "white", "smoothing": 0})
    context = RenderContext(1, links={1: PortLink(1, True, True, 1000, True, uplink=True)}, traffic_bps={1: 1e9})
    assert effect.render(0, context) == [Color.parse("white")]
    effect.update({"custom_uplink": False})
    assert effect.render(1, context) == [Color.parse("#0a3cff")]
    context.links[1] = PortLink(1, True, True, 1000, True, uplink=False)
    assert effect.render(2, context) == [Color.parse("#ff3b30")]


def test_uplink_settings_survive_scene_round_trip_with_default_brightness():
    engine = Engine(EtherlightSwitch(SimulatedTransport()), rules=[], brightness=60,
                    effect="traffic", effect_params={"custom_uplink": True, "uplink_color": "#80c0ff",
                                                     "brightness_uplink_color": 25})
    restored = Engine(EtherlightSwitch(SimulatedTransport()), rules=[])
    restored.apply_snapshot(engine.snapshot())
    assert restored.snapshot() == engine.snapshot()
    assert restored.brightness == 60
    assert restored.zones[0].effect.params_json()["brightness_uplink_color"] == 25


def test_legacy_traffic_profiles_keep_idle_uplink_appearance():
    effect = create_effect("traffic", 1, {"idle_color": "#123456", "brightness_idle_color": 45})
    context = RenderContext(1, links={1: PortLink(1, True, True, 1000, True, uplink=True)}, traffic_bps={1: 1e9})
    assert effect.p["custom_uplink"] is False
    assert effect.render(0, context) == [Color.parse("#123456").scaled(0.45)]
