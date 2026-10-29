"""Built-in effects: 15 lighting effects and a traffic meter.

Each effect returns one color per port in its zone. Colors based on port status (offline, PoE, anomaly, ...) come
from rules, which are applied after the effect.
"""

from __future__ import annotations

import itertools
import math

from ..color import BLACK, Color
from .base import Effect, Param, RenderContext


def pulse(phase: float) -> float:
    """0 -> 1 -> 0 over one unit of phase, with soft ends."""
    return 0.5 - 0.5 * math.cos(2 * math.pi * phase)


def triangle(x: float) -> float:
    """0 at 0, 1 at 0.5, back to 0 at 1, repeating."""
    return 1.0 - abs(2.0 * (x % 1.0) - 1.0)


def bounce(position: float, length: int) -> tuple[float, int]:
    """A position traveling back and forth over 0..length-1, and which way it's heading."""
    if length <= 1:
        return 0.0, 1
    period = 2 * (length - 1)
    x = position % period
    return (x, 1) if x <= length - 1 else (period - x, -1)


def hash01(i: int, seed: int) -> float:
    x = (i * 374761393 + seed * 668265263) & 0xFFFFFFFF
    x = ((x ^ (x >> 13)) * 1274126177) & 0xFFFFFFFF
    return (x ^ (x >> 16)) / 0xFFFFFFFF


def noise(x: float, seed: int) -> float:
    """Smooth 1-D value noise between 0 and 1."""
    i = math.floor(x)
    f = x - i
    f = f * f * (3 - 2 * f)
    a, b = hash01(i, seed), hash01(i + 1, seed)
    return a + (b - a) * f


def gradient_at(stops: tuple[tuple[float, Color], ...], x: float) -> Color:
    x = min(1.0, max(0.0, x))
    for (p0, c0), (p1, c1) in itertools.pairwise(stops):
        if x <= p1:
            return c0.lerp(c1, (x - p0) / (p1 - p0) if p1 > p0 else 1.0)
    return stops[-1][1]


class Static(Effect):
    id = "static"
    name = "Static"
    category = "static"
    description = "One color on every port."
    params = (Param("color", "Color", "color", "#ffffff"),)

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        return [self.p["color"]] * self.num_ports


class Breathing(Effect):
    id = "breathing"
    name = "Breathing"
    description = "Fades in and out."
    params = (
        Param("color", "Color", "color", "#0a84ff"),
        Param("period", "Breath length", "float", 4.0, min=0.5, max=20.0, step=0.5, unit="s"),
        Param("floor", "Lowest brightness", "float", 0.05, min=0.0, max=1.0, step=0.05),
        Param("ripple", "Ripple across ports", "bool", False),
    )

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        floor, period = self.p["floor"], self.p["period"]
        colors = []
        for i in range(self.num_ports):
            offset = i / self.num_ports if self.p["ripple"] else 0.0
            colors.append(self.p["color"].scaled(floor + (1 - floor) * pulse(t / period + offset)))
        return colors


class ColorCycle(Effect):
    id = "color_cycle"
    name = "Color Cycle"
    description = "All ports move through the color wheel together."
    params = (
        Param("speed", "Speed", "float", 0.1, min=0.01, max=2.0, step=0.01, unit="cycles/s"),
        Param("saturation", "Saturation", "float", 1.0, min=0.0, max=1.0, step=0.05),
    )

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        return [Color.from_hsv(t * self.p["speed"], self.p["saturation"])] * self.num_ports


class RainbowWave(Effect):
    id = "rainbow"
    name = "Rainbow Wave"
    description = "A rainbow that moves along the ports."
    params = (
        Param("speed", "Speed", "float", 0.2, min=0.0, max=3.0, step=0.05, unit="cycles/s"),
        Param("spread", "Spread", "float", 1.0, min=0.1, max=3.0, step=0.05,
              description="How much of the color wheel fits across the ports."),
        Param("saturation", "Saturation", "float", 1.0, min=0.0, max=1.0, step=0.05),
        Param("reverse", "Reverse", "bool", False),
    )

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        start = (-1 if self.p["reverse"] else 1) * t * self.p["speed"]
        step = self.p["spread"] / self.num_ports
        return [Color.from_hsv(start + i * step, self.p["saturation"]) for i in range(self.num_ports)]


