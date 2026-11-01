"""Effect framework: self-describing parameters so a UI can build its controls from `/api/effects`."""

from __future__ import annotations

import math
import random
from dataclasses import dataclass, field
from datetime import datetime
from typing import Any, ClassVar

from ..color import Color
from ..links import PortLink

PARAM_TYPES = ("color", "float", "int", "bool", "choice")


def hash01(i: int, seed: int) -> float:
    x = (i * 374761393 + seed * 668265263) & 0xFFFFFFFF
    x = ((x ^ (x >> 13)) * 1274126177) & 0xFFFFFFFF
    return (x ^ (x >> 16)) / 0xFFFFFFFF


def color_seed(key: str) -> int:
    seed = 2166136261
    for value in key.encode("utf-8"):
        seed = ((seed ^ value) * 16777619) & 0xFFFFFFFF
    return seed


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
    randomizes: str | None = None
    dims: str | None = None

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
        except (TypeError, ValueError, OverflowError) as exc:
            raise ValueError(f"{self.key} must be a number") from exc
        if not math.isfinite(number):
            raise ValueError(f"{self.key} must be a finite number")
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
            "random_param": f"random_{self.key}" if self.type == "color" else None,
            "brightness_param": f"brightness_{self.key}" if self.type == "color" else None,
            "randomizes": self.randomizes,
            "dims": self.dims,
        }
        return {k: v for k, v in schema.items() if v is not None}


COLOR_TRANSITION = Param("color_transition", "Color transition", "float", 8.0,
                         min=1.0, max=30.0, step=0.5, unit="s")


@dataclass
class RenderContext:
    """Live data available to effects. Port numbers are 1-based and local to the zone being rendered."""

    num_ports: int
    links: dict[int, PortLink] = field(default_factory=dict)
    traffic_bps: dict[int, float] = field(default_factory=dict)
    now: datetime = field(default_factory=datetime.now)
    poe_power_w: dict[int, float] = field(default_factory=dict)


