"""Rules: set a port's look when its conditions match.

A rule is a list of conditions and a look. Rules are checked in order, and the first rule that matches a port
sets its look. Ports that no rule matches show their zone's effect.

    {
        "name": "Offline",
        "conditions": [{"field": "link", "op": "is", "value": false}],
        "look": {"color": "#ff0000", "style": "solid"}
    }

Conditions check port status (link, speed, PoE, anomaly, ...) or the clock (time, weekday).
"""

from __future__ import annotations

import math
import re
import uuid
from dataclasses import dataclass, field
from datetime import datetime
from typing import Any

from .color import BLACK, Color

WEEKDAYS = ("mon", "tue", "wed", "thu", "fri", "sat", "sun")
POE_TIERS = ("off", "poe", "poe+", "poe++", "poe+++")
STP_CHOICES = ("forwarding", "blocking", "learning", "listening", "discarding", "disabled")
STYLES = ("solid", "blink", "pulse", "flash", "effect")

_TIME = re.compile(r"^([01]?\d|2[0-3]):([0-5]\d)$")

OPERATORS = {
    "bool": ("is",),
    "number": ("gt", "ge", "lt", "le", "eq", "ne", "between"),
    "choice": ("is", "is_not", "in", "not_in"),
    "time": ("between",),
    "weekday": ("in",),
}


@dataclass(frozen=True)
class Field:
    key: str
    label: str
    type: str
    unit: str | None = None
    choices: tuple[str, ...] | None = None
    description: str = ""

    def schema(self) -> dict:
        data = {
            "key": self.key,
            "label": self.label,
            "type": self.type,
            "operators": list(OPERATORS[self.type]),
            "unit": self.unit,
            "choices": list(self.choices) if self.choices else None,
            "description": self.description or None,
        }
        return {k: v for k, v in data.items() if v is not None}


FIELDS: dict[str, Field] = {
    f.key: f
    for f in (
        Field("link", "Link up", "bool", description="A device is connected and the link is up."),
        Field("speed", "Link speed", "number", unit="Mbps", description="Negotiated speed, 0 without a link."),
        Field("full_duplex", "Full duplex", "bool"),
        Field("uplink", "Uplink port", "bool"),
        Field("anomaly", "Anomaly", "bool", description="The switch flags a problem on the port (anomaly not 0x0)."),
        Field("stp_state", "Spanning tree", "choice", choices=STP_CHOICES),
        Field("lag", "In a LAG", "bool", description="The port is a member of a link aggregation group."),
        Field("traffic", "Traffic", "number", unit="Mbps", description="Receive plus transmit."),
        Field("poe", "PoE type", "choice", choices=POE_TIERS,
              description="poe = 802.3af, poe+ = 802.3at, poe++ = 802.3bt type 3, poe+++ = 802.3bt type 4."),
        Field("poe_power", "PoE power", "number", unit="W", description="Power the port is delivering."),
        Field("time", "Time of day", "time", description='Local time, e.g. between ["22:00", "07:00"].'),
        Field("weekday", "Day of week", "weekday", choices=WEEKDAYS),
    )
}


@dataclass
class PortFacts:
    """Everything a rule can ask about one port at one moment. None means the switch hasn't told us."""

    port: int
    now: datetime
    link: bool | None = None
    speed: float | None = None
    full_duplex: bool | None = None
    uplink: bool | None = None
    anomaly: bool | None = None
    stp_state: str | None = None
    lag: bool | None = None
    traffic: float | None = None
    poe: str | None = None
    poe_power: float | None = None

    def value(self, key: str) -> Any:
        if key == "time":
            return self.now.hour * 60 + self.now.minute
        if key == "weekday":
            return WEEKDAYS[self.now.weekday()]
        return getattr(self, key)


def parse_clock(text: str) -> int:
    m = _TIME.match(str(text).strip())
    if not m:
        raise ValueError(f"times look like 07:30, got {text!r}")
    return int(m.group(1)) * 60 + int(m.group(2))


