import colorsys
import math
from dataclasses import FrozenInstanceError

import pytest

from etherlighting_evolved.color import BLACK, WHITE, Color
from etherlighting_evolved.effects.ambient import weighted


def test_plain_color_keeps_public_rgb_identity_and_uses_exact_components():
    plain = Color(0, 0, 0)
    dimmed = WHITE.scaled(0.001)
    assert dimmed == plain and hash(dimmed) == hash(plain)
    assert repr(dimmed) == "Color(r=0, g=0, b=0)"
    assert dimmed.to_hex() == str(dimmed) == "#000000"
    assert dimmed.is_off and dimmed.max_channel == 0
    assert Color.__match_args__ == ("r", "g", "b")
    assert plain.precise_channels == (0.0, 0.0, 0.0)
    assert dimmed.precise_channels == pytest.approx((0.255, 0.255, 0.255))
    assert Color.parse(dimmed) is dimmed
    with pytest.raises(FrozenInstanceError):
        dimmed._precise = (1.0, 1.0, 1.0)
    with pytest.raises(TypeError):
        Color(0, 0, 0, (1.0, 1.0, 1.0))


def test_sub_byte_ramp_retains_every_sample_before_output_encoding():
    ramp = [Color(0, 0, 255).scaled(index / 100000) for index in range(401)]
    assert len({color.to_hex() for color in ramp}) == 2
    values = [color.precise_channels[2] for color in ramp]
    assert len(set(values)) == 401
    assert values == sorted(values)
    assert all(color.precise_channels[:2] == (0.0, 0.0) for color in ramp)
    assert values == pytest.approx([index * 255 / 100000 for index in range(401)])


def test_composed_scaling_retains_precision_from_before_each_rounding():
    source = Color(63, 128, 255)
    result = source.scaled(0.017).scaled(0.3).scaled(0.41)
    assert (result.r, result.g, result.b) == (0, 0, 0)
    assert result.precise_channels == pytest.approx([channel * 0.017 * 0.3 * 0.41 for channel in (63, 128, 255)])
    direct = source.scaled(0.017 * 0.3 * 0.41)
    assert result.precise_channels == pytest.approx(direct.precise_channels)


def test_lerp_uses_precise_endpoints_even_when_both_public_colors_round_to_black():
    low = Color(0, 0, 255).scaled(0.0001)
    high = Color(0, 0, 255).scaled(0.001)
    assert low == high == BLACK
    middle = low.lerp(high, 0.25)
    assert middle == BLACK
    assert middle.precise_channels == pytest.approx((0.0, 0.0, 0.0255 * 0.75 + 0.255 * 0.25))
    assert middle.scaled(0.2).precise_channels[2] == pytest.approx(middle.precise_channels[2] * 0.2)


def test_hsv_keeps_unrounded_components_through_blending_and_scaling():
    hue, saturation, value = 0.6137, 0.8731, 0.0723
    channels = tuple(channel * 255 for channel in colorsys.hsv_to_rgb(hue, saturation, value))
    color = Color.from_hsv(hue, saturation, value)
    assert color == Color(*(round(channel) for channel in channels))
    assert color.precise_channels == pytest.approx(channels)
    result = color.lerp(BLACK, 0.73).scaled(0.035)
    assert result.precise_channels == pytest.approx([channel * 0.27 * 0.035 for channel in channels])


def test_weighted_color_preserves_precise_inputs_and_legacy_aggregate_rounding():
    lights = [(Color(31, 73, 149).scaled(0.013), 0.17),
              (Color.from_hsv(0.31, 0.93, 0.05), 0.29)]
    expected_legacy = tuple(round(sum(getattr(color, channel) * weight for color, weight in lights))
                            for channel in ("r", "g", "b"))
    expected_precise = tuple(sum(color.precise_channels[index] * weight for color, weight in lights)
                             for index in range(3))
    result = weighted(lights)
    assert result == Color(*expected_legacy)
    assert result.precise_channels == pytest.approx(expected_precise)
    assert result.scaled(0.21).precise_channels == pytest.approx([channel * 0.21 for channel in expected_precise])


@pytest.mark.parametrize("operation", [
    lambda: WHITE.scaled(2), lambda: WHITE.lerp(Color(255, 0, 0), -1),
    lambda: BLACK.lerp(WHITE, 2), lambda: weighted([(WHITE, 2)]),
])
def test_positive_clamping_applies_to_precise_and_public_components(operation):
    result = operation()
    assert result == WHITE
    assert result.precise_channels == (255.0, 255.0, 255.0)


@pytest.mark.parametrize("operation", [
    lambda: WHITE.scaled(-1), lambda: WHITE.lerp(BLACK, 2),
    lambda: weighted([(WHITE, -1)]), lambda: weighted([]),
])
def test_negative_and_empty_clamping_produce_exact_black(operation):
    result = operation()
    assert result == BLACK
    assert result.precise_channels == (0.0, 0.0, 0.0)


def test_saturation_is_kept_when_a_clipped_color_is_scaled_again():
    result = Color(255, 51, 0).scaled(2).scaled(0.1)
    assert result.precise_channels == pytest.approx((25.5, 10.2, 0.0))


def test_large_finite_inputs_cannot_put_infinite_values_in_precise_channels():
    for result in (Color(255, 0, 63).scaled(1e308), BLACK.lerp(WHITE, 1e308),
                   Color.from_hsv(1e308, 1e308, 1e308)):
        assert all(math.isfinite(channel) and 0 <= channel <= 255 for channel in result.precise_channels)
    with pytest.raises(ValueError, match="weighted color channels must be finite"):
        weighted([(WHITE, 1e308)])


@pytest.mark.parametrize("bad", [math.nan, math.inf, -math.inf])
@pytest.mark.parametrize("operation", [
    lambda bad: Color.from_hsv(bad), lambda bad: Color.from_hsv(0, bad),
    lambda bad: Color.from_hsv(0, 1, bad), lambda bad: WHITE.scaled(bad),
    lambda bad: WHITE.lerp(BLACK, bad), lambda bad: weighted([(WHITE, bad)]),
])
def test_nonfinite_transform_inputs_are_rejected(operation, bad):
    with pytest.raises(ValueError, match="finite"):
        operation(bad)


@pytest.mark.parametrize("precise", [(math.nan, 0, 0), (0, math.inf, 0), (0, 0, -math.inf),
                                    (-0.1, 0, 0), (255.1, 0, 0), (10**1000, 0, 0), (0, 0), (True, 0, 0)])
def test_private_precision_constructor_rejects_invalid_channels(precise):
    with pytest.raises(ValueError, match="precise color channels"):
        Color._with_precision((0, 0, 0), precise)


def test_legacy_hsv_scale_and_blend_bytes_remain_exact_over_a_mixed_sequence():
    for index in range(101):
        h, s, v = index * 0.071, index / 100, 1 - index / 200
        expected = tuple(round(channel * 255) for channel in colorsys.hsv_to_rgb(h % 1, s, v))
        actual = Color.from_hsv(h, s, v)
        for scale, blend in ((0.019, 0.017), (0.49, 0.513), (2.1, 0.981)):
            expected = tuple(min(255, round(channel * scale)) for channel in expected)
            expected = tuple(round(channel + (target - channel) * blend)
                             for channel, target in zip(expected, (31, 73, 149), strict=True))
            actual = actual.scaled(scale).lerp(Color(31, 73, 149), blend)
            assert actual == Color(*expected)
            assert actual.to_hex() == "#" + "".join(f"{channel:02x}" for channel in expected)
