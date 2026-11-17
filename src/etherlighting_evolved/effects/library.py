"""Built-in effects: 15 lighting effects and a traffic meter.

Each effect returns one color per port in its zone. Colors based on port status (offline, PoE, anomaly, ...) come
from rules, which are applied after the effect.

Moving light has soft edges wider than a port (see `soft`), which makes neighboring ports crossfade as it travels.
Colors are in perceived brightness; the engine applies gamma correction at output.
"""

from __future__ import annotations

import itertools
import math

from ..color import BLACK, Color
from .base import Effect, Param, RenderContext, hash01


def breath(phase: float) -> float:
    """One smooth rise and fall, with zero slope at both ends."""
    return 0.5 - 0.5 * math.cos(2 * math.pi * phase)


def soft(distance: float, width: float) -> float:
    """1 at distance 0, easing to 0 at `width` ports away (a raised cosine)."""
    x = abs(distance) / width
    return 0.0 if x >= 1.0 else 0.5 + 0.5 * math.cos(math.pi * x)


def ease(x: float) -> float:
    x = min(1.0, max(0.0, x))
    return x * x * (3 - 2 * x)


def inverse_ease(x: float) -> float:
    return 0.5 - math.sin(math.asin(1 - 2 * min(1.0, max(0.0, x))) / 3)


def bounce(position: float, length: int) -> tuple[float, int]:
    """A position traveling back and forth over 0..length-1, and which way it's heading."""
    if length <= 1:
        return 0.0, 1
    period = 2 * (length - 1)
    x = position % period
    return (x, 1) if x <= length - 1 else (period - x, -1)


def ring_distance(a: float, b: float, n: int) -> float:
    """Signed distance from b to a going forward around n ports in a loop, between -n/2 and n/2."""
    d = (a - b) % n
    return d - n if d > n / 2 else d


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
        return [self.colors(t)["color"]] * self.num_ports


class Breathing(Effect):
    id = "breathing"
    name = "Breathing"
    description = "Fades in and out."
    params = (
        Param("color", "Color", "color", "#0a84ff"),
        Param("playback", "Playback", "choice", "custom", choices=("synchronized", "custom")),
        Param("pace", "Shared speed", "int", 2, min=1, max=3, step=1),
        Param("period", "Breath length", "float", 5.0, min=1.0, max=30.0, step=0.5, unit="s"),
        Param("floor", "Lowest brightness", "float", 0.1, min=0.0, max=1.0, step=0.05),
        Param("ripple", "Ripple across ports", "bool", False),
    )

    def animation_rate(self) -> float:
        period = ({1: 3.08, 2: 2.0, 3: 1.0}[self.p["pace"]]
                  if self.p["playback"] == "synchronized" else self.p["period"])
        return 1 / period

    def colors(self, t: float) -> dict[str, Color]:
        if self.p["playback"] == "synchronized":
            return {"color": self.adjust_color("color", self.p["color"])}
        return super().colors(t)

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        color = self.colors(t)["color"]
        synchronized = self.p["playback"] == "synchronized"
        floor = 0.0 if synchronized else self.p["floor"]
        phase = self.phase(t, self.animation_rate())
        colors = []
        for i in range(self.num_ports):
            offset = i / (2 * self.num_ports) if self.p["ripple"] and not synchronized else 0.0
            colors.append(color.scaled(floor + (1 - floor) * breath(phase - offset)))
        return colors


class ColorCycle(Effect):
    id = "color_cycle"
    name = "Color Cycle"
    description = "All ports move through the color wheel together."
    params = (
        Param("speed", "Speed", "float", 0.05, min=0.01, max=1.0, step=0.01, unit="cycles/s"),
        Param("saturation", "Saturation", "float", 1.0, min=0.0, max=1.0, step=0.05),
    )

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        return [Color.from_hsv(self.phase(t, self.p["speed"]), self.p["saturation"])] * self.num_ports


class RainbowWave(Effect):
    id = "rainbow"
    name = "Rainbow Wave"
    description = "A rainbow that moves along the ports."
    params = (
        Param("speed", "Speed", "float", 0.1, min=0.0, max=1.0, step=0.01, unit="cycles/s"),
        Param("spread", "Spread", "float", 1.0, min=0.1, max=3.0, step=0.05,
              description="How much of the color wheel fits across the ports."),
        Param("saturation", "Saturation", "float", 1.0, min=0.0, max=1.0, step=0.05),
        Param("reverse", "Reverse", "bool", False),
    )

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        start = (-1 if self.p["reverse"] else 1) * self.phase(t, self.p["speed"])
        step = self.p["spread"] / self.num_ports
        return [Color.from_hsv(start + i * step, self.p["saturation"]) for i in range(self.num_ports)]


