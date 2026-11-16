"""Export deterministic backend effect frames for the browser and desktop preview contracts.

Usage: python tools/preview_fixtures.py <checkout> <folder>

Writes effect-frames.json, color-sources.json, traffic-trails-frames.json, activity-glow-frames.json and
poe-usage-frames.json, rendered by the engine in <checkout>/src. Pass tests/fixtures as the folder to refresh the
checked-in files; tools/check-fixtures.py compares them with a fresh export.
"""
import json
import sys
from datetime import datetime, timedelta
from pathlib import Path

sys.path.insert(0, str(Path(sys.argv[1]) / "src"))
from etherlighting_evolved.effects import EFFECTS  # noqa: E402
from etherlighting_evolved.effects.base import RenderContext  # noqa: E402
from etherlighting_evolved.links import PortLink  # noqa: E402

output = Path(sys.argv[2])


def write(name, data):
    (output / name).write_text(json.dumps(data, separators=(",", ":")) + "\n", encoding="utf-8", newline="\n")


variants = {
    "breathing": [{"ripple": True, "floor": 0}],
    "rainbow": [{"reverse": True, "spread": 2}],
    "comet": [{"bounce": True}, {"tail": 0.5}],
    "scanner": [{"width": 4}],
    "marquee": [{"spacing": 2}],
    "fire": [{"palette": p} for p in ("ice", "toxic", "plasma")],
    "police": [{"pattern": p} for p in ("alternate", "together")],
    "strobe": [{"random_colors": True}, {"rate": 4, "duty": 0.4},
               {"rate": 4, "duty": 0.6, "random_colors": True},
               {"random_colors": True, "brightness_color": 37}],
    "gradient": [{"speed": 0.1, "mirror": m} for m in (True, False)],
    "ripple": [{"center": 1, "spacing": 8}],
    "daylight": [{"palette": p} for p in ("warm", "vivid")],
    "aurora": [{"layers": 0.1}, {"speed": 1.0}],
    "lava_lamp": [{"width": 1.25}, {"width": 8.0, "speed": 0.8}],
    "plasma": [{"scale": 3.0}, {"scale": 0.5, "speed": 1.5}],
    "ocean_swell": [{"depth": 1.0}, {"speed": 1.0}],
    "eclipse": [{"width": 8.0}, {"width": 1.25, "depth": 1.0}],
    "meteor_shower": [{"count": 1, "tail": 1.25}, {"count": 4, "tail": 8.0}],
    "counterflow": [{"width": 8.0}, {"width": 1.25}],
    "color_dissolve": [{"stagger": 0.0}, {"stagger": 0.9}],
    "ink_spread": [{"spread": 0.1}, {"spread": 0.8}],
    "color_tide": [{"reverse": True}, {"width": 1.25}, {"width": 8.0}],
    "palette_morph": [{"drift": 0.0}, {"drift": 1.0}],
    "ember_bed": [{"depth": 1.0}, {"speed": 2.0}],
    "fireflies": [{"count": 1, "width": 1.25}, {"count": 4, "width": 5.0}],
}
frames = []
color_sources = []
for effect in EFFECTS.values():
    keys = [param.key for param in effect.params if param.type == "color"]
    color_variants = [{f"random_{key}": True} for key in keys]
    if len(keys) > 1:
        color_variants.append({f"random_{key}": True for key in keys})
    brightness_variants = []
    if keys:
        brightness_variants = [{f"brightness_{key}": 0 for key in keys},
                               {f"brightness_{key}": 37 for key in keys},
                               {**{f"random_{key}": True for key in keys},
                                **{f"brightness_{key}": (0, 37, 100)[index % 3] for index, key in enumerate(keys)}}]
    for randomized in color_variants + brightness_variants:
        for period in (1, 8, 30):
            for phase in (0, 0.25, 0.5, 0.9999, 1, 1.0001, 1.5, 2, 2.25):
                instance = effect(8, {**randomized, "color_transition": period}, seed=7)
                t = phase * period
                color_sources.append({"config": {"id": effect.id, "params": instance.params_json()}, "time": t,
                                      "colors": {key: value.to_hex() for key, value in instance.colors(t).items()}})
    if effect.id in ("starry_night", "lightning", "traffic", "traffic_trails", "activity_glow", "poe_usage"):
        continue
    for params in [{}] + variants.get(effect.id, []) + color_variants + brightness_variants:
        for count in (1, 8, 16):
            values = effect(count, params, seed=7).params_json()
            rate = values.get("speed", values.get("rate", 0))
            cycle = values.get("period", 1 / rate if rate else 8)
            if rate and effect.id == "comet":
                cycle = (2 * max(1, count - 1) if values["bounce"] else count) / rate
            elif rate and effect.id == "scanner":
                cycle = 2 * max(1, count - 1) / rate
            elif rate and effect.id in ("marquee", "ripple"):
                cycle = values["spacing"] / rate
            for t in (0, cycle * 0.01, cycle * 0.125, cycle * 0.25, cycle * 0.5, cycle * 0.75,
                      max(0, cycle - 0.001), cycle + 0.001):
                hours = (0, 5.9, 6, 6.5, 8, 20, 21.5, 22, 23.99) if effect.id == "daylight" else (12,)
                for hour in hours:
                    ctx = RenderContext(count, now=datetime(2026, 10, 30) + timedelta(hours=hour))
                    instance = effect(count, params, seed=7)
                    frames.append({"config": {"id": effect.id, "params": instance.params_json()},
                                   "count": count, "time": t, "hour": hour,
                                   "colors": [c.to_hex() for c in instance.render(t, ctx)]})
