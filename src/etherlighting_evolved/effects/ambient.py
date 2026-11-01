"""Continuous light fields and moving patterns for small port groups."""

from __future__ import annotations

import math

from ..color import Color
from .base import Effect, Param, RenderContext, hash01
from .library import ease, noise, ring_distance, soft

TAU = 2 * math.pi


def wave(phase: float) -> float:
    return 0.5 + 0.5 * math.sin(TAU * phase)


def coordinate(index: int, count: int) -> float:
    return index / (count - 1) if count > 1 else 0.5


def palette(colors: tuple[Color, ...], value: float) -> Color:
    position = max(0.0, min(1.0, value)) * (len(colors) - 1)
    index = min(len(colors) - 2, int(position))
    return colors[index].lerp(colors[index + 1], ease(position - index))


def weighted(lights: list[tuple[Color, float]]) -> Color:
    if any(not math.isfinite(weight) for _, weight in lights):
        raise ValueError("color weights must be finite")
    rounded_totals = tuple(sum(getattr(color, channel) * weight for color, weight in lights)
                           for channel in ("r", "g", "b"))
    precise_totals = tuple(sum(color.precise_channels[index] * weight for color, weight in lights)
                           for index in range(3))
    if not all(math.isfinite(value) for value in (*rounded_totals, *precise_totals)):
        raise ValueError("weighted color channels must be finite")
    rgb = tuple(min(255, max(0, round(value))) for value in rounded_totals)
    precise = tuple(min(255.0, max(0.0, value)) for value in precise_totals)
    return Color._with_precision(rgb, precise)


def glow(background: Color, lights: list[tuple[Color, float]]) -> Color:
    total = sum(weight for _, weight in lights)
    if total <= 0:
        return background
    strength = 1 - math.exp(-total)
    return weighted([(background, 1 - strength)] + [(color, weight * strength / total) for color, weight in lights])


class Aurora(Effect):
    id = "aurora"
    name = "Aurora"
    description = "Overlapping curtains of color drift across the ports."
    params = (
        Param("color_a", "First curtain", "color", "#38e8b5"),
        Param("color_b", "Second curtain", "color", "#ae78ff"),
        Param("background", "Background", "color", "#07142e"),
        Param("speed", "Speed", "float", 0.08, min=0.02, max=1.0, step=0.01, unit="cycles/s"),
        Param("layers", "Depth", "float", 0.65, min=0.1, max=1.0, step=0.05),
    )

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        c, p = self.colors(t), self.phase(t, self.p["speed"])
        colors = []
        for i in range(self.num_ports):
            x = coordinate(i, self.num_ports)
            a = wave(x * 0.8 - p + 0.12 * math.sin(TAU * (p * 0.31 + x)))
            b = wave(x * 1.2 + p * 0.67 + 0.35)
            colors.append(glow(c["background"], [(c["color_a"], 1.8 * a * a),
                                                   (c["color_b"], 1.8 * self.p["layers"] * b * b)]))
        return colors


class LavaLamp(Effect):
    id = "lava_lamp"
    name = "Lava Lamp"
    description = "Soft blobs of color drift, stretch and merge."
    params = (
        Param("color_a", "First color", "color", "#fa6399"),
        Param("color_b", "Second color", "color", "#ffbe63"),
        Param("background", "Background", "color", "#200c38"),
        Param("speed", "Speed", "float", 0.06, min=0.01, max=0.8, step=0.01, unit="cycles/s"),
        Param("width", "Blob width", "float", 2.5, min=1.25, max=8.0, step=0.25, unit="ports"),
    )

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        c, p = self.colors(t), self.phase(t, self.p["speed"])
        n = self.num_ports
        centers = [(n - 1) * wave(p), (n - 1) * wave(p * 0.73 + 0.39),
                   (n - 1) * wave(p * 0.41 + 0.7)]
        colors = []
        for i in range(n):
            lights = []
            for j, center in enumerate(centers):
                width = self.p["width"] * (0.8 + 0.3 * wave(p * 0.53 + j * 0.3))
                weight = math.exp(-((i - center) / width) ** 2) * (0.65 + 0.65 * wave(p * 0.61 + j * 0.4))
                lights.append((c["color_a"].lerp(c["color_b"], j / 2), weight))
            colors.append(glow(c["background"], lights))
        return colors


