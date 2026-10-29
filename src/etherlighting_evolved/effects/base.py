"""Effect framework: self-describing parameters so a UI can build its controls from `/api/effects`."""

from __future__ import annotations

import random
from dataclasses import dataclass, field
from datetime import datetime
from typing import Any, ClassVar

from ..color import Color
from ..links import PortLink

PARAM_TYPES = ("color", "float", "int", "bool", "choice")


@dataclass(frozen=True)
class Param:
    """One user-adjustable setting of an effect.

    type: "color" (value is "#rrggbb"), "float", "int", "bool" or "choice" (one of `choices`).
    Numbers are clamped to min/max; `step` and `unit` are hints for UI sliders.
    """

    key: str
    label: str
    type: str
    default: Any
    min: float | None = None
    max: float | None = None
    step: float | None = None
    choices: tuple[str, ...] | None = None
    unit: str | None = None
    description: str = ""

    def __post_init__(self) -> None:
        if self.type not in PARAM_TYPES:
            raise ValueError(f"unknown param type {self.type!r}")
        if self.type == "choice" and not self.choices:
            raise ValueError(f"choice param {self.key!r} needs choices")

    def coerce(self, value: Any) -> Any:
        if self.type == "color":
            return Color.parse(value)
        if self.type == "bool":
            if isinstance(value, str):
                return value.strip().lower() in ("1", "true", "yes", "on")
            return bool(value)
        if self.type == "choice":
            text = str(value)
            if text not in self.choices:
                raise ValueError(f"{self.key} must be one of {', '.join(self.choices)}")
            return text
        try:
            number = float(value)
        except (TypeError, ValueError) as exc:
            raise ValueError(f"{self.key} must be a number") from exc
        if number != number:  # NaN
            raise ValueError(f"{self.key} must be a number")
        if self.min is not None:
            number = max(self.min, number)
        if self.max is not None:
            number = min(self.max, number)
        return int(round(number)) if self.type == "int" else number

    def to_schema(self) -> dict:
        schema = {
            "key": self.key,
            "label": self.label,
            "type": self.type,
            "default": self.default.to_hex() if isinstance(self.default, Color) else self.default,
            "min": self.min,
            "max": self.max,
            "step": self.step,
            "choices": list(self.choices) if self.choices else None,
            "unit": self.unit,
            "description": self.description or None,
        }
        return {k: v for k, v in schema.items() if v is not None}


@dataclass
class RenderContext:
    """Live data available to effects. Port numbers are 1-based and local to the zone being rendered."""

    num_ports: int
    links: dict[int, PortLink] = field(default_factory=dict)
    traffic_bps: dict[int, float] = field(default_factory=dict)
    now: datetime = field(default_factory=datetime.now)


class Effect:
    """Base class. Subclasses set the class attributes and implement `render`."""

    id: ClassVar[str]
    name: ClassVar[str]
    description: ClassVar[str] = ""
    category: ClassVar[str] = "animated"  # "static", "animated", "time" or "data"
    params: ClassVar[tuple[Param, ...]] = ()

    def __init__(self, num_ports: int, params: dict | None = None, *, seed: int | None = None) -> None:
        if num_ports < 1:
            raise ValueError("num_ports must be at least 1")
        self.num_ports = num_ports
        self.rng = random.Random(seed)
        self.p: dict[str, Any] = {}
        self.update(params or {}, reset=True)
        self.setup()

    # Parameters
    @classmethod
    def param_map(cls) -> dict[str, Param]:
        return {p.key: p for p in cls.params}

    def update(self, params: dict, *, reset: bool = False) -> None:
        spec = self.param_map()
        unknown = set(params) - set(spec)
        if unknown:
            raise ValueError(f"unknown parameter(s) for {self.id}: {', '.join(sorted(unknown))}")
        values = {k: p.coerce(p.default) for k, p in spec.items()} if reset else dict(self.p)
        for key, value in params.items():
            values[key] = spec[key].coerce(value)
        self.p = values

    def params_json(self) -> dict[str, Any]:
        return {k: (v.to_hex() if isinstance(v, Color) else v) for k, v in self.p.items()}

    @classmethod
    def describe(cls) -> dict:
        return {
            "id": cls.id,
            "name": cls.name,
            "description": cls.description,
            "category": cls.category,
            "params": [p.to_schema() for p in cls.params],
        }

    # Rendering
    def setup(self) -> None:
        """Initialize per-instance state (called once after the first parameters are set)."""

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        """Return one color per port (index 0 = port 1) for time `t` seconds since the effect started."""
        raise NotImplementedError
