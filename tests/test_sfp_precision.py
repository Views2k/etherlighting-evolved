import math

import pytest
from test_native_engine import NativeSwitch

from etherlighting_evolved import timing
from etherlighting_evolved.color import BLACK
from etherlighting_evolved.engine import Engine, to_output
from etherlighting_evolved.switch import PortOutput


class PrecisionSwitch(NativeSwitch):
    def __init__(self, enabled=True):
        super().__init__()
        self.sfp_precision_enabled = enabled

    @property
    def sfp_precise(self):
        return self.sfp_precision_enabled


@pytest.fixture
def sfp_clock(monkeypatch):
    clock = [0.0]
    monkeypatch.setattr(timing, "monotonic", lambda: clock[0])
    return clock


def engine_for_effect(effect, *, precision=True, gamma=2.2, brightness=100, **settings):
    engine = Engine(PrecisionSwitch(precision), rules=[], gamma=gamma, brightness=brightness)
    engine.set_sfp(9, {"mode": "color", "effect": effect, "period": 6,
                       "color": "blue", "secondary_color": "red", **settings})
    return engine


@pytest.mark.parametrize("effect", ["pulse", "color_cycle", "two_color"])
@pytest.mark.parametrize("gamma", [1.0, 2.2, 3.0])
def test_animated_precision_applies_each_brightness_and_gamma_exactly_once(effect, gamma, sfp_clock):
    engine = engine_for_effect(effect, gamma=gamma, brightness=50, color_brightness=60,
                               secondary_brightness=30)
    engine.set_sfp(9, {"brightness": 40})
    sfp_clock[0] = 1.5 if effect == "two_color" else 0.0
    channels = (255 * 0.3 * 0.5, 0.0, 255 * 0.6 * 0.5) if effect == "two_color" else (0.0, 0.0, 255 * 0.6)
    expected = tuple(round(65280 * (channel / 255) ** gamma * (0.5 * 0.4) ** gamma) for channel in channels)
    output = engine.sfp_indicators.outputs()[9]
    assert output.precise_rgb == expected
    assert output.level == 100 and output.breath is None
    assert all(type(channel) is int and 0 <= channel <= 65280 for channel in output.precise_rgb)


@pytest.mark.parametrize("effect", ["pulse", "color_cycle", "two_color"])
def test_twenty_percent_animations_keep_more_than_one_thousand_distinct_channel_codes(effect, sfp_clock):
    engine = engine_for_effect(effect, brightness=20)
    channels = [set(), set(), set()]
    previews = set()
    for tick in range(24001):
        sfp_clock[0] = tick / 4000
        output = engine.sfp_indicators.outputs()[9]
        previews.add(output.color.to_hex())
        for index, value in enumerate(output.precise_rgb):
            channels[index].add(value)
    assert max(map(len, channels)) > 1000
    if effect == "pulse":
        assert len(previews) <= 9
        assert min(channels[2]) == 0
        assert max(channels[2]) == round(65280 * 0.2 ** 2.2)


@pytest.mark.parametrize("effect", ["pulse", "color_cycle", "two_color"])
def test_low_intensity_survives_a_black_eight_bit_preview(effect, sfp_clock):
    engine = engine_for_effect(effect, gamma=1, brightness=1, color_brightness=1, secondary_brightness=1)
    sfp_clock[0] = 1.5 if effect == "two_color" else 0.0
    output = engine.sfp_indicators.outputs()[9]
    assert output.color == BLACK
    assert max(output.precise_rgb) > 0
    assert output.level == 100


def test_pulse_preserves_sub_byte_render_values_before_encoding(sfp_clock):
    engine = engine_for_effect("pulse", gamma=1, color_brightness=1)
    sfp_clock[0] = 2.7
    rendered = engine.sfp[9].render(2.7, 9)
    assert rendered == BLACK
    assert rendered.precise_channels[2] > 0
    output = engine.sfp_indicators.outputs()[9]
    amplitude = (1 + math.cos(math.tau * 2.7 / 6)) / 2
    assert output.precise_rgb == (0, 0, round(65280 * 0.01 * amplitude))


@pytest.mark.parametrize("effect", ["pulse", "color_cycle", "two_color"])
def test_unverified_capability_preserves_existing_rgb8_output(effect, sfp_clock):
    engine = engine_for_effect(effect, precision=False, brightness=37, color_brightness=63,
                               secondary_brightness=41)
    engine.set_sfp(9, {"brightness": 42})
    for elapsed in (0.0, 0.23, 1.5, 3.0, 5.9):
        sfp_clock[0] = elapsed
        expected = to_output(engine.sfp[9].render(elapsed, 9), 37 * 42 / 100, gamma=engine.gamma)
        assert engine.sfp_indicators.outputs()[9] == expected
        assert engine.sfp_indicators.outputs()[9].precise_rgb is None


@pytest.mark.parametrize("mode", ["color", "off", "unmanaged"])
def test_steady_off_and_unmanaged_keep_their_existing_output_contract(mode, sfp_clock):
    engine = engine_for_effect("steady", brightness=37, color_brightness=63)
    engine.set_sfp(9, {"mode": mode, "brightness": 42})
    sfp_clock[0] = 1.23
    if mode == "unmanaged":
        assert engine.sfp_indicators.outputs() == {}
    else:
        expected = to_output(engine.sfp[9].render(1.23, 9), 37 * 42 / 100, gamma=engine.gamma)
        assert engine.sfp_indicators.outputs()[9] == expected
        assert engine.sfp_indicators.outputs()[9].precise_rgb is None


@pytest.mark.parametrize("effect", ["pulse", "color_cycle", "two_color"])
def test_off_does_not_activate_precision_for_a_saved_animation(effect, sfp_clock):
    engine = engine_for_effect(effect)
    engine.set_sfp(9, {"mode": "off"})
    assert engine.sfp_indicators.outputs()[9] == PortOutput.off()
    assert engine.sfp_indicators.outputs()[9].precise_rgb is None


@pytest.mark.parametrize("effect", ["pulse", "color_cycle", "two_color"])
def test_zero_master_brightness_has_exact_zero_precision_components(effect, sfp_clock):
    engine = engine_for_effect(effect, brightness=0)
    sfp_clock[0] = 1.123
    assert engine.sfp_indicators.outputs()[9].precise_rgb == (0, 0, 0)
    assert engine.sfp_indicators.outputs()[9].level == 100


def test_releasing_sfp_is_omitted_and_never_joins_the_copper_frame(sfp_clock):
    engine = engine_for_effect("pulse")
    engine.set_sfp(10, {"mode": "color", "effect": "two_color"})
    engine.sfp_indicators.releasing.add(9)
    assert set(engine.sfp_indicators.outputs()) == {10}
    assert engine.sfp_indicators.outputs()[10].precise_rgb is not None
    assert len(engine.compose(t=1)[1]) == 8
