"""PoE power use shown with an elapsed-time color fade."""

from __future__ import annotations

import math

from ..color import Color
from .base import Effect, Param, RenderContext


class PoeUsage(Effect):
    id = "poe_usage"
    name = "PoE Usage"
    category = "data"
    description = "Blend from low to high color as each port supplies more PoE power."
    params = (
        Param("low_color", "Low power", "color", "#4da6ff"),
        Param("high_color", "High power", "color", "#ef78c7"),
        Param("no_power_color", "No power", "color", "#111827"),
        Param("speed", "Fade speed", "float", 1.0, min=0.1, max=5.0, step=0.05, unit="x"),
    )

    def setup(self) -> None:
        self._weights = [(0.0, 0.0)] * self.num_ports
        self._targets = [(0.0, 0.0)] * self.num_ports
        self._last: float | None = None

    def _advance(self, t: float, speed: float) -> None:
        dt = 0.0 if self._last is None else max(0.0, t - self._last)
        self._last = t
        alpha = -math.expm1(-dt * speed / 0.6)
        self._weights = [tuple(old + (target - old) * alpha for old, target in zip(weights, targets, strict=True))
                         for weights, targets in zip(self._weights, self._targets, strict=True)]

    def update_at(self, params: dict, t: float) -> None:
        previous_speed = self.p["speed"]
        super().update_at(params, t)
        self._advance(t, previous_speed)

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        self._advance(t, self.p["speed"])
        targets = []
        for port in range(1, self.num_ports + 1):
            watts = ctx.poe_power_w.get(port)
            if type(watts) in (int, float) and math.isfinite(watts) and watts > 0:
                load = min(1.0, math.log1p(watts) / math.log1p(60.0))
                targets.append((1.0 - load, load))
            else:
                targets.append((0.0, 0.0))
        self._targets = targets
        colors = self.colors(t)
        result = []
        for low, high in self._weights:
            powered = min(1.0, max(0.0, low + high))
            active = colors["low_color"].lerp(colors["high_color"], high / powered if powered > 0 else 0.0)
            result.append(colors["no_power_color"].lerp(active, powered))
        return result