class Plasma(Effect):
    id = "plasma"
    name = "Plasma"
    description = "Intersecting waves reshape a flowing three-color field."
    params = (
        Param("color_a", "First color", "color", "#5b4dff"),
        Param("color_b", "Middle color", "color", "#ef60d0"),
        Param("color_c", "Last color", "color", "#61e9e2"),
        Param("speed", "Speed", "float", 0.1, min=0.02, max=1.5, step=0.01, unit="cycles/s"),
        Param("scale", "Wave density", "float", 1.2, min=0.5, max=3.0, step=0.1),
    )

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        c, p = self.colors(t), self.phase(t, self.p["speed"])
        stops = (c["color_a"], c["color_b"], c["color_c"])
        return [palette(stops, (wave(coordinate(i, self.num_ports) * self.p["scale"] + p)
                               + wave(coordinate(i, self.num_ports) * self.p["scale"] * 0.6 - p * 0.73)
                               + wave(coordinate(i, self.num_ports) * self.p["scale"] * 0.35 + p * 0.41)) / 3)
                for i in range(self.num_ports)]


class OceanSwell(Effect):
    id = "ocean_swell"
    name = "Ocean Swell"
    description = "Layered swells rise into bright crests and settle back."
    params = (
        Param("deep_color", "Deep water", "color", "#0843a8"),
        Param("crest_color", "Wave crest", "color", "#56e1d2"),
        Param("foam_color", "Highlight", "color", "#c0fff2"),
        Param("speed", "Speed", "float", 0.09, min=0.02, max=1.0, step=0.01, unit="cycles/s"),
        Param("depth", "Swell depth", "float", 0.6, min=0.1, max=1.0, step=0.05),
    )

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        c, p = self.colors(t), self.phase(t, self.p["speed"])
        colors = []
        for i in range(self.num_ports):
            x = coordinate(i, self.num_ports)
            height = 0.65 * wave(x * 0.8 - p) + 0.35 * wave(x * 1.4 + p * 0.63 + 0.2)
            foam = 0.65 * ease((height - 0.6) / 0.4)
            level = 1 - self.p["depth"] * (1 - height)
            colors.append(weighted([(c["deep_color"], (1 - height) * (1 - foam) * level),
                                    (c["crest_color"], height * (1 - foam) * level),
                                    (c["foam_color"], foam * level)]))
        return colors


class Eclipse(Effect):
    id = "eclipse"
    name = "Eclipse"
    description = "A soft shadow passes over a steady field of color."
    params = (
        Param("color", "Base color", "color", "#f5c789"),
        Param("shadow", "Shadow color", "color", "#0e1539"),
        Param("speed", "Speed", "float", 0.1, min=0.02, max=1.5, step=0.01, unit="cycles/s"),
        Param("width", "Shadow width", "float", 2.0, min=1.25, max=8.0, step=0.25, unit="ports"),
        Param("depth", "Shadow depth", "float", 0.9, min=0.1, max=1.0, step=0.05),
    )

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        c, p = self.colors(t), self.phase(t, self.p["speed"])
        if self.num_ports == 1:
            return [c["color"].lerp(c["shadow"], self.p["depth"] * wave(p))]
        return [c["color"].lerp(c["shadow"], self.p["depth"]
                                * soft(ring_distance(i, p * self.num_ports, self.num_ports), self.p["width"]))
                for i in range(self.num_ports)]


class MeteorShower(Effect):
    id = "meteor_shower"
    name = "Meteor Shower"
    description = "Several soft streaks cross the ports with fading trails."
    params = (
        Param("color_a", "First streak", "color", "#7aa6ff"),
        Param("color_b", "Second streak", "color", "#d5b1ff"),
        Param("background", "Background", "color", "#04091e"),
        Param("speed", "Speed", "float", 0.12, min=0.03, max=1.5, step=0.01, unit="cycles/s"),
        Param("count", "Streaks", "int", 3, min=1, max=4, step=1),
        Param("tail", "Trail length", "float", 3.0, min=1.25, max=8.0, step=0.25, unit="ports"),
    )

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        c, p = self.colors(t), self.phase(t, self.p["speed"])
        colors = []
        for i in range(self.num_ports):
            lights = []
            for j in range(self.p["count"]):
                life = p * (1 - j * 0.09) + j * 0.37
                cycle, u = math.floor(life), life % 1
                head = -self.p["tail"] + u * (self.num_ports - 1 + 2 * self.p["tail"])
                distance = head - i
                width = self.p["tail"] if distance >= 0 else 1.25
                weight = math.exp(-(distance / width) ** 2) * math.sin(math.pi * u) ** 2 * 2
                color = c["color_a"].lerp(c["color_b"], hash01(cycle, 83 + j))
                lights.append((color, weight))
            colors.append(glow(c["background"], lights))
        return colors