class Comet(Effect):
    id = "comet"
    name = "Comet"
    description = "A bright point with a fading tail that moves along the ports."
    params = (
        Param("color", "Color", "color", "#64d2ff"),
        Param("background", "Background", "color", "#000000"),
        Param("speed", "Speed", "float", 1.0, min=0.25, max=12.0, step=0.25, unit="ports/s"),
        Param("tail", "Tail", "float", 3.0, min=0.5, max=8.0, step=0.5, unit="ports"),
        Param("bounce", "Bounce", "bool", False),
    )

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        palette = self.colors(t)
        n, tail = self.num_ports, self.p["tail"]
        travel = self.phase(t, self.p["speed"])
        if n == 1:
            return [palette["color"]]
        head = travel % n
        trail = [(bounce(travel - tail * step / 16, n)[0], math.exp(-3 * step / 16))
                 for step in range(17)] if self.p["bounce"] else []
        colors = []
        for i in range(n):
            if trail:
                # The trail follows earlier positions through the turn instead of switching sides instantly.
                glow = max(soft(i - position, 1.5) * strength for position, strength in trail)
            else:
                behind = (head - i) % n
                head_glow = soft(ring_distance(head, i, n), 1.5)
                tail_glow = math.exp(-3 * behind / tail) * ease((n - behind) / 1.5)
                glow = max(head_glow, tail_glow)
            colors.append(palette["background"].lerp(palette["color"], glow))
        return colors


class Scanner(Effect):
    id = "scanner"
    name = "Scanner"
    description = "A light that sweeps back and forth."
    params = (
        Param("color", "Color", "color", "#ff453a"),
        Param("background", "Background", "color", "#000000"),
        Param("speed", "Speed", "float", 1.0, min=0.25, max=12.0, step=0.25, unit="ports/s"),
        Param("width", "Width", "float", 2.0, min=2.0, max=4.0, step=0.1, unit="ports"),
    )

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        palette = self.colors(t)
        n = self.num_ports
        if n == 1:
            return [palette["color"]]
        # Slows down and turns around at each end instead of bouncing off it.
        sweep = self.phase(t, self.p["speed"]) / (2 * (n - 1))
        head = (n - 1) * (0.5 - 0.5 * math.cos(2 * math.pi * sweep))
        return [palette["background"].lerp(palette["color"], soft(i - head, self.p["width"])) for i in range(n)]


class Marquee(Effect):
    id = "marquee"
    name = "Marquee"
    description = "Every second, third or fourth port lit, stepping along."
    params = (
        Param("color", "Color", "color", "#ffd60a"),
        Param("background", "Background", "color", "#000000"),
        Param("speed", "Speed", "float", 0.75, min=0.25, max=10.0, step=0.25, unit="ports/s"),
        Param("spacing", "Spacing", "int", 3, min=2, max=4, step=1, unit="ports"),
    )

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        palette = self.colors(t)
        spacing = self.p["spacing"]
        shift = self.phase(t, self.p["speed"])
        colors = []
        for i in range(self.num_ports):
            offset = (i - shift) % spacing
            nearest = min(offset, spacing - offset)  # distance to the closest lit position
            colors.append(palette["background"].lerp(palette["color"], soft(nearest, 1.25)))
        return colors


class StarryNight(Effect):
    id = "starry_night"
    name = "Starry Night"
    description = "Random ports light up and fade out."
    exclusive_params = (("random_color", "rainbow"),)
    params = (
        Param("color", "Color", "color", "#ffffff"),
        Param("background", "Background", "color", "#000000"),
        Param("rainbow", "Different color per sparkle", "bool", False),
        Param("rate", "Sparkles", "float", 0.3, min=0.05, max=2.0, step=0.05, unit="per port/s"),
        Param("fade", "Fade", "float", 2.5, min=1.5, max=6.0, step=0.1, unit="s"),
    )

    RISE = 1.5

    def setup(self) -> None:
        self._born: list[float | None] = [None] * self.num_ports
        self._hue = [0.0] * self.num_ports
        self._last: float | None = None

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        palette = self.colors(t)
        dt = 0.0 if self._last is None else max(0.0, t - self._last)
        self._last = t
        life = self.RISE + self.p["fade"]
        colors = []
        for i in range(self.num_ports):
            born = self._born[i]
            if (born is None or t - born > life) and self.rng.random() < -math.expm1(-self.p["rate"] * dt):
                born = self._born[i] = t
                self._hue[i] = self.rng.random()
            glow = 0.0
            if born is not None and t - born <= life:
                age = t - born
                glow = ease(age / self.RISE) if age < self.RISE else 1 - ease((age - self.RISE) / self.p["fade"])
            star = (self.adjust_color("color", Color.from_hsv(self._hue[i]))
                    if self.p["rainbow"] else palette["color"])
            colors.append(palette["background"].lerp(star, glow))
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
        Param("speed", "Flicker", "float", 1.0, min=0.1, max=3.0, step=0.1),
    )

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        stops = FIRE_PALETTES[self.p["palette"]]
        phase = self.phase(t, self.p["speed"])
        colors = []
        for i in range(self.num_ports):
            slow = noise(phase * 1.2 + i * 7.31, seed=i + 1)
            fast = noise(phase * 3.0 + i * 3.7, seed=100 + i)
            colors.append(gradient_at(stops, self.p["intensity"] * (0.25 + 0.55 * slow + 0.2 * fast)))
        return colors


