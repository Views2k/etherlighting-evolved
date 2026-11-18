"""Numeric scene compilation shared by the desktop and resident renderer."""
from __future__ import annotations

from .color import BLACK
from .effects import create_effect
from .effects.base import RenderContext
from .effects.library import DAYLIGHT_PALETTES
from .hardware import PROFILES, HardwareProfile


def compile_scene_data(engine, profile: HardwareProfile) -> tuple[str, str]:
    """Compile a canonical connector layout into numeric data, without device I/O.

    Compilation does not qualify a transport, sensor source, or startup mechanism.
    The transfer owner checks those capabilities separately.
    """
    if not isinstance(profile, HardwareProfile) or PROFILES.get(profile.key) is not profile:
        raise ValueError("A canonical hardware profile is required")
    ports = profile.copper_ports + profile.optical_ports
    if ports != tuple(range(1, len(ports) + 1)) or not 1 <= len(ports) <= 54:
        raise ValueError("The scene needs a contiguous connector layout")
    chosen = [port for zone in engine.zones for port in zone.ports]
    if (any(type(port) is not int or port not in ports for port in chosen)
            or len(chosen) != len(set(chosen))
            or any(type(port) is not int or port not in ports for port in engine.overrides)
            or any(type(port) is not int for port in engine.sfp)
            or not set(engine.sfp) <= set(profile.optical_ports)
            or any(port in profile.optical_ports and port not in engine.sfp for port in chosen)):
        raise ValueError("Scene ports do not match the hardware profile")
    if engine.sfp_sync.mode == "ethernet" and engine.sfp_sync.source_port not in profile.copper_ports:
        raise ValueError("The optical sync source must be a copper connector")
    settings = {p: [0, p, 0, 0, 1, engine.brightness, engine.gamma] for p in ports}
    for port in set(profile.optical_ports) - engine.sfp.keys():
        settings[port][1] = 0
    inputs = {}
    groups = []
    for group, zone in enumerate(engine.zones, 1):
        fx = create_effect(zone.effect.id, len(zone.ports), zone.effect.params_json())
        groups.append((zone.ports, fx, RenderContext(len(zone.ports))))
        for i, p in enumerate(zone.ports):
            if fx.id == "activity_glow":
                settings[p][:5] = [group, p, fx.p["minimum_mbps"], int(fx.p["include_uplinks"]), fx.p["speed"]]
            elif fx.id == "traffic":
                settings[p][3:5] = [int(fx.p["include_uplinks"]), fx.p["smoothing"]]
                inputs[p] = [1, int(fx.p["custom_uplink"])]
            elif fx.id == "traffic_trails":
                settings[p][2:5] = [fx.p["max_mbps"], int(fx.p["include_uplinks"]), fx.p["speed"]]
                inputs[p] = [2, i, len(zone.ports), fx.p["width"]]
            elif fx.id == "poe_usage":
                settings[p][4] = fx.p["speed"]
                inputs[p] = [3]
            elif fx.id == "daylight":
                stops = DAYLIGHT_PALETTES[fx.p["palette"]]
                inputs[p] = [4, fx.p["night_brightness"], len(stops)]
                for hour, color in stops:
                    inputs[p].extend([hour, *color.precise_channels])
            elif fx.category in {"time", "data"}:
                raise ValueError(f"{fx.name} has no offline sensor mapping.")
            if p in engine.sfp:
                settings[p][5] *= engine.sfp[p].brightness / 100
    grouped = {p for ports, _, _ in groups for p in ports}
    independent = {}
    for p in engine.sfp.keys() - grouped:
        if engine.sfp_sync.mode == "ethernet":
            source = engine.sfp_sync.source_port
            settings[p][1] = source
            settings[p][5] = settings[source][5]
        else:
            light = engine.sfp[min(engine.sfp) if engine.sfp_sync.mode == "together" else p]
            if light.mode == "unmanaged":
                settings[p][1] = 0
            else:
                independent[p] = light
                settings[p][5] *= light.brightness / 100
    for p, override in engine.overrides.items():
        inputs.pop(p, None)
        settings[p][:2] = [0, p]
        if override.brightness is not None:
            settings[p][5] = override.brightness
    for p in engine.sfp.keys() - grouped:
        if engine.sfp_sync.mode == "ethernet":
            settings[p][5] = settings[engine.sfp_sync.source_port][5]
    rows = []
    for index in range(120):
        t = index / 4
        colors = {p: (BLACK, BLACK) for p in settings}
        extra = {p: (BLACK, BLACK) for p in settings}
        for ports, fx, ctx in groups:
            if fx.id == "activity_glow":
                palette = fx.colors(t)
                for p in ports:
                    colors[p] = palette["idle_color"], palette["active_color"]
            elif fx.id in {"traffic", "traffic_trails", "poe_usage"}:
                palette = fx.colors(t)
                for p in ports:
                    if fx.id == "poe_usage":
                        colors[p] = palette["no_power_color"], palette["low_color"]
                        extra[p] = palette["high_color"], BLACK
                    else:
                        colors[p] = palette["idle_color"], palette["busy_color"]
                        extra[p] = palette["offline_color"], palette.get("uplink_color", BLACK)
            elif fx.id != "daylight":
                for p, color in zip(ports, fx.render(t, ctx), strict=True):
                    colors[p] = color, color
        for p, light in independent.items():
            color = light.render(t, p)
            colors[p] = color, color
        for p, override in engine.overrides.items():
            colors[p] = override.color, override.color
        pairs = list(colors.values())
        if any(fields[0] in {1, 2, 3} for fields in inputs.values()):
            pairs.extend(extra.values())
        channels = (c for pair in pairs for col in pair for c in col.precise_channels)
        rows.append(" ".join(format(c, ".7g") for c in channels))
    if all(row == rows[0] for row in rows):
        rows = rows[:1]
    limit = max(1, min(10, int(engine.max_commands)))
    config = "".join(" ".join(str(n) for n in [p, *values, limit, *inputs.get(p, [])]) + "\n"
                     for p, values in settings.items())
    return config, "\n".join(rows) + "\n"