class Effect:
    """Base class. Subclasses set the class attributes and implement `render`."""

    id: ClassVar[str]
    name: ClassVar[str]
    description: ClassVar[str] = ""
    category: ClassVar[str] = "animated"  # "static", "animated", "time" or "data"
    params: ClassVar[tuple[Param, ...]] = ()
    exclusive_params: ClassVar[tuple[tuple[str, str], ...]] = ()

    def __init__(self, num_ports: int, params: dict | None = None, *, seed: int | None = None) -> None:
        if num_ports < 1:
            raise ValueError("num_ports must be at least 1")
        self.num_ports = num_ports
        self.rng = random.Random(seed)
        self.p: dict[str, Any] = {}
        self._phase_offset = 0.0
        self._phase_rate: float | None = None
        self._color_phase_offset = 0.0
        self._color_phase_rate: float | None = None
        self.update(params or {}, reset=True)
        self.setup()

    # Parameters
    @classmethod
    def parameters(cls) -> tuple[Param, ...]:
        colors = [p for p in cls.params if p.type == "color"]
        if not colors:
            return cls.params
        generated = tuple(Param(f"random_{p.key}", f"Randomize {p.label.lower()}", "bool", False,
                                randomizes=p.key) for p in colors)
        generated += tuple(Param(f"brightness_{p.key}", f"{p.label} brightness", "float", 100.0,
                                 min=0.0, max=100.0, step=1.0, unit="%", dims=p.key) for p in colors)
        keys = {p.key for p in cls.params}
        if "color_transition" in keys or any(p.key in keys for p in generated):
            raise ValueError(f"{cls.id} declares a reserved color-source parameter")
        return cls.params + generated + (COLOR_TRANSITION,)

    @classmethod
    def param_map(cls) -> dict[str, Param]:
        return {p.key: p for p in cls.parameters()}

    def update(self, params: dict, *, reset: bool = False) -> None:
        if not isinstance(params, dict):
            raise ValueError("effect parameters must be an object")
        spec = self.param_map()
        unknown = set(params) - set(spec)
        if unknown:
            raise ValueError(f"unknown parameter(s) for {self.id}: {', '.join(sorted(unknown))}")
        values = {k: p.coerce(p.default) for k, p in spec.items()}
        if not reset:
            values.update(self.p)
        for key, value in params.items():
            values[key] = spec[key].coerce(value)
        for left, right in self.exclusive_params:
            if values.get(left) and values.get(right):
                if left in params and right not in params:
                    values[right] = False
                elif right in params and left not in params:
                    values[left] = False
                else:
                    raise ValueError(f"{left} and {right} cannot both be enabled")
        self.p = values

    def phase(self, t: float, rate: float) -> float:
        """Keep the current position when an animation rate changes, including a pause at zero."""
        if self._phase_rate is not None and rate != self._phase_rate:
            self._phase_offset += t * (self._phase_rate - rate)
        self._phase_rate = rate
        return self._phase_offset + t * rate

    def animation_rate(self) -> float | None:
        if "period" in self.p:
            return 1 / self.p["period"]
        if "speed" in self.p:
            return self.p["speed"]
        if self.id == "strobe":
            return self.p["rate"]
        return None

    def update_at(self, params: dict, t: float) -> None:
        previous = self.animation_rate()
        previous_color_period = self.p.get("color_transition", 8.0)
        self.update(params)
        current = self.animation_rate()
        if previous is not None and current is not None:
            self.phase(t, previous)
            self.phase(t, current)
        current_color_period = self.p.get("color_transition", 8.0)
        if previous_color_period != current_color_period:
            self.color_phase(t, previous_color_period)
            self.color_phase(t, current_color_period)

    def color_phase(self, t: float, period: float) -> float:
        rate = 1 / period
        previous = getattr(self, "_color_phase_rate", None)
        offset = getattr(self, "_color_phase_offset", 0.0)
        if previous is not None and rate != previous:
            offset += t * (previous - rate)
        self._color_phase_offset, self._color_phase_rate = offset, rate
        return offset + t * rate

    def colors(self, t: float) -> dict[str, Color]:
        """Resolve color sources without changing the saved fixed colors or movement phase."""
        colors = {p.key: self.p[p.key] for p in self.params if p.type == "color"}
        randomized = [key for key in colors if self.p.get(f"random_{key}", False)]
        if randomized:
            phase = self.color_phase(t, self.p.get("color_transition", 8.0))
            index = math.floor(phase)
            fraction = phase - index
            eased = fraction * fraction * (3 - 2 * fraction)
            for key in randomized:
                seed = color_seed(key)
                start, end = hash01(index, seed), hash01(index + 1, seed)
                distance = (end - start + 0.5) % 1 - 0.5
                colors[key] = Color.from_hsv(start + distance * eased, 0.85)
        return {key: self.adjust_color(key, color) for key, color in colors.items()}

    def adjust_color(self, key: str, color: Color) -> Color:
        """Scale a resolved color before effect blending; retain the chosen source color."""
        brightness = self.p.get(f"brightness_{key}", 100.0)
        return color if brightness == 100 else color.scaled(brightness / 100)

    def params_json(self) -> dict[str, Any]:
        return {k: (v.to_hex() if isinstance(v, Color) else v) for k, v in self.p.items()}

    @classmethod
    def describe(cls) -> dict:
        description = {
            "id": cls.id,
            "name": cls.name,
            "description": cls.description,
            "category": cls.category,
            "params": [p.to_schema() for p in cls.parameters()],
        }
        if cls.exclusive_params:
            description["exclusive_params"] = [list(pair) for pair in cls.exclusive_params]
        return description

    # Rendering
    def setup(self) -> None:
        """Initialize per-instance state (called once after the first parameters are set)."""

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        """Return one color per port (index 0 = port 1) for time `t` seconds since the effect started."""
        raise NotImplementedError