class Comet(Effect):
    id = "comet"
    name = "Comet"
    description = "A bright point with a fading tail that moves along the ports."
    params = (
        Param("color", "Color", "color", "#64d2ff"),
        Param("background", "Background", "color", "#000000"),
        Param("speed", "Speed", "float", 6.0, min=0.5, max=30.0, step=0.5, unit="ports/s"),
        Param("tail", "Tail", "int", 3, min=0, max=7, step=1, unit="ports"),
        Param("bounce", "Bounce", "bool", False),
    )

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        n, span = self.num_ports, self.p["tail"] + 1
        travel = t * self.p["speed"]
        head, heading = bounce(travel, n) if self.p["bounce"] else (travel % n, 1)

        colors = []
        for i in range(n):
            if self.p["bounce"]:
                behind = (head - i) * heading
            else:
                behind = (head - i) % n
                if behind > n - 1:  # the head is wrapping round onto this port
                    behind -= n
            # In front of the head it drops off within a port; behind, it fades over the tail.
            glow = max(0.0, 1.0 + behind) if behind < 0 else max(0.0, 1.0 - behind / span)
            colors.append(self.p["background"].lerp(self.p["color"], glow))
        return colors


class Scanner(Effect):
    id = "scanner"
    name = "Scanner"
    description = "A light that sweeps back and forth."
    params = (
        Param("color", "Color", "color", "#ff453a"),
        Param("background", "Background", "color", "#000000"),
        Param("speed", "Speed", "float", 5.0, min=0.5, max=30.0, step=0.5, unit="ports/s"),
        Param("width", "Width", "float", 1.5, min=0.5, max=4.0, step=0.1, unit="ports"),
    )

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        head, _ = bounce(t * self.p["speed"], self.num_ports)
        width = self.p["width"]
        return [
            self.p["background"].lerp(self.p["color"], max(0.0, 1.0 - abs(i - head) / width))
            for i in range(self.num_ports)
        ]


class Marquee(Effect):
    id = "marquee"
    name = "Marquee"
    description = "Every second, third or fourth port lit, stepping along."
    params = (
        Param("color", "Color", "color", "#ffd60a"),
        Param("background", "Background", "color", "#000000"),
        Param("speed", "Speed", "float", 4.0, min=0.5, max=20.0, step=0.5, unit="steps/s"),
        Param("spacing", "Spacing", "int", 3, min=2, max=4, step=1, unit="ports"),
    )

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        step = int(t * self.p["speed"])
        lit, unlit = self.p["color"], self.p["background"]
        return [lit if (i - step) % self.p["spacing"] == 0 else unlit for i in range(self.num_ports)]


class StarryNight(Effect):
    id = "starry_night"
    name = "Starry Night"
    description = "Random ports flash and fade out."
    params = (
        Param("color", "Color", "color", "#ffffff"),
        Param("background", "Background", "color", "#000000"),
        Param("rainbow", "Random colors", "bool", False),
        Param("rate", "Sparkles", "float", 0.8, min=0.05, max=5.0, step=0.05, unit="per port/s"),
        Param("fade", "Fade", "float", 0.6, min=0.1, max=3.0, step=0.1, unit="s"),
    )

    def setup(self) -> None:
        self._glow = [0.0] * self.num_ports
        self._hue = [0.0] * self.num_ports
        self._last: float | None = None

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        dt = 0.0 if self._last is None else min(0.5, max(0.0, t - self._last))
        self._last = t
        colors = []
        for i in range(self.num_ports):
            glow = max(0.0, self._glow[i] - dt / self.p["fade"])
            if self.rng.random() < self.p["rate"] * dt:
                glow = 1.0
                self._hue[i] = self.rng.random()
            self._glow[i] = glow
            star = Color.from_hsv(self._hue[i]) if self.p["rainbow"] else self.p["color"]
            colors.append(self.p["background"].lerp(star, glow))
        return colors


