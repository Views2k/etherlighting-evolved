import pytest
from test_native_engine import NativeSwitch

from etherlighting_evolved import timing
from etherlighting_evolved.color import Color
from etherlighting_evolved.engine import Engine, to_output
from etherlighting_evolved.links import PortLink
from etherlighting_evolved.switch import EtherlightSwitch
from etherlighting_evolved.transport import SimulatedTransport


def display_engine(*, native=False, **kwargs):
    switch = NativeSwitch() if native else EtherlightSwitch(SimulatedTransport(latency=0))
    return Engine(switch, rules=[], gamma=2.2, transition=0, **kwargs)


def render(engine, **kwargs):
    engine.last_colors, engine.last_outputs = engine.compose(**kwargs)
    return engine.ports()


@pytest.mark.parametrize("precise", [False, True])
def test_display_uses_perceptual_channels_and_preserves_hardware_output(precise):
    engine = display_engine(native=precise, brightness=51, effect="static",
                            effect_params={"color": "#4da6ff"})
    port = render(engine, t=0)[0]
    expected = to_output(Color(77, 166, 255), 51, gamma=2.2, precise=precise)
    assert port["display_rgb"] == pytest.approx([39.27, 84.66, 130.05])
    assert engine.last_outputs[0] == expected
    assert port["output"] == {"color": expected.color.to_hex(), "level": expected.level}
    assert port["preview"] == expected.preview().to_hex()
    assert engine.frame_message()["ports"][0]["display_rgb"] == port["display_rgb"]


def test_display_brightness_follows_rule_and_override_precedence():
    engine = display_engine(brightness=20, effect="static", effect_params={"color": "white"})
    engine.add_rule({"name": "Selected ports", "ports": [1, 2, 3], "conditions": [],
                     "look": {"style": "solid", "color": "#4da6ff", "brightness": 51}})
    engine.set_override(1, "#4da6ff", 80)
    engine.set_override(2, "#4da6ff")
    ports = render(engine, t=0)
    assert ports[0]["display_rgb"] == pytest.approx([61.6, 132.8, 204])
    assert ports[1]["display_rgb"] == pytest.approx([39.27, 84.66, 130.05])
    assert ports[2]["display_rgb"] == pytest.approx([39.27, 84.66, 130.05])
    assert ports[3]["display_rgb"] == pytest.approx([51, 51, 51])
    engine.set_brightness(0)
    ports = render(engine, t=0)
    assert ports[0]["display_rgb"] == pytest.approx([61.6, 132.8, 204])
    assert ports[1]["display_rgb"] == pytest.approx([39.27, 84.66, 130.05])
    assert ports[3]["display_rgb"] == [0, 0, 0]


def test_display_preserves_fractional_crossfade_color():
    engine = display_engine(brightness=51, effect="static", effect_params={"color": "red"})
    engine.transition = 1
    render(engine, t=0, clock=0)
    engine.set_effect("static", {"color": "blue"})
    render(engine, t=0, clock=0)
    port = render(engine, t=.25, clock=.25)[0]
    assert port["display_rgb"] == pytest.approx([109.7296875, 0, 20.3203125])
    assert any(channel % 1 for channel in port["display_rgb"])


def test_native_breathing_display_animates_while_hardware_plan_stays_constant():
    engine = display_engine(native=True, brightness=51, effect="breathing",
                            effect_params={"playback": "synchronized", "color": "#4da6ff", "pace": 2})
    frames, plans = [], []
    for at in (0, .25, .65, 1.1):
        frames.append(render(engine, t=at)[0]["display_rgb"])
        plans.append(engine.last_outputs[0])
    assert len(set(plans)) == 1
    assert plans[0].breath == "breath_2s"
    assert len({tuple(frame) for frame in frames}) > 2
    assert max(frame[2] for frame in frames) - min(frame[2] for frame in frames) > 50


def test_sfp_display_composition_keeps_output_bytes_and_fractional_values(monkeypatch):
    engine = display_engine(native=True, brightness=51)
    monkeypatch.setattr(timing, "monotonic", lambda: engine._t0 + 1.3)
    engine.set_sfp(9, {"mode": "color", "color": "#4da6ff", "color_brightness": 37,
                       "brightness": 73, "effect": "steady"})
    expected_color = Color(77, 166, 255).scaled(.37)
    expected_output = to_output(expected_color, 51 * .73, gamma=2.2)
    state = engine.sfp_indicators.state()[0]
    assert state["display_rgb"] == pytest.approx([c * .37 * .51 * .73 for c in (77, 166, 255)])
    assert engine.sfp_indicators.outputs()[9] == expected_output
    assert state["preview"] == expected_output.preview().to_hex()
    engine.set_sfp(9, {"effect": "two_color", "secondary_color": "red", "period": 6})
    assert engine.sfp_indicators.state()[0]["display_rgb"] != state["display_rgb"]


def test_sfp_display_handles_rule_brightness_off_and_unmanaged():
    engine = display_engine(native=True, brightness=20)
    assert engine.sfp_indicators.state()[0]["display_rgb"] is None
    engine.links[9] = PortLink(9, True, False, 0, False, anomaly=1)
    engine.add_rule({"name": "SFP anomaly", "ports": [9],
                     "conditions": [{"field": "anomaly", "op": "is", "value": True}],
                     "look": {"style": "solid", "color": "#4da6ff", "brightness": 51}})
    engine.set_sfp(9, {"brightness": 50})
    state = engine.sfp_indicators.state()[0]
    assert state["mode"] == "unmanaged"
    assert state["display_rgb"] == pytest.approx([19.635, 42.33, 65.025])
    anomaly = engine.links[9]
    engine.links[9] = PortLink(9, True, False, 0, False)
    engine.sfp_indicators.sync_rules()
    assert engine.sfp_indicators.begin_release(9) is not None
    engine.links[9] = anomaly
    assert engine.sfp_indicators.state()[0]["display_rgb"] is None
    engine.sfp_indicators.end_release(9)
    engine.set_sfp(9, {"mode": "off"})
    assert engine.sfp_indicators.state()[0]["display_rgb"] == [0, 0, 0]
    assert engine.sfp_indicators.outputs()[9].color.is_off