class Police(Effect):
    id = "police"
    name = "Police"
    description = "Alternating flashes in two colors."
    params = (
        Param("color_a", "Left color", "color", "#ff0000"),
        Param("color_b", "Right color", "color", "#0040ff"),
        Param("speed", "Speed", "float", 0.4, min=0.1, max=3.0, step=0.05, unit="cycles/s"),
        Param("pattern", "Pattern", "choice", "split", choices=("split", "alternate", "together")),
    )

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        phase = self.phase(t, self.p["speed"]) % 1.0
        first_half = phase < 0.5
        flash = math.sin(math.pi * ((phase % 0.5) / 0.5)) ** 2  # ramps up and down within each half
        palette = self.colors(t)
        a, b, n = palette["color_a"], palette["color_b"], self.num_ports

        colors = []
        for i in range(n):
            if self.p["pattern"] == "split":
                left = i < n // 2
                lit = first_half == left
                colors.append((a if left else b).scaled(flash) if lit else BLACK)
            elif self.p["pattern"] == "alternate":
                even = i % 2 == 0
                lit = first_half == even
                colors.append((a if even else b).scaled(flash) if lit else BLACK)
            else:
                colors.append((a if first_half else b).scaled(flash))
        return colors


class Strobe(Effect):
    id = "strobe"
    name = "Strobe"
    exclusive_params = (("random_color", "random_colors"),)
    description = "Fast flashes. Can affect people who are sensitive to flashing light."
    params = (
        Param("color", "Color", "color", "#ffffff"),
        Param("rate", "Rate", "float", 0.5, min=0.25, max=4.0, step=0.25, unit="Hz"),
        Param("duty", "On time", "float", 0.5, min=0.4, max=0.6, step=0.05),
        Param("random_colors", "New color each flash", "bool", False),
    )

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        beat = self.phase(t, self.p["rate"])
        if beat % 1.0 >= self.p["duty"]:
            return [BLACK] * self.num_ports
        color = (self.adjust_color("color", Color.from_hsv(hash01(int(beat), 99)))
                 if self.p["random_colors"] else self.colors(t)["color"])
        phase = beat % 1.0
        edge = min(0.1 * self.p["rate"], self.p["duty"] / 2)
        envelope = ease(phase / edge) * ease((self.p["duty"] - phase) / edge)
        return [color.scaled(envelope)] * self.num_ports


class Gradient(Effect):
    id = "gradient"
    name = "Gradient"
    description = "A blend between two colors, static or moving."
    params = (
        Param("color_a", "From", "color", "#79dcc0"),
        Param("color_b", "To", "color", "#a38de0"),
        Param("speed", "Speed", "float", 0.0, min=0.0, max=3.0, step=0.01, unit="cycles/s",
              description="0 keeps the gradient still."),
        Param("mirror", "Mirror", "bool", True,
              description="Use an even back-and-forth blend instead of a longer sweep and shorter return."),
    )

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        palette = self.colors(t)
        n = self.num_ports
        travel = self.phase(t, self.p["speed"])
        colors = []
        for i in range(n):
            x = i / (n - 1) if n > 1 else 0.0
            if self.p["mirror"]:
                mix = 0.5 - 0.5 * math.cos(math.acos(1 - 2 * x) + 2 * math.pi * travel)
            else:
                phase = (0.8 * inverse_ease(x) + travel) % 1.0
                mix = ease(phase / 0.8) if phase < 0.8 else 1 - ease((phase - 0.8) / 0.2)
            colors.append(palette["color_a"].lerp(palette["color_b"], mix))
        return colors


class Ripple(Effect):
    id = "ripple"
    name = "Ripple"
    description = "Rings that move outward from one port."
    params = (
        Param("color", "Color", "color", "#64d2ff"),
        Param("background", "Background", "color", "#000000"),
        Param("center", "Center port", "int", 0, min=0, max=48, step=1, description="0 = the middle."),
        Param("speed", "Speed", "float", 1.0, min=0.25, max=10.0, step=0.25, unit="ports/s"),
        Param("spacing", "Ring spacing", "float", 4.0, min=2.0, max=12.0, step=0.5, unit="ports"),
    )

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        palette = self.colors(t)
        n = self.num_ports
        center = (n - 1) / 2 if self.p["center"] == 0 else min(self.p["center"], n) - 1
        spacing = self.p["spacing"]
        travel = self.phase(t, self.p["speed"])
        colors = []
        for i in range(n):
            behind_front = (travel - abs(i - center)) % spacing
            nearest = min(behind_front, spacing - behind_front)
            colors.append(palette["background"].lerp(palette["color"], soft(nearest, 1.75)))
        return colors


