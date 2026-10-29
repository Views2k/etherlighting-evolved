"""Color handling: parsing user input, HSV helpers and blending."""

from __future__ import annotations

import colorsys
import re
from dataclasses import dataclass
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
    """An 8-bit RGB color. Immutable and hashable."""

    r: int
    g: int
    b: int

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
        s = min(1.0, max(0.0, saturation))
        v = min(1.0, max(0.0, value))
        r, g, b = colorsys.hsv_to_rgb(hue % 1.0, s, v)
        return cls(round(r * 255), round(g * 255), round(b * 255))

    # Conversion
    def to_hex(self) -> str:
        return f"#{self.r:02x}{self.g:02x}{self.b:02x}"

    def to_hsv(self) -> tuple[float, float, float]:
        return colorsys.rgb_to_hsv(self.r / 255, self.g / 255, self.b / 255)

    @property
    def max_channel(self) -> int:
        return max(self.r, self.g, self.b)

    @property
    def is_off(self) -> bool:
        return self.max_channel == 0

    # Math
    def scaled(self, factor: float) -> Color:
        f = max(0.0, factor)
        return Color(*(min(255, round(c * f)) for c in (self.r, self.g, self.b)))

    def lerp(self, other: Color, t: float) -> Color:
        t = min(1.0, max(0.0, t))
        return Color(
            round(self.r + (other.r - self.r) * t),
            round(self.g + (other.g - self.g) * t),
            round(self.b + (other.b - self.b) * t),
        )

    def __str__(self) -> str:
        return self.to_hex()


BLACK = Color(0, 0, 0)
WHITE = Color(255, 255, 255)
RED = Color(255, 0, 0)