FIRE_PALETTES: dict[str, tuple[tuple[float, Color], ...]] = {
    "fire": ((0.0, Color(20, 0, 0)), (0.35, Color(200, 10, 0)), (0.7, Color(255, 90, 0)), (1.0, Color(255, 200, 60))),
    "ice": ((0.0, Color(0, 0, 30)), (0.4, Color(0, 40, 200)), (0.75, Color(0, 160, 255)), (1.0, Color(200, 240, 255))),
    "toxic": ((0.0, Color(0, 20, 0)), (0.4, Color(0, 140, 10)), (0.75, Color(90, 255, 0)), (1.0, Color(220, 255, 120))),
    "plasma": (
        (0.0, Color(30, 0, 40)), (0.4, Color(150, 0, 200)), (0.75, Color(255, 0, 140)), (1.0, Color(255, 180, 240)),
    ),
}


class Fire(Effect):
    id = "fire"
    name = "Fire"
    description = "Each port flickers on its own."
    params = (
        Param("palette", "Palette", "choice", "fire", choices=tuple(FIRE_PALETTES)),
        Param("intensity", "Intensity", "float", 0.85, min=0.1, max=1.0, step=0.05),
        Param("speed", "Flicker", "float", 1.0, min=0.1, max=4.0, step=0.1),
    )

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        stops = FIRE_PALETTES[self.p["palette"]]
        speed = self.p["speed"]
        colors = []
        for i in range(self.num_ports):
            slow = noise(t * speed * 4 + i * 7.31, seed=i + 1)
            fast = noise(t * speed * 11 + i * 3.7, seed=100 + i)
            colors.append(gradient_at(stops, self.p["intensity"] * (0.25 + 0.55 * slow + 0.2 * fast)))
        return colors


class Police(Effect):
    id = "police"
    name = "Police"
    description = "Alternating flashes in two colors."
    params = (
        Param("color_a", "Left color", "color", "#ff0000"),
        Param("color_b", "Right color", "color", "#0040ff"),
        Param("speed", "Speed", "float", 1.5, min=0.25, max=5.0, step=0.25, unit="cycles/s"),
        Param("pattern", "Pattern", "choice", "split", choices=("split", "alternate", "together")),
    )

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        phase = (t * self.p["speed"]) % 1.0
        first_half = phase < 0.5
        within = (phase % 0.5) / 0.5
        double_flash = within < 0.3 or 0.45 <= within < 0.75
        a, b, n = self.p["color_a"], self.p["color_b"], self.num_ports

        colors = []
        for i in range(n):
            if self.p["pattern"] == "split":
                left = i < n // 2
                on = double_flash and (first_half == left)
                colors.append((a if left else b) if on else BLACK)
            elif self.p["pattern"] == "alternate":
                if i % 2 == 0:
                    colors.append(a if first_half else BLACK)
                else:
                    colors.append(BLACK if first_half else b)
            else:
                colors.append((a if first_half else b) if double_flash else BLACK)
        return colors


class Strobe(Effect):
    id = "strobe"
    name = "Strobe"
    description = "Fast flashes. Can affect people who are sensitive to flashing light."
    params = (
        Param("color", "Color", "color", "#ffffff"),
        Param("rate", "Rate", "float", 4.0, min=0.5, max=12.0, step=0.5, unit="Hz"),
        Param("duty", "On time", "float", 0.25, min=0.05, max=0.9, step=0.05),
        Param("random_colors", "New color each flash", "bool", False),
    )

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        beat = t * self.p["rate"]
        if beat % 1.0 >= self.p["duty"]:
            return [BLACK] * self.num_ports
        color = Color.from_hsv(hash01(int(beat), 99)) if self.p["random_colors"] else self.p["color"]
        return [color] * self.num_ports


