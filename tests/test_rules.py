from datetime import datetime

import pytest

from etherlighting_evolved.color import BLACK, Color
from etherlighting_evolved.rules import DEFAULT_RULES, TEMPLATES, Condition, Look, PortFacts, Rule, first_match

NOON = datetime(2026, 6, 3, 12, 0)  # a Wednesday


def facts(**kw):
    return PortFacts(port=kw.pop("port", 1), now=kw.pop("now", NOON), **kw)


def test_templates_and_defaults_all_load():
    for spec in TEMPLATES + DEFAULT_RULES:
        Rule.parse(spec, num_ports=8)


def test_offline_rule():
    rule = Rule.parse(TEMPLATES[0], num_ports=8)
    assert rule.matches(facts(link=False))
    assert not rule.matches(facts(link=True))
    assert not rule.matches(facts())  # unknown link state never matches


def test_first_match_wins():
    rules = [Rule.parse(r, num_ports=8) for r in DEFAULT_RULES]
    assert first_match(rules, facts(link=False, anomaly=True)).name == "Anomaly"
    assert first_match(rules, facts(link=False, anomaly=False)).name == "Offline"
    assert first_match(rules, facts(link=True, anomaly=False)) is None


def test_time_window_wraps_midnight():
    night = Condition.parse({"field": "time", "op": "between", "value": ["22:00", "07:00"]})
    assert night.test(facts(now=datetime(2026, 6, 3, 23, 15)))
    assert night.test(facts(now=datetime(2026, 6, 3, 6, 59)))
    assert not night.test(facts(now=datetime(2026, 6, 3, 7, 0)))
    assert not night.test(facts(now=NOON))


def test_weekday_and_numbers():
    weekend = Condition.parse({"field": "weekday", "op": "in", "value": ["Saturday", "sun"]})
    assert weekend.test(facts(now=datetime(2026, 6, 6, 10, 0)))
    assert not weekend.test(facts())
    fast = Condition.parse({"field": "speed", "op": "between", "value": [9999, 2500]})
    assert fast.value == (2500.0, 9999.0)
    assert fast.test(facts(speed=5000))


def test_poe_tiers():
    rule = Rule.parse({"name": "Big PoE", "conditions": [{"field": "poe", "op": "in", "value": ["poe++", "poe+++"]}],
                       "look": {"color": "purple"}}, num_ports=8)
    assert rule.matches(facts(poe="poe+++"))
    assert not rule.matches(facts(poe="poe+"))


def test_match_any_and_port_filter():
    rule = Rule.parse({"name": "x", "match": "any", "ports": [2, 3],
                       "conditions": [{"field": "link", "op": "is", "value": False},
                                      {"field": "anomaly", "op": "is", "value": True}]}, num_ports=8)
    assert rule.matches(facts(port=2, link=True, anomaly=True))
    assert not rule.matches(facts(port=1, link=False))


def test_rule_round_trip():
    rule = Rule.parse(TEMPLATES[15], num_ports=8)
    again = Rule.parse(rule.to_dict(), num_ports=8)
    assert again.to_dict() == rule.to_dict()


@pytest.mark.parametrize(
    "bad",
    [
        {"conditions": [{"field": "color", "op": "is", "value": 1}]},
        {"conditions": [{"field": "link", "op": "gt", "value": 1}]},
        {"conditions": [{"field": "poe", "op": "is", "value": "poe++++"}]},
        {"conditions": [{"field": "time", "op": "between", "value": ["25:00", "07:00"]}]},
        {"look": {"style": "wobble"}},
        {"ports": [9]},
    ],
)
def test_bad_rules_are_rejected(bad):
    with pytest.raises(ValueError):
        Rule.parse(bad, num_ports=8)


def test_look_styles():
    red = Color(255, 0, 0)
    assert Look(red, "blink").render(0.1, BLACK) == red
    assert Look(red, "blink").render(0.6, BLACK) == BLACK
    assert Look(red, "effect").render(0.3, Color(1, 2, 3)) == Color(1, 2, 3)
    assert Look(red, "pulse").render(0.0, BLACK).r < Look(red, "pulse").render(0.5, BLACK).r
