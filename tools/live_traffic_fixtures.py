"""Export Live Traffic frames for the browser and desktop preview contracts.

Usage: python tools/live_traffic_fixtures.py <checkout> <folder>

Writes live-traffic-frames.json, rendered by the engine in <checkout>/src. Pass tests/fixtures as the folder to
refresh the checked-in file; tools/check-fixtures.py compares it with a fresh export.
"""
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(sys.argv[1]) / "src"))
from etherlighting_evolved.effects import create_effect
from etherlighting_evolved.effects.base import RenderContext
from etherlighting_evolved.links import PortLink

cases = []
settings = [{"scale": scale, "smoothing": smoothing}
            for scale in ("linear", "log") for smoothing in (0, 0.6, 0.95)]
settings.extend({"smoothing": 0.6, "custom_uplink": True, "uplink_color": "#80c0ff",
                 "brightness_uplink_color": brightness, "random_uplink_color": random}
                for brightness in (0, 25, 100) for random in (False, True))
for params in settings:
    effect = create_effect("traffic", 4, params)
    frames = []
    for time in (0, 0.1, 0.5, 1, 1, 1.2, 2, 3, 3.5, 4, 5):
        facts = [{"link": time < 3 or time >= 4, "speed": speed, "uplink": index == 3,
                  "traffic": 50 if time < 2 else 500 if time < 4 else 0}
                 for index, speed in enumerate((100, 1000, 10000, 0))]
        links = {i + 1: PortLink(i + 1, True, fact["link"], fact["speed"], True, uplink=fact["uplink"])
                 for i, fact in enumerate(facts)}
        context = RenderContext(4, links=links,
                                traffic_bps={i + 1: fact["traffic"] * 1e6 for i, fact in enumerate(facts)})
        frames.append({"time": time, "facts": facts, "colors": [c.to_hex() for c in effect.render(time, context)]})
    cases.append({"config": {"id": "traffic", "params": {**effect.params_json(), **params}}, "frames": frames})
output = Path(sys.argv[2])
output.mkdir(parents=True, exist_ok=True)
(output / "live-traffic-frames.json").write_text(json.dumps(cases, separators=(",", ":")) + "\n", encoding="utf-8",
                                                 newline="\n")
print(f"Exported {len(cases)} Live Traffic sequences.")