class Gradient(Effect):
    id = "gradient"
    name = "Gradient"
    description = "A blend between two colors, static or moving."
    params = (
        Param("color_a", "From", "color", "#ff2d55"),
        Param("color_b", "To", "color", "#0a84ff"),
        Param("speed", "Speed", "float", 0.0, min=0.0, max=3.0, step=0.05, unit="cycles/s",
              description="0 keeps the gradient still."),
        Param("mirror", "Mirror", "bool", True,
              description="When moving, blend back to the first color instead of jumping to it."),
    )

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        n = self.num_ports
        colors = []
        for i in range(n):
            x = i / (n - 1) if n > 1 else 0.0
            if self.p["mirror"]:
                mix = triangle(x * 0.5 + t * self.p["speed"])
            else:
                mix = (x + t * self.p["speed"]) % 1.0 if self.p["speed"] else x
            colors.append(self.p["color_a"].lerp(self.p["color_b"], mix))
        return colors


class Ripple(Effect):
    id = "ripple"
    name = "Ripple"
    description = "Rings that move outward from one port."
    params = (
        Param("color", "Color", "color", "#64d2ff"),
        Param("background", "Background", "color", "#000000"),
        Param("center", "Center port", "int", 0, min=0, max=48, step=1, description="0 = the middle."),
        Param("speed", "Speed", "float", 4.0, min=0.5, max=20.0, step=0.5, unit="ports/s"),
        Param("spacing", "Ring spacing", "float", 4.0, min=1.5, max=12.0, step=0.5, unit="ports"),
    )

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        n = self.num_ports
        center = (n - 1) / 2 if self.p["center"] == 0 else min(self.p["center"], n) - 1
        spacing = self.p["spacing"]
        colors = []
        for i in range(n):
            behind_front = (t * self.p["speed"] - abs(i - center)) % spacing
            glow = max(0.0, 1.0 - behind_front / min(2.0, spacing))
            colors.append(self.p["background"].lerp(self.p["color"], glow))
        return colors


class Lightning(Effect):
    id = "lightning"
    name = "Lightning"
    description = "A dark background with random double flashes."
    params = (
        Param("color", "Flash", "color", "#e8f0ff"),
        Param("sky", "Sky", "color", "#050a24"),
        Param("rate", "Strikes", "float", 0.4, min=0.05, max=3.0, step=0.05, unit="per s"),
        Param("reach", "Reach", "int", 2, min=0, max=4, step=1, unit="ports",
              description="How many neighboring ports a flash spreads to."),
    )

    def setup(self) -> None:
        self._strikes: list[tuple[float, int]] = []
        self._last: float | None = None

    @staticmethod
    def _flash(age: float) -> float:
        if age < 0.05:
            return 1.0
        if age < 0.10:
            return 0.15
        if age < 0.16:
            return 1.0
        return math.exp(-(age - 0.16) * 10)

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        dt = 0.0 if self._last is None else min(0.5, max(0.0, t - self._last))
        self._last = t
        if self.rng.random() < self.p["rate"] * dt:
            self._strikes.append((t, self.rng.randrange(self.num_ports)))
        self._strikes = [(start, port) for start, port in self._strikes if t - start < 1.5]

        colors = []
        for i in range(self.num_ports):
            light = 0.0
            for start, port in self._strikes:
                distance = abs(i - port)
                if distance <= self.p["reach"]:
                    light = max(light, self._flash(t - start) * (1.0 - distance / (self.p["reach"] + 1)))
            colors.append(self.p["sky"].lerp(self.p["color"], light))
        return colors


