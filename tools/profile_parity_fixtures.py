"""Export the colors the engine shows for saved profiles, for the browser and desktop preview contracts.

Usage: python tools/profile_parity_fixtures.py <checkout> <folder>

Writes profile-colors.json, composed by the engine in <checkout>/src. Pass tests/fixtures as the folder to refresh
the checked-in file; tools/check-fixtures.py compares it with a fresh export.
"""

import json
import sys
from copy import deepcopy
from datetime import datetime
from pathlib import Path

sys.path.insert(0, str(Path(sys.argv[1]).resolve() / "src"))

from etherlighting_evolved.engine import Engine  # noqa: E402
from etherlighting_evolved.switch import EtherlightSwitch  # noqa: E402
from etherlighting_evolved.transport import SimulatedTransport  # noqa: E402


def base():
    return {
        "brightness": 20,
        "zones": [{"id": "all", "ports": [1], "effect": {"id": "static", "params": {"color": "#00ff00"}}}],
        "overrides": {},
        "rules": [
            {
                "id": "sfp-level",
                "name": "SFP level",
                "enabled": True,
                "ports": [9],
                "conditions": [],
                "look": {"style": "effect", "brightness": 80, "color": "#ff0000"},
            }
        ],
        "sfp": {"9": {"mode": "color", "effect": "steady", "color": "#0000ff", "brightness": 100}},
        "sfp_sync": {"mode": "independent"},
    }


cases = []
for master in (0, 20):
    scene = base()
    scene["brightness"] = master
    cases.append((f"SFP rule replaces master {master}", scene))

scene = base()
scene["sfp_sync"] = {"mode": "ethernet", "source_port": 1}
scene["overrides"] = {"1": {"color": "#00ff00", "brightness": 80}}
scene["rules"][0]["look"]["brightness"] = 40
cases.append(("SFP follows source override before replacing its brightness", scene))

scene = base()
scene["sfp"]["9"]["brightness"] = 50
scene["rules"][0]["look"] = {"style": "solid", "color": "#ff0000", "brightness": 80}
cases.append(("SFP solid rule retains per-light brightness", scene))

scene = base()
scene["rules"] = []
cases.append(("Ungrouped copper starts dark", scene))

scene = base()
scene["rules"] = [
    {
        "id": "ungrouped",
        "name": "Ungrouped rule",
        "ports": [2, 3],
        "conditions": [],
        "look": {"style": "solid", "color": "#ff0000", "brightness": 60},
    }
]
scene["overrides"] = {"3": {"color": "#0000ff", "brightness": None}, "4": {"color": "#ffffff", "brightness": 30}}
cases.append(("Ungrouped ports apply rules and inherited or explicit overrides", scene))

scene = base()
scene["sfp"]["9"]["brightness"] = 50
scene["sfp"]["10"] = {"mode": "off", "brightness": 1}
scene["sfp_sync"] = {"mode": "together"}
scene["rules"][0]["ports"] = [10]
cases.append(("Together uses shared light settings with independently targeted rules", scene))

scene = base()
scene["sfp"]["9"]["mode"] = "off"
scene["rules"][0]["look"] = {"style": "solid", "color": "#ff0000", "brightness": 80}
cases.append(("Off SFP suppresses targeted rules", scene))

scene = base()
scene["sfp"]["9"]["mode"] = "unmanaged"
cases.append(("Effect-only rule does not acquire unmanaged SFP", scene))

scene = base()
scene["rules"][0]["ports"] = None
scene["rules"][0]["look"] = {"style": "solid", "color": "#ff0000", "brightness": 80}
cases.append(("All-copper rule does not acquire SFP", scene))

result = []
for name, scene in cases:
    switch = EtherlightSwitch(SimulatedTransport(latency=0))
    switch.sfp_ports = (9, 10)
    engine = Engine(switch, transition=0, rules=[])
    engine.apply_snapshot(deepcopy(scene))
    engine.compose(t=0, clock=0, now=datetime(2026, 10, 31, 12))
    colors = {str(port): engine._display_rgb(*engine._shown[port - 1]) for port in range(1, engine.num_ports + 1)}
    colors.update({str(port): engine._display_rgb(*engine._sfp_color(port, 0)) for port in engine.sfp})
    result.append(
        {"name": name, "scene": engine.snapshot(), "state": {"num_ports": engine.num_ports}, "colors": colors}
    )

output = Path(sys.argv[2])
output.mkdir(parents=True, exist_ok=True)
(output / "profile-colors.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8", newline="\n")
