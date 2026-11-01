import math

import pytest

from etherlighting_evolved.color import BLACK
from etherlighting_evolved.rules import TEMPLATES, Condition, Look, Rule


@pytest.mark.parametrize("field", ["rate", "brightness"])
@pytest.mark.parametrize("value", [math.nan, math.inf, -math.inf])
def test_nonfinite_rule_output_is_rejected(field, value):
    with pytest.raises(ValueError, match="finite"):
        Look.parse({field: value})


@pytest.mark.parametrize("style,limit", [("pulse", 0.33), ("blink", 1.0), ("flash", 0.5)])
def test_rule_rates_fit_the_conservative_output_cadence(style, limit):
    assert Look.parse({"style": style, "rate": 10}).rate == limit


def test_flash_can_be_seen_at_each_sampling_phase():
    look = Look.parse({"style": "flash", "rate": 0.5, "color": "white"})
    cadence = 8 / 30
    for offset in (0, 0.02, 0.09, 0.14, 0.2, 0.26):
        samples = [look.render(offset + step * cadence, BLACK) for step in range(16)]
        assert any(not color.is_off for color in samples)
        assert any(color.is_off for color in samples)


def test_shipped_templates_keep_their_validated_rates():
    for template in TEMPLATES:
        if "rate" in template["look"]:
            assert Look.parse(template["look"]).rate == template["look"]["rate"]


@pytest.mark.parametrize("value", [True, 1.5, math.inf, math.nan])
def test_invalid_rule_port_does_not_target_a_different_port(value):
    with pytest.raises(ValueError):
        Rule.parse({"ports": [value]}, num_ports=8)


@pytest.mark.parametrize("value", [True, math.inf, math.nan, None])
def test_invalid_numeric_condition_is_rejected(value):
    with pytest.raises(ValueError, match="finite"):
        Condition.parse({"field": "poe_power", "op": "gt", "value": value})