@dataclass(frozen=True)
class Condition:
    field: str
    op: str
    value: Any

    @classmethod
    def parse(cls, data: dict) -> Condition:
        try:
            key, op, value = data["field"], data["op"], data["value"]
        except (KeyError, TypeError):
            raise ValueError("a condition needs field, op and value") from None
        spec = FIELDS.get(key)
        if spec is None:
            raise ValueError(f"unknown field {key!r}; see /api/rules/fields")
        if op not in OPERATORS[spec.type]:
            raise ValueError(f"{key} supports {', '.join(OPERATORS[spec.type])}, not {op!r}")

        if spec.type == "bool":
            value = value if isinstance(value, bool) else str(value).lower() in ("true", "1", "yes", "on")
        elif spec.type == "number":
            if op == "between":
                low, high = (float(v) for v in value)
                value = (min(low, high), max(low, high))
            else:
                value = float(value)
        elif spec.type == "choice":
            values = list(value) if op in ("in", "not_in") else [value]
            for v in values:
                if v not in spec.choices:
                    raise ValueError(f"{key} must be one of {', '.join(spec.choices)}")
            value = tuple(values) if op in ("in", "not_in") else values[0]
        elif spec.type == "time":
            start, end = value
            value = (parse_clock(start), parse_clock(end))
        elif spec.type == "weekday":
            days = tuple(str(d).lower()[:3] for d in value)
            for d in days:
                if d not in WEEKDAYS:
                    raise ValueError(f"weekdays are {', '.join(WEEKDAYS)}")
            value = days
        return cls(key, op, value)

    def test(self, facts: PortFacts) -> bool:
        actual = facts.value(self.field)
        if actual is None:
            return False
        op, want = self.op, self.value
        if op == "is":
            return actual == want
        if op == "is_not":
            return actual != want
        if op == "in":
            return actual in want
        if op == "not_in":
            return actual not in want
        if op == "between" and self.field == "time":
            start, end = want
            return start <= actual < end if start <= end else actual >= start or actual < end
        if op == "between":
            return want[0] <= actual <= want[1]
        return {
            "gt": actual > want,
            "ge": actual >= want,
            "lt": actual < want,
            "le": actual <= want,
            "eq": actual == want,
            "ne": actual != want,
        }[op]

    def to_dict(self) -> dict:
        value = self.value
        if self.field == "time":
            value = [f"{m // 60:02d}:{m % 60:02d}" for m in value]
        elif isinstance(value, tuple):
            value = list(value)
        return {"field": self.field, "op": self.op, "value": value}


@dataclass(frozen=True)
class Look:
    """How a matching port looks.

    style: solid, blink, pulse, flash, or "effect" (keep the zone's effect color, e.g. to only change brightness).
    brightness: optional 0-100 that replaces the global brightness for these ports.
    """

    color: Color = BLACK
    style: str = "solid"
    rate: float = 1.0
    brightness: int | None = None

    @classmethod
    def parse(cls, data: dict) -> Look:
        style = data.get("style", "solid")
        if style not in STYLES:
            raise ValueError(f"style must be one of {', '.join(STYLES)}")
        color = Color.parse(data.get("color", "#000000"))
        rate = min(10.0, max(0.05, float(data.get("rate", 1.0))))
        brightness = data.get("brightness")
        if brightness is not None:
            brightness = int(round(min(100.0, max(0.0, float(brightness)))))
        return cls(color, style, rate, brightness)

    def render(self, t: float, underneath: Color) -> Color:
        phase = (t * self.rate) % 1.0
        if self.style == "blink":
            return self.color if phase < 0.5 else BLACK
        if self.style == "pulse":
            return self.color.scaled(0.15 + 0.85 * (0.5 - 0.5 * math.cos(2 * math.pi * phase)))
        if self.style == "flash":
            return self.color if phase < 0.12 else BLACK
        if self.style == "effect":
            return underneath
        return self.color

    def to_dict(self) -> dict:
        return {"color": self.color.to_hex(), "style": self.style, "rate": self.rate, "brightness": self.brightness}


