"""Turn a composed color and brightness into what the LED controller receives and what the clients display."""

from __future__ import annotations

from .color import Color
from .switch import PortOutput


def to_output(color: Color, brightness: float, gamma: float = 2.2, *, precise: bool = False) -> PortOutput:
    """Turn a color and a brightness into what the LED controller receives.

    Apply the configured gamma curve to both color and effective brightness. gamma=1 disables the curve.
    All brightness changes are carried in the color bytes; the hardware level stays at 100, including black.
    Level-only updates did not reproduce effect fades on the Pro XG 8 PoE.
    """
    if precise:
        scale = (max(0.0, min(100.0, brightness)) / 100) ** gamma
        channels = tuple(round(65280 * (c / 255) ** gamma * scale) for c in color.precise_channels)
        preview = Color(*(round(c / 256) for c in channels))
        return PortOutput(preview, 100, channels)
    if brightness <= 0 or color.is_off:
        return PortOutput.off()
    scale = (min(100.0, brightness) / 100) ** gamma
    rgb = Color(*(round(255 * (c / 255) ** gamma * scale) for c in (color.r, color.g, color.b)))
    return PortOutput.off() if rgb.is_off else PortOutput(rgb, 100)


def display_rgb(color: Color, brightness: float) -> list[float]:
    """Display the composed color before the switch's output transfer curve."""
    scale = max(0.0, min(100.0, brightness)) / 100
    return [channel * scale for channel in color.precise_channels]
