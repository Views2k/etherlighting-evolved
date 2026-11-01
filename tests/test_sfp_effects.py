import math
from dataclasses import FrozenInstanceError

import pytest

from etherlighting_evolved.color import BLACK, Color
from etherlighting_evolved.sfp import EFFECTS, SfpLight


def test_legacy_settings_keep_color_and_brightness_with_steady_default():
    old = {"mode": "color", "color": "#123456", "brightness": 37}
    light = SfpLight().updated(old)
    assert light == SfpLight("color", Color(18, 52, 86), 37)
    assert light.effect == "steady" and light.period == 6
    assert light.render(25, 9) == Color(18, 52, 86)
    assert SfpLight().updated(light.to_dict()) == light
    assert SfpLight().updated({}).mode == "unmanaged"


def test_settings_are_immutable_and_updates_leave_other_controls_intact():
    light = SfpLight().updated({"mode": "color", "color": "red", "effect": "two_color", "period": 9.5,
                                "brightness": 63, "color_brightness": 42, "secondary_brightness": 17})
    changed = light.updated({"secondary_color": "green"})
    assert changed.to_dict() == light.to_dict() | {"secondary_color": "#00ff00"}
    assert SfpLight().updated(changed.to_dict()) == changed
    with pytest.raises(FrozenInstanceError):
        light.period = 3


@pytest.mark.parametrize("effect", EFFECTS)
def test_overall_brightness_is_not_composed_inside_the_effect(effect):
    bright = SfpLight().updated({"mode": "color", "effect": effect, "color": "#d85b31",
                                 "color_brightness": 45, "secondary_brightness": 29})
    dim = bright.updated({"brightness": 12})
    for t in (0, 0.5, 1.5, 3.0, 4.5):
        assert dim.render(t, 9).precise_channels == bright.render(t, 9).precise_channels


def test_two_color_controls_are_independent_and_zero_is_black():
    light = SfpLight().updated({"mode": "color", "effect": "two_color", "color": "red",
                                "secondary_color": "blue", "color_brightness": 20, "secondary_brightness": 60})
    assert light.render(0, 9).precise_channels == pytest.approx((51, 0, 0))
    assert light.render(3, 9).precise_channels == pytest.approx((0, 0, 153))
    assert light.render(1.5, 9).precise_channels == pytest.approx((25.5, 0, 76.5))
    muted = light.updated({"secondary_brightness": 0})
    assert muted.render(0, 9).precise_channels == pytest.approx((51, 0, 0))
    assert muted.render(3, 9) == BLACK


def test_pulse_has_a_full_bright_to_dark_to_bright_cycle():
    light = SfpLight().updated({"mode": "color", "effect": "pulse", "color": "blue", "color_brightness": 40})
    assert light.render(0, 9).precise_channels == pytest.approx((0, 0, 102))
    assert light.render(1.5, 9).precise_channels == pytest.approx((0, 0, 51))
    assert light.render(3, 9) == BLACK
    assert light.render(4.5, 9).precise_channels == pytest.approx((0, 0, 51))
    assert light.render(6, 9).precise_channels == pytest.approx((0, 0, 102))


@pytest.mark.parametrize("effect", EFFECTS)
def test_periodic_render_has_no_port_offset_or_discontinuity(effect):
    light = SfpLight().updated({"mode": "color", "effect": effect, "color": "#e94b23",
                                "secondary_color": "#318bf3", "period": 6.0})
    for t in (0, 0.01, 1, 2.3, 3, 5.999, 6, 11.9):
        current = light.render(t, 9).precise_channels
        assert current == light.render(t, 10).precise_channels
        assert current == pytest.approx(light.render(t + 6, 9).precise_channels)
        nearby = light.render(t + 0.00001, 9).precise_channels
        assert max(abs(a - b) for a, b in zip(current, nearby, strict=True)) < 0.005
        assert all(0 <= channel <= 255 for channel in current)


@pytest.mark.parametrize("effect", ["pulse", "two_color"])
def test_fade_velocity_reaches_zero_at_endpoints(effect):
    light = SfpLight().updated({"mode": "color", "effect": effect, "color": "red", "secondary_color": "blue"})
    epsilon = 0.0001
    for t in (0, 3, 6):
        before = light.render(t - epsilon, 9).precise_channels
        at = light.render(t, 9).precise_channels
        after = light.render(t + epsilon, 9).precise_channels
        assert max(abs(a - b) / epsilon for a, b in zip(before, at, strict=True)) < 0.02
        assert max(abs(a - b) / epsilon for a, b in zip(after, at, strict=True)) < 0.02


def test_color_cycle_moves_even_from_default_white_and_retains_black_and_brightness():
    light = SfpLight().updated({"mode": "color", "effect": "color_cycle", "color_brightness": 40})
    samples = [light.render(t, 9) for t in range(6)]
    assert len(set(samples)) == 6
    assert all(max(color.precise_channels) == pytest.approx(102) for color in samples)
    assert light.updated({"color": "black"}).render(1, 9) == BLACK
    assert light.updated({"color_brightness": 0}).render(1, 9) == BLACK


def test_color_cycle_has_smooth_velocity_through_hue_sector_boundaries():
    light = SfpLight().updated({"mode": "color", "effect": "color_cycle", "color": "red"})
    epsilon = 0.0001
    for t in range(7):
        before = light.render(t - epsilon, 9).precise_channels
        at = light.render(t, 9).precise_channels
        after = light.render(t + epsilon, 9).precise_channels
        assert max(abs(a - b) / epsilon for a, b in zip(before, at, strict=True)) < 0.08
        assert max(abs(a - b) / epsilon for a, b in zip(after, at, strict=True)) < 0.08


@pytest.mark.parametrize("effect", EFFECTS)
@pytest.mark.parametrize("mode", ["unmanaged", "off"])
def test_inactive_modes_do_not_render_light(effect, mode):
    light = SfpLight().updated({"mode": mode, "effect": effect})
    assert light.render(1.25, 9) == BLACK


@pytest.mark.parametrize("changes", [
    {"mode": "invalid"}, {"mode": True}, {"effect": "flash"}, {"effect": None},
    {"period": 1.99}, {"period": 30.01}, {"period": "6"}, {"period": True},
    {"period": math.inf}, {"period": math.nan}, {"period": 10**400}, {"brightness": False}, {"brightness": 101},
    {"brightness": 50.5}, {"color_brightness": -1}, {"secondary_brightness": 1.5},
    {"color_brightness": "50"}, {"color": [True, 0, 0]}, {"color": [1.5, 0, 0]},
    {"secondary_color": {"r": 0, "g": 0, "b": "255"}},
    {"color": {"r": 0, "g": 0, "b": 255, "alpha": 1}},
    {"secondary_color": [256, 0, 0]}, {"mode_mask": "100"}, [], None,
])
def test_invalid_settings_fail_without_mutating_previous_values(changes):
    light = SfpLight()
    original = light.to_dict()
    with pytest.raises(ValueError):
        light.updated(changes)
    assert light.to_dict() == original


@pytest.mark.parametrize("port", [0, -1, 65, True, 9.0, "9"])
def test_invalid_port_numbers_cannot_render(port):
    with pytest.raises(ValueError):
        SfpLight(mode="color").render(0, port)


@pytest.mark.parametrize("time", [True, "1", None, math.inf, -math.inf, math.nan, 10**400])
def test_render_rejects_invalid_time(time):
    with pytest.raises(ValueError):
        SfpLight(mode="color").render(time, 9)