class Counterflow(Effect):
    id = "counterflow"
    name = "Counterflow"
    description = "Two streams travel in opposite directions and blend as they meet."
    params = (
        Param("color_a", "Forward color", "color", "#fa8cba"),
        Param("color_b", "Reverse color", "color", "#5dcfdc"),
        Param("background", "Background", "color", "#071323"),
        Param("speed", "Speed", "float", 0.1, min=0.02, max=1.5, step=0.01, unit="cycles/s"),
        Param("width", "Stream width", "float", 2.25, min=1.25, max=8.0, step=0.25, unit="ports"),
    )

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        c, p = self.colors(t), self.phase(t, self.p["speed"])
        if self.num_ports == 1:
            return [c["color_a"].lerp(c["color_b"], wave(p))]
        return [glow(c["background"], [
            (c["color_a"], 2 * soft(ring_distance(i, p * self.num_ports, self.num_ports), self.p["width"])),
            (c["color_b"], 2 * soft(ring_distance(i, (0.5 - p * 0.83) * self.num_ports,
                                                self.num_ports), self.p["width"]))])
            for i in range(self.num_ports)]


class ColorDissolve(Effect):
    id = "color_dissolve"
    name = "Color Dissolve"
    description = "Ports blend through a palette in a staggered order."
    params = (
        Param("color_a", "First color", "color", "#f0a3c2"),
        Param("color_b", "Second color", "color", "#9c9bea"),
        Param("color_c", "Third color", "color", "#67d8bd"),
        Param("speed", "Speed", "float", 0.12, min=0.02, max=1.5, step=0.01, unit="cycles/s"),
        Param("stagger", "Stagger", "float", 0.6, min=0.0, max=0.9, step=0.05),
    )

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        c, p = self.colors(t), self.phase(t, self.p["speed"])
        stops = (c["color_a"], c["color_b"], c["color_c"])
        colors = []
        for i in range(self.num_ports):
            local = p + hash01(i, 31) * self.p["stagger"]
            index = math.floor(local)
            colors.append(stops[index % 3].lerp(stops[(index + 1) % 3], ease(local - index)))
        return colors


class InkSpread(Effect):
    id = "ink_spread"
    name = "Ink Spread"
    description = "A new color spreads gently from a changing starting point."
    params = (
        Param("color_a", "First color", "color", "#e56ba9"),
        Param("color_b", "Second color", "color", "#768aef"),
        Param("color_c", "Third color", "color", "#53d8c6"),
        Param("speed", "Speed", "float", 0.1, min=0.02, max=1.0, step=0.01, unit="cycles/s"),
        Param("spread", "Spread", "float", 0.55, min=0.1, max=0.8, step=0.05),
    )

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        c, p = self.colors(t), self.phase(t, self.p["speed"])
        stops = (c["color_a"], c["color_b"], c["color_c"])
        index, u = math.floor(p), p % 1
        origin = hash01(index, 211) * (self.num_ports - 1)
        return [stops[index % 3].lerp(stops[(index + 1) % 3],
                ease((u - self.p["spread"] * abs(i - origin) / max(1, self.num_ports - 1))
                     / (1 - self.p["spread"]))) for i in range(self.num_ports)]


class AlternatingFade(Effect):
    id = "alternating_fade"
    name = "Alternating Fade"
    description = "Odd and even ports crossfade between two colors."
    params = (
        Param("color_a", "First color", "color", "#e7b678"),
        Param("color_b", "Second color", "color", "#9d8be7"),
        Param("speed", "Speed", "float", 0.12, min=0.02, max=1.5, step=0.01, unit="cycles/s"),
    )

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        c, p = self.colors(t), self.phase(t, self.p["speed"])
        return [c["color_a"].lerp(c["color_b"], wave(p + (i % 2) * 0.5)) for i in range(self.num_ports)]


class ColorTide(Effect):
    id = "color_tide"
    name = "Color Tide"
    description = "Color fills the row and gently recedes."
    params = (
        Param("color", "Tide color", "color", "#57dfc0"),
        Param("background", "Background", "color", "#164099"),
        Param("speed", "Speed", "float", 0.08, min=0.02, max=1.0, step=0.01, unit="cycles/s"),
        Param("width", "Edge softness", "float", 4.0, min=1.25, max=8.0, step=0.25, unit="ports"),
        Param("reverse", "Reverse", "bool", False),
    )

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        c, p = self.colors(t), self.phase(t, self.p["speed"])
        width = self.p["width"]
        front = -width / 2 + (self.num_ports - 1 + width) * wave(p)
        return [c["background"].lerp(c["color"], ease((front - (self.num_ports - 1 - i
                if self.p["reverse"] else i)) / width + 0.5)) for i in range(self.num_ports)]