class Lightning(Effect):
    id = "lightning"
    name = "Lightning"
    description = "A dark background with random double flashes."
    params = (
        Param("color", "Flash", "color", "#e8f0ff"),
        Param("sky", "Sky", "color", "#050a24"),
        Param("rate", "Strikes", "float", 0.3, min=0.05, max=2.0, step=0.05, unit="per s"),
        Param("reach", "Reach", "int", 2, min=0, max=4, step=1, unit="ports",
              description="How many neighboring ports a flash spreads to."),
    )

    def setup(self) -> None:
        self._strikes: list[tuple[float, int]] = []
        self._last: float | None = None

    @staticmethod
    def _flash(age: float) -> float:
        """Two rounded flashes followed by a soft afterglow."""
        if age < 0:
            return 0.0
        return max(soft(age - 0.5, 0.5), 0.8 * soft(age - 1.5, 0.65), 0.2 * soft(age - 1.8, 1.2))

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        palette = self.colors(t)
        dt = 0.0 if self._last is None else max(0.0, t - self._last)
        self._last = t
        if self.rng.random() < -math.expm1(-self.p["rate"] * dt):
            self._strikes.append((t, self.rng.randrange(self.num_ports)))
        self._strikes = [(start, port) for start, port in self._strikes if t - start < 3.0]

        colors = []
        for i in range(self.num_ports):
            light = 0.0
            for start, port in self._strikes:
                distance = abs(i - port)
                if distance <= self.p["reach"]:
                    light = max(light, self._flash(t - start) * (1.0 - distance / (self.p["reach"] + 1)))
            colors.append(palette["sky"].lerp(palette["color"], light))
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
        daylight = ease((hour - 6) / 2) * (1 - ease((hour - 20) / 2))
        color = color.scaled(self.p["night_brightness"] + (1 - self.p["night_brightness"]) * daylight)
        return [color] * self.num_ports


class TrafficMeter(Effect):
    id = "traffic"
    name = "Live Traffic"
    category = "data"
    description = "Colors follow each device’s traffic, scaled automatically to its link speed."
    params = (
        Param("idle_color", "Idle", "color", "#0a3cff"),
        Param("busy_color", "Busy", "color", "#ff3b30"),
        Param("smoothing", "Smoothing", "float", 0.6, min=0.0, max=0.95, step=0.05),
        Param("offline_color", "No link", "color", "#000000"),
        Param("include_uplinks", "Include uplink traffic", "bool", False),
        Param("custom_uplink", "Customize uplink", "bool", False),
        Param("uplink_color", "Uplink", "color", "#ffffff"),
    )

    def update(self, params: dict, *, reset: bool = False) -> None:
        # Older scenes stored a manual scale. Link speed now supplies it per port.
        params = dict(params)
        if "max_mbps" in params:
            Param("max_mbps", "", "float", 1000.0).coerce(params.pop("max_mbps"))
        if "scale" in params:
            Param("scale", "", "choice", "log", choices=("log", "linear")).coerce(params.pop("scale"))
        super().update(params, reset=reset)

    def setup(self) -> None:
        self._level = [0.0] * self.num_ports
        self._last: float | None = None

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        palette = self.colors(t)
        dt = 0.25 if self._last is None else max(0.0, t - self._last)
        self._last = t
        smoothing = self.p["smoothing"] ** (dt * 4)
        colors = []
        for i in range(self.num_ports):
            link = ctx.links.get(i + 1)
            if link is None or not link.link_up:
                self._level[i] = 0.0
                colors.append(palette["offline_color"])
                continue
            if link.uplink and self.p["custom_uplink"]:
                self._level[i] = 0.0
                colors.append(palette["uplink_color"])
                continue
            bps = ctx.traffic_bps.get(i + 1, 0.0) if self.p["include_uplinks"] or not link.uplink else 0.0
            full_scale = (link.speed_mbps if link.speed_mbps > 0 else 1000) * 1e6
            load = 0.0 if bps <= 1e3 else math.log10(bps / 1e3) / math.log10(max(full_scale / 1e3, 10.0))
            load = min(1.0, max(0.0, load))
            self._level[i] = smoothing * self._level[i] + (1 - smoothing) * load
            colors.append(palette["idle_color"].lerp(palette["busy_color"], self._level[i]))
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