output.mkdir(parents=True, exist_ok=True)
write("effect-frames.json", frames)
write("color-sources.json", color_sources)
sequences = []
for count in (1, 8, 16):
    for params in ({}, {"speed": 0.7, "width": 1.25, "max_mbps": 100},
                   {"random_idle_color": True, "random_busy_color": True, "color_transition": 3},
                   {"brightness_idle_color": 25, "brightness_busy_color": 70, "brightness_offline_color": 40}):
        instance = EFFECTS["traffic_trails"](count, params, seed=7)
        sequence = {"config": {"id": instance.id, "params": instance.params_json()}, "count": count, "frames": []}
        for t in (0, 0.1, 0.4, 1, 2, 3, 4, 7.9, 8, 8.1, 8.2, 8.3, 8.5, 8.9, 10, 10.3, 10.6, 12, 14, 14.5, 20):
            facts = []
            links, traffic = {}, {}
            for index in range(count):
                link = None if 10 <= t < 12 else not (8 <= t < 10 and index % 3 == 0)
                if t == 8.2:
                    link = True
                mbps = (index + 1) * 180 if 2 <= t < 8 else 0
                uplink = index == 0 and t >= 8.3
                facts.append({"link": link, "traffic": mbps, "uplink": uplink})
                if link is not None:
                    links[index + 1] = PortLink(index + 1, True, link, 1000, True, uplink=uplink)
                traffic[index + 1] = mbps * 1e6
            ctx = RenderContext(count, links=links, traffic_bps=traffic)
            sequence["frames"].append({"time": t, "facts": facts,
                                       "colors": [color.to_hex() for color in instance.render(t, ctx)]})
        sequences.append(sequence)
write("traffic-trails-frames.json", sequences)
glow_sequences = []
for count in (1, 8, 16):
    for params in ({}, {"speed": 4, "minimum_mbps": 100},
                   {"speed": 0.25, "random_active_color": True, "color_transition": 3},
                   {"brightness_idle_color": 30, "brightness_active_color": 70}):
        instance = EFFECTS["activity_glow"](count, params, seed=7)
        sequence = {"config": {"id": instance.id, "params": instance.params_json()}, "count": count, "frames": []}
        for t in (0, 0.1, 0.4, 1, 2, 3, 4, 7.9, 8, 8.1, 8.1, 7.8, 8.2, 8.5, 9, 10, 10.3, 10.6, 12, 14, 20):
            facts, links, traffic = [], {}, {}
            for index in range(count):
                link = None if 10 <= t < 12 else not (8 <= t < 10 and index % 3 == 0)
                uplink = index == 0 and t >= 4
                mbps = (index + 1) * 180 if 2 <= t < 10 else 0
                if uplink:
                    mbps = 10000
                facts.append({"link": link, "uplink": uplink, "traffic": mbps})
                if link is not None:
                    links[index + 1] = PortLink(index + 1, True, link, 1000, True, uplink=uplink)
                traffic[index + 1] = mbps * 1e6
            ctx = RenderContext(count, links=links, traffic_bps=traffic)
            sequence["frames"].append({"time": t, "facts": facts,
                                       "colors": [color.to_hex() for color in instance.render(t, ctx)]})
        glow_sequences.append(sequence)
write("activity-glow-frames.json", glow_sequences)
print(f"Exported {len(frames)} deterministic effect frames.")
print(f"Exported {len(color_sources)} shared color-source cases.")
print(f"Exported {len(sequences)} traffic-trail sequences.")
print(f"Exported {len(glow_sequences)} activity-glow sequences.")

power_sequences = []
for count in (1, 8, 16):
    for params in ({}, {"speed": 0.1}, {"speed": 5},
                   {"random_low_color": True, "random_high_color": True, "color_transition": 3},
                   {"brightness_low_color": 25, "brightness_high_color": 70}):
        instance = EFFECTS["poe_usage"](count, params, seed=7)
        sequence = {"config": {"id": instance.id, "params": instance.params_json()}, "count": count, "frames": []}
        for t in (0, 0.1, 0.2, 0.5, 1, 2, 3, 4, 4, 4.1, 4.5, 5, 8, 10, 12, 20):
            facts, watts_by_port = [], {}
            for index in range(count):
                watts = (0.1, 1, 5, 15, 30, 60, 90, 0)[index % 8] if 0.1 <= t < 4 or t >= 10 else 0
                facts.append({"poe": "poe++" if watts else "off", "poe_power": watts})
                watts_by_port[index + 1] = watts
            ctx = RenderContext(count, poe_power_w=watts_by_port)
            sequence["frames"].append({"time": t, "facts": facts,
                                       "colors": [color.to_hex() for color in instance.render(t, ctx)]})
        power_sequences.append(sequence)
write("poe-usage-frames.json", power_sequences)
print(f"Exported {len(power_sequences)} PoE-usage sequences.")