class PaletteMorph(Effect):
    id = "palette_morph"
    name = "Palette Morph"
    description = "A complete gradient transforms into another palette."
    params = (
        Param("first_start", "First palette start", "color", "#ef948e"),
        Param("first_end", "First palette end", "color", "#e5bf73"),
        Param("second_start", "Second palette start", "color", "#8375db"),
        Param("second_end", "Second palette end", "color", "#64d2c8"),
        Param("speed", "Speed", "float", 0.06, min=0.01, max=1.0, step=0.01, unit="cycles/s"),
        Param("drift", "Gradient drift", "float", 0.25, min=0.0, max=1.0, step=0.05),
    )

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        c, p = self.colors(t), self.phase(t, self.p["speed"])
        colors = []
        for i in range(self.num_ports):
            x = coordinate(i, self.num_ports)
            position = 0.5 - 0.5 * math.cos(math.pi * x + TAU * p * 0.5 * self.p["drift"])
            morph = wave(p)
            colors.append(weighted([(c["first_start"], (1 - position) * (1 - morph)),
                                    (c["first_end"], position * (1 - morph)),
                                    (c["second_start"], (1 - position) * morph),
                                    (c["second_end"], position * morph)]))
        return colors


class EmberBed(Effect):
    id = "ember_bed"
    name = "Ember Bed"
    description = "Diffuse warm hotspots glow and cool at different rates."
    params = (
        Param("cool_color", "Cooling color", "color", "#85251d"),
        Param("hot_color", "Hot color", "color", "#ffb85c"),
        Param("speed", "Speed", "float", 0.18, min=0.03, max=2.0, step=0.01, unit="cycles/s"),
        Param("depth", "Glow depth", "float", 0.65, min=0.1, max=1.0, step=0.05),
    )

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        c, p = self.colors(t), self.phase(t, self.p["speed"])
        colors = []
        for i in range(self.num_ports):
            heat = 0.15 + 0.55 * noise(i * 0.7 + p, 71) + 0.3 * wave(p * 0.37 - i * 0.23)
            level = 1 - self.p["depth"] * (1 - heat)
            colors.append(weighted([(c["cool_color"], (1 - heat) * level), (c["hot_color"], heat * level)]))
        return colors


class Fireflies(Effect):
    id = "fireflies"
    name = "Fireflies"
    description = "Small lights wander between ports while gently pulsing."
    params = (
        Param("color_a", "First light", "color", "#dfed7a"),
        Param("color_b", "Second light", "color", "#70e5b3"),
        Param("background", "Background", "color", "#071b18"),
        Param("speed", "Speed", "float", 0.15, min=0.03, max=1.5, step=0.01, unit="cycles/s"),
        Param("count", "Lights", "int", 3, min=1, max=4, step=1),
        Param("width", "Glow width", "float", 1.75, min=1.25, max=5.0, step=0.25, unit="ports"),
    )

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        c, p = self.colors(t), self.phase(t, self.p["speed"])
        colors = []
        for i in range(self.num_ports):
            lights = []
            for j in range(self.p["count"]):
                center = (self.num_ports - 1) * noise(p * 0.23 + j * 11, 131 + j)
                weight = soft(i - center, self.p["width"]) * wave(p * 0.7 + j * 0.31) ** 2 * 2.5
                lights.append((c["color_a"].lerp(c["color_b"], wave(p * 0.19 + j * 0.43)), weight))
            colors.append(glow(c["background"], lights))
        return colors


