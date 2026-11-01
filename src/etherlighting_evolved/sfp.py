"""SFP indicator settings, synchronization, and continuous color effects."""

from __future__ import annotations

import math
from dataclasses import dataclass
from typing import Any

from .color import BLACK, WHITE, Color

EFFECTS = ("steady", "pulse", "color_cycle", "two_color")
NATIVE_PERIODS = {"pulse": (2, 4, 6), "two_color": (2, 4, 6), "color_cycle": (6, 12, 18)}


@dataclass(frozen=True)
class SfpSync:
    mode: str = "independent"
    source_port: int = 1

    def __post_init__(self) -> None:
        if self.mode not in ("independent", "together", "ethernet"):
            raise ValueError("SFP synchronization must be independent, together, or ethernet")
        if type(self.source_port) is not int or not 1 <= self.source_port <= 64:
            raise ValueError("SFP source port must be a valid Ethernet port number")

    def to_dict(self) -> dict:
        return {"mode": self.mode, "source_port": self.source_port}

    def updated(self, changes: dict) -> SfpSync:
        if not isinstance(changes, dict) or set(changes) - {"mode", "source_port"}:
            raise ValueError("invalid SFP synchronization settings")
        return SfpSync(**{**self.to_dict(), **changes})


def _color(value: Any) -> Color:
    if isinstance(value, (Color, str)):
        return Color.parse(value)
    if isinstance(value, dict):
        if set(value) != {"r", "g", "b"}:
            raise ValueError("SFP colors require red, green, and blue channels")
        channels = (value["r"], value["g"], value["b"])
    elif isinstance(value, (list, tuple)) and len(value) == 3:
        channels = value
    else:
        raise ValueError("invalid SFP color")
    if any(type(channel) is not int or not 0 <= channel <= 255 for channel in channels):
        raise ValueError("SFP color channels must be integers from 0 to 255")
    return Color(*channels)


@dataclass(frozen=True)
class SfpLight:
    mode: str = "unmanaged"
    color: Color = WHITE
    brightness: int = 100
    effect: str = "steady"
    period: float = 6.0
    secondary_color: Color = Color(0, 111, 255)
    color_brightness: int = 100
    secondary_brightness: int = 100

    def __post_init__(self) -> None:
        if self.mode not in ("unmanaged", "color", "off"):
            raise ValueError("SFP mode must be unmanaged, color, or off")
        if self.effect not in EFFECTS:
            raise ValueError("invalid SFP lighting effect")
        if not isinstance(self.color, Color) or not isinstance(self.secondary_color, Color):
            raise ValueError("SFP settings require parsed colors")
        for value in (self.brightness, self.color_brightness, self.secondary_brightness):
            if type(value) is not int or not 0 <= value <= 100:
                raise ValueError("SFP brightness must be an integer from 0 to 100")
        if type(self.period) not in (int, float) or not 2 <= self.period <= 30:
            raise ValueError("SFP period must be a number from 2 to 30 seconds")

    def to_dict(self) -> dict:
        return {
            "mode": self.mode, "color": self.color.to_hex(), "brightness": self.brightness,
            "effect": self.effect, "period": self.period, "secondary_color": self.secondary_color.to_hex(),
            "color_brightness": self.color_brightness, "secondary_brightness": self.secondary_brightness,
        }

    def updated(self, changes: dict) -> SfpLight:
        values = self.to_dict()
        if not isinstance(changes, dict) or set(changes) - set(values):
            raise ValueError("invalid SFP lighting settings")
        values.update(changes)
        values["color"] = _color(values["color"])
        values["secondary_color"] = _color(values["secondary_color"])
        return SfpLight(**values)

    def native_anchors(self) -> tuple[Color, ...]:
        primary = self.color.scaled(self.color_brightness / 100)
        if self.effect == "pulse":
            return primary, BLACK
        if self.effect == "two_color":
            return primary, self.secondary_color.scaled(self.secondary_brightness / 100)
        if self.effect == "color_cycle":
            hue, saturation, value = self.color.to_hsv()
            return tuple(Color.from_hsv(hue + index / 6, max(0.75, saturation), value)
                         .scaled(self.color_brightness / 100) for index in range(6))
        return (primary,)

    @property
    def native_duration(self) -> int | None:
        if self.mode != "color" or self.period not in NATIVE_PERIODS.get(self.effect, ()):
            return None
        return int(self.period / len(self.native_anchors()))

    def render(self, t: float, port: int) -> Color:
        """Render color brightness only; the engine applies master and per-SFP brightness."""
        if type(port) is not int or not 1 <= port <= 64:
            raise ValueError("SFP effects require a valid port number")
        if type(t) not in (int, float):
            raise ValueError("SFP effect time must be finite")
        try:
            t = float(t)
        except OverflowError as exc:
            raise ValueError("SFP effect time must be finite") from exc
        if not math.isfinite(t):
            raise ValueError("SFP effect time must be finite")
        if self.mode != "color":
            return BLACK
        primary = self.color.scaled(self.color_brightness / 100)
        if self.effect == "steady":
            return primary
        phase = (t / self.period) % 1.0
        blend = (1 - math.cos(math.tau * phase)) / 2
        if self.effect == "pulse":
            return primary.scaled(1 - blend)
        if self.effect == "two_color":
            secondary = self.secondary_color.scaled(self.secondary_brightness / 100)
            return primary.lerp(secondary, blend)
        hue, saturation, value = self.color.to_hsv()
        sector = ((hue + phase) % 1.0) * 6
        fraction = sector % 1.0
        eased = fraction * fraction * (3 - 2 * fraction)
        hue = (math.floor(sector) + eased) / 6
        return Color.from_hsv(hue, max(0.75, saturation), value).scaled(self.color_brightness / 100)