DAYLIGHT_PALETTES: dict[str, tuple[tuple[float, Color], ...]] = {
    # (hour of day, color). The curve wraps from 24:00 back to 0:00.
    "natural": (
        (0.0, Color(40, 18, 70)), (5.5, Color(40, 18, 70)), (6.5, Color(255, 120, 40)), (8.0, Color(255, 200, 140)),
        (12.0, Color(235, 245, 255)), (17.0, Color(255, 210, 160)), (19.0, Color(255, 110, 60)),
        (20.5, Color(160, 50, 120)), (22.0, Color(40, 18, 70)), (24.0, Color(40, 18, 70)),
    ),
    "warm": (
        (0.0, Color(120, 30, 0)), (6.0, Color(120, 30, 0)), (8.0, Color(255, 170, 90)), (13.0, Color(255, 220, 170)),
        (19.0, Color(255, 140, 60)), (22.0, Color(120, 30, 0)), (24.0, Color(120, 30, 0)),
    ),
    "vivid": (
        (0.0, Color(20, 0, 120)), (6.0, Color(255, 60, 0)), (9.0, Color(255, 220, 0)), (13.0, Color(0, 200, 255)),
        (18.0, Color(255, 0, 120)), (21.0, Color(120, 0, 255)), (24.0, Color(20, 0, 120)),
    ),
}


class Daylight(Effect):
    id = "daylight"
    name = "Daylight"
    category = "time"
    description = "Follows the clock through sunrise, day, sunset and night colors."
    params = (
        Param("palette", "Palette", "choice", "natural", choices=tuple(DAYLIGHT_PALETTES)),
        Param("night_brightness", "Night brightness", "float", 0.35, min=0.0, max=1.0, step=0.05),
    )

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        now = ctx.now
        hour = now.hour + now.minute / 60 + now.second / 3600
        stops = DAYLIGHT_PALETTES[self.p["palette"]]
        color = gradient_at(tuple((h / 24, c) for h, c in stops), hour / 24)
        night = hour < 6 or hour >= 22
        if night:
            color = color.scaled(self.p["night_brightness"])
        return [color] * self.num_ports


class TrafficMeter(Effect):
    id = "traffic"
    name = "Traffic Meter"
    category = "data"
    description = "Each port moves from the idle color to the busy color as its throughput rises."
    params = (
        Param("idle_color", "Idle", "color", "#0a3cff"),
        Param("busy_color", "Busy", "color", "#ff3b30"),
        Param("max_mbps", "Full scale", "float", 1000.0, min=1, max=10000, step=1, unit="Mbps"),
        Param("scale", "Scale", "choice", "log", choices=("log", "linear")),
        Param("smoothing", "Smoothing", "float", 0.6, min=0.0, max=0.95, step=0.05),
        Param("offline_color", "No link", "color", "#000000"),
    )

    def setup(self) -> None:
        self._level = [0.0] * self.num_ports

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        full_scale = self.p["max_mbps"] * 1e6
        smoothing = self.p["smoothing"]
        colors = []
        for i in range(self.num_ports):
            link = ctx.links.get(i + 1)
            if link is None or not link.link_up:
                self._level[i] = 0.0
                colors.append(self.p["offline_color"])
                continue
            bps = ctx.traffic_bps.get(i + 1, 0.0)
            if self.p["scale"] == "log":
                load = 0.0 if bps <= 1e3 else math.log10(bps / 1e3) / math.log10(max(full_scale / 1e3, 10.0))
            else:
                load = bps / full_scale
            load = min(1.0, max(0.0, load))
            self._level[i] = smoothing * self._level[i] + (1 - smoothing) * load
            colors.append(self.p["idle_color"].lerp(self.p["busy_color"], self._level[i]))
        return colors


ALL_EFFECTS: tuple[type[Effect], ...] = (
    Static,
    Breathing,
    ColorCycle,
    RainbowWave,
    Comet,
    Scanner,
    Marquee,
    StarryNight,
    Fire,
    Police,
    Strobe,
    Gradient,
    Ripple,
    Lightning,
    Daylight,
    TrafficMeter,
)