class TrafficTrails(Effect):
    id = "traffic_trails"
    name = "Traffic Trails"
    category = "data"
    description = "Moving trails brighten and change color with each port's traffic."
    params = (
        Param("idle_color", "Idle color", "color", "#386fbd"),
        Param("busy_color", "Busy color", "color", "#75edc1"),
        Param("offline_color", "No link", "color", "#000000"),
        Param("speed", "Speed", "float", 0.2, min=0.03, max=2.0, step=0.01, unit="cycles/s"),
        Param("width", "Trail width", "float", 2.0, min=1.25, max=8.0, step=0.25, unit="ports"),
        Param("max_mbps", "Full scale", "float", 1000.0, min=1.0, max=10000.0, step=1.0, unit="Mbps"),
    )

    def setup(self) -> None:
        self._load = [0.0] * self.num_ports
        self._last: float | None = None
        self._online: list[bool | None] = [None] * self.num_ports
        self._shown: list[Color | None] = [None] * self.num_ports
        self._link_fades: dict[int, tuple[Color, float]] = {}

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        c, p = self.colors(t), self.phase(t, self.p["speed"])
        dt = 0.0 if self._last is None else max(0.0, t - self._last)
        self._last = t
        colors = []
        for i in range(self.num_ports):
            link = ctx.links.get(i + 1)
            online = link is None or link.link_up
            if self._online[i] is not None and online != self._online[i] and self._shown[i] is not None:
                self._link_fades[i] = (self._shown[i], t)
            self._online[i] = online
            if not online:
                self._load[i] = 0.0
                color = c["offline_color"]
            else:
                rate = 0.0 if link is not None and link.uplink else ctx.traffic_bps.get(i + 1, 0.0)
                target = min(1.0, max(0.0, rate / (self.p["max_mbps"] * 1e6)))
                tau = 0.35 if target > self._load[i] else 1.4
                self._load[i] += (target - self._load[i]) * (1 - math.exp(-dt / tau))
                pulse = (wave(p) if self.num_ports == 1 else
                         soft(ring_distance(i, p * self.num_ports, self.num_ports), self.p["width"]))
                level = 0.3 + 0.2 * pulse + 0.5 * pulse * self._load[i]
                color = weighted([(c["idle_color"], (1 - self._load[i]) * level),
                                  (c["busy_color"], self._load[i] * level)])
            if i in self._link_fades:
                start, started = self._link_fades[i]
                progress = (t - started) / 0.6
                if progress >= 1:
                    del self._link_fades[i]
                else:
                    color = start.lerp(color, ease(progress))
            self._shown[i] = color
            colors.append(color)
        return colors


class ActivityGlow(Effect):
    id = "activity_glow"
    name = "Activity Glow"
    category = "data"
    description = "Busier device ports glow brighter and gently settle as traffic falls."
    params = (
        Param("active_color", "Active color", "color", "#72e0bd"),
        Param("idle_color", "Idle color", "color", "#0b1523"),
        Param("minimum_mbps", "Full glow threshold", "float", 1.0, min=0.1, max=10000.0, step=0.1, unit="Mbps",
              description="The busiest device reaches full glow at this rate. "
                          "Above it, other ports scale relative to that device."),
        Param("speed", "Fade speed", "float", 1.0, min=0.25, max=4.0, step=0.05, unit="x"),
    )

    def setup(self) -> None:
        self._level = [0.0] * self.num_ports
        self._target = [0.0] * self.num_ports
        self._last: float | None = None

    def _advance(self, t: float, speed: float) -> None:
        dt = 0.0 if self._last is None else max(0.0, t - self._last)
        self._last = t
        for i, target in enumerate(self._target):
            tau = (0.25 if target > self._level[i] else 1.2) / speed
            self._level[i] += (target - self._level[i]) * (1 - math.exp(-dt / tau))

    def update_at(self, params: dict, t: float) -> None:
        previous_speed = self.p["speed"]
        super().update_at(params, t)
        self._advance(t, previous_speed)

    def render(self, t: float, ctx: RenderContext) -> list[Color]:
        self._advance(t, self.p["speed"])
        rates = [0.0] * self.num_ports
        for i in range(self.num_ports):
            link = ctx.links.get(i + 1)
            if link is not None and link.link_up and not link.uplink:
                rate = ctx.traffic_bps.get(i + 1, 0.0)
                if isinstance(rate, (int, float)) and math.isfinite(rate) and rate >= 0:
                    rates[i] = rate
        scale = max(self.p["minimum_mbps"] * 1e6, max(rates))
        self._target = [rate / scale for rate in rates]
        colors = self.colors(t)
        return [colors["idle_color"].lerp(colors["active_color"], level) for level in self._level]


AMBIENT_EFFECTS: tuple[type[Effect], ...] = (
    Aurora, LavaLamp, Plasma, OceanSwell, Eclipse, MeteorShower, Counterflow, ColorDissolve,
    InkSpread, AlternatingFade, ColorTide, PaletteMorph, EmberBed, Fireflies, TrafficTrails, ActivityGlow,
)