@dataclass
class Rule:
    name: str
    look: Look
    conditions: list[Condition] = field(default_factory=list)
    match: str = "all"
    ports: tuple[int, ...] | None = None
    enabled: bool = True
    id: str = field(default_factory=lambda: uuid.uuid4().hex[:8])

    @classmethod
    def parse(cls, data: dict, *, num_ports: int) -> Rule:
        if not isinstance(data, dict):
            raise ValueError("a rule must be an object")
        name = str(data.get("name") or "Rule").strip()[:60]
        match = data.get("match", "all")
        if match not in ("all", "any"):
            raise ValueError("match must be 'all' or 'any'")
        ports = data.get("ports")
        if ports is not None:
            ports = tuple(sorted({int(p) for p in ports}))
            if any(not 1 <= p <= num_ports for p in ports):
                raise ValueError(f"rule ports must be between 1 and {num_ports}")
        rule = cls(
            name=name,
            look=Look.parse(data.get("look") or {}),
            conditions=[Condition.parse(c) for c in data.get("conditions") or []],
            match=match,
            ports=ports or None,
            enabled=bool(data.get("enabled", True)),
        )
        if data.get("id"):
            rule.id = str(data["id"])[:32]
        return rule

    def matches(self, facts: PortFacts) -> bool:
        if not self.enabled:
            return False
        if self.ports and facts.port not in self.ports:
            return False
        if not self.conditions:
            return True
        results = (c.test(facts) for c in self.conditions)
        return all(results) if self.match == "all" else any(results)

    def to_dict(self) -> dict:
        return {
            "id": self.id,
            "name": self.name,
            "enabled": self.enabled,
            "match": self.match,
            "ports": list(self.ports) if self.ports else None,
            "conditions": [c.to_dict() for c in self.conditions],
            "look": self.look.to_dict(),
        }


def first_match(rules: list[Rule], facts: PortFacts) -> Rule | None:
    return next((rule for rule in rules if rule.matches(facts)), None)


def _when(field_: str, op: str, value: Any) -> dict:
    return {"field": field_, "op": op, "value": value}


# Ready-made rules the UI can offer with one tap.
TEMPLATES: list[dict] = [
    {"name": "Offline", "conditions": [_when("link", "is", False)],
     "look": {"color": "#ff0000", "style": "solid"}},
    {"name": "Anomaly", "conditions": [_when("anomaly", "is", True)],
     "look": {"color": "#ff9f0a", "style": "blink", "rate": 2}},
    {"name": "Uplink", "conditions": [_when("uplink", "is", True)],
     "look": {"color": "#0a84ff", "style": "solid"}},
    {"name": "Spanning tree blocking", "conditions": [_when("stp_state", "in", ["blocking", "discarding"])],
     "look": {"color": "#ff9f0a", "style": "pulse", "rate": 0.5}},
    {"name": "PoE", "conditions": [_when("poe", "is", "poe")], "look": {"color": "#30d158"}},
    {"name": "PoE+", "conditions": [_when("poe", "is", "poe+")], "look": {"color": "#64d2ff"}},
    {"name": "PoE++", "conditions": [_when("poe", "is", "poe++")], "look": {"color": "#bf5af2"}},
    {"name": "PoE+++", "conditions": [_when("poe", "is", "poe+++")], "look": {"color": "#ff375f"}},
    {"name": "10 GbE", "conditions": [_when("speed", "ge", 10000)], "look": {"color": "#0a84ff"}},
    {"name": "5 GbE", "conditions": [_when("speed", "between", [5000, 9999])], "look": {"color": "#5e5ce6"}},
    {"name": "2.5 GbE", "conditions": [_when("speed", "between", [2500, 4999])], "look": {"color": "#30d158"}},
    {"name": "1 GbE", "conditions": [_when("speed", "between", [1000, 2499])], "look": {"color": "#ffd60a"}},
    {"name": "Slow link", "conditions": [_when("link", "is", True), _when("speed", "lt", 1000)],
     "look": {"color": "#ff9f0a"}},
    {"name": "Half duplex", "conditions": [_when("link", "is", True), _when("full_duplex", "is", False)],
     "look": {"color": "#ff9f0a", "style": "pulse"}},
    {"name": "Busy", "conditions": [_when("traffic", "ge", 500)],
     "look": {"color": "#ff453a", "style": "pulse", "rate": 2}},
    {"name": "Night dim", "conditions": [_when("time", "between", ["22:00", "07:00"])],
     "look": {"style": "effect", "brightness": 15}},
    {"name": "Weekend", "conditions": [_when("weekday", "in", ["sat", "sun"])],
     "look": {"style": "effect", "brightness": 60}},
]

# What a fresh install looks like: white when online, red when offline, amber blink on an anomaly.
DEFAULT_RULES: list[dict] = [TEMPLATES[1], TEMPLATES[0]]
