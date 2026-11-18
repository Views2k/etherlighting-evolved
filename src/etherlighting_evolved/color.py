"""Color handling: parsing user input, HSV helpers and blending."""

from __future__ import annotations

import colorsys
import math
import re
from dataclasses import dataclass, field
from typing import Any

_HEX_RE = re.compile(r"^#?(?:[0-9a-f]{3}|[0-9a-f]{6})$")

NAMED_COLORS: dict[str, tuple[int, int, int]] = {
    "off": (0, 0, 0),
    "black": (0, 0, 0),
    "white": (255, 255, 255),
    "warmwhite": (255, 180, 100),
    "red": (255, 0, 0),
    "green": (0, 255, 0),
    "blue": (0, 0, 255),
    "yellow": (255, 220, 0),
    "orange": (255, 120, 0),
    "cyan": (0, 255, 255),
    "magenta": (255, 0, 255),
    "purple": (150, 0, 255),
    "pink": (255, 60, 160),
    "teal": (0, 180, 160),
    "ubiquiti": (0, 111, 255),
}


@dataclass(frozen=True, slots=True)
class Color:
    """An immutable 8-bit RGB color with optional precision retained for output encoding."""

    r: int
    g: int
    b: int
    _precise: tuple[float, float, float] | None = field(default=None, init=False, compare=False, repr=False)

    def __post_init__(self) -> None:
        for name in ("r", "g", "b"):
            value = getattr(self, name)
            if isinstance(value, bool) or not isinstance(value, int) or not 0 <= value <= 255:
                raise ValueError(f"color channel {name} must be an integer 0-255, got {value!r}")

    # Construction
    @classmethod
    def parse(cls, value: Any) -> Color:
        """Accepts '#ff8800', 'ff8800', '#f80', a color name, [r, g, b], (r, g, b) or {'r','g','b'}."""
        if isinstance(value, Color):
            return value
        if isinstance(value, str):
            text = value.strip().lower().replace(" ", "").replace("_", "")
            if text in NAMED_COLORS:
                return cls(*NAMED_COLORS[text])
            if _HEX_RE.match(text):
                digits = text.lstrip("#")
                if len(digits) == 3:
                    digits = "".join(ch * 2 for ch in digits)
                return cls(int(digits[0:2], 16), int(digits[2:4], 16), int(digits[4:6], 16))
            raise ValueError(f"unrecognized color {value!r} (use #rrggbb, #rgb or a name)")
        try:
            if isinstance(value, dict):
                return cls(int(value["r"]), int(value["g"]), int(value["b"]))
            if isinstance(value, (list, tuple)) and len(value) == 3:
                return cls(*(int(v) for v in value))
        except (KeyError, TypeError) as exc:
            raise ValueError(f"unrecognized color {value!r}") from exc
        raise ValueError(f"unrecognized color {value!r}")

    @classmethod
    def from_hsv(cls, hue: float, saturation: float = 1.0, value: float = 1.0) -> Color:
        """hue wraps around 0..1; saturation and value are clamped to 0..1."""
        if not all(math.isfinite(component) for component in (hue, saturation, value)):
            raise ValueError("HSV components must be finite")
        s = min(1.0, max(0.0, saturation))
        v = min(1.0, max(0.0, value))
        r, g, b = colorsys.hsv_to_rgb(hue % 1.0, s, v)
        precise = (r * 255, g * 255, b * 255)
        return cls._with_precision(tuple(round(channel) for channel in precise), precise)

    @classmethod
    def _with_precision(cls, rgb: tuple[int, int, int], precise: tuple[float, float, float]) -> Color:
        if len(precise) != 3 or any(isinstance(value, bool) or not isinstance(value, (int, float))
                                   or not 0 <= value <= 255 or not math.isfinite(value) for value in precise):
            raise ValueError("precise color channels must be finite numbers from 0 to 255")
        color = cls(*rgb)
        object.__setattr__(color, "_precise", tuple(float(value) for value in precise))
        return color

    # Conversion
    def to_hex(self) -> str:
        return f"#{self.r:02x}{self.g:02x}{self.b:02x}"

    def to_hsv(self) -> tuple[float, float, float]:
        return colorsys.rgb_to_hsv(self.r / 255, self.g / 255, self.b / 255)

    @property
    def precise_channels(self) -> tuple[float, float, float]:
        """Unrounded components in the same 0..255 scale as the public RGB channels."""
        precise = getattr(self, "_precise", None)
        return (float(self.r), float(self.g), float(self.b)) if precise is None else precise

    @property
    def max_channel(self) -> int:
        return max(self.r, self.g, self.b)

    @property
    def is_off(self) -> bool:
        return self.max_channel == 0

    # Math
    def scaled(self, factor: float) -> Color:
        if not math.isfinite(factor):
            raise ValueError("color scale must be finite")
        f = max(0.0, factor)
        rgb = tuple(round(min(255, c * f)) for c in (self.r, self.g, self.b))
        precise = tuple(min(255.0, c * f) for c in self.precise_channels)
        return Color._with_precision(rgb, precise)

    def lerp(self, other: Color, t: float) -> Color:
        if not math.isfinite(t):
            raise ValueError("color blend must be finite")
        t = min(1.0, max(0.0, t))
        rgb = (
            round(self.r + (other.r - self.r) * t),
            round(self.g + (other.g - self.g) * t),
            round(self.b + (other.b - self.b) * t),
        )
        precise = tuple(min(255.0, max(0.0, start + (end - start) * t))
                        for start, end in zip(self.precise_channels, other.precise_channels, strict=True))
        return Color._with_precision(rgb, precise)

    def __str__(self) -> str:
        return self.to_hex()


BLACK = Color(0, 0, 0)
WHITE = Color(255, 255, 255)
