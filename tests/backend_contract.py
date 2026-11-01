"""Exercise frontend payloads against the existing backend's in-process simulator."""
import json
from pathlib import Path
import sys
import tempfile
import faulthandler

source = Path(sys.argv[1])
sys.path.insert(0, str(source / "src"))
from fastapi.testclient import TestClient
from etherlighting_evolved.config import Config
from etherlighting_evolved.api import create_app

root = Path(__file__).resolve().parent.parent
catalog_text = (root / "src" / "catalog.mjs").read_text(encoding="utf-8")
catalog = json.loads(catalog_text.split("=", 1)[1].strip().removesuffix(";"))
(root / "outputs").mkdir(exist_ok=True)
checks = 0
faulthandler.dump_traceback_later(25, exit=True)

with tempfile.TemporaryDirectory(prefix="ee-contract-", dir=root / "outputs") as data_path:
    config = Config()
    config.data_dir = Path(data_path)
    config.base_dir = root
    config.api.static_dir = str(root)
    with TestClient(create_app(config, autoconnect=False), base_url="http://127.0.0.1") as client:
        def request(method, route, body=None):
            global checks
            response = client.request(method, "/api" + route, json=body)
            assert 200 <= response.status_code < 300, f"{method} {route}: HTTP {response.status_code}"
            checks += 1
            print(f"Verified {method} {route}", flush=True)
            return response.json()

        session = request("POST", "/session/login", {"host": "demo", "username": "demo", "password": "demo", "remember": False})
        assert session["logged_in"] and session["simulated"]
        state = request("PUT", "/sfp/9", {"mode": "color", "color": "#A1B2C3", "brightness": 65})
        assert state["sfp"][0]["color"] == "#a1b2c3" and state["sfp"][0]["brightness"] == 65
        state = request("PUT", "/sfp/10", {"mode": "off", "color": "#FFFFFF", "brightness": 100})
        assert state["sfp"][1]["mode"] == "off"
        state = request("PUT", "/sfp/9", {"mode": "unmanaged", "color": "#A1B2C3", "brightness": 65})
        assert state["sfp"][0]["mode"] == "unmanaged"
        remote_effects = request("GET", "/effects")
        assert {e["id"] for e in catalog} == {e["id"] for e in remote_effects}
        remote_by_id = {e["id"]: e for e in remote_effects}
        assert remote_by_id["traffic"]["name"] == "Live Traffic"
        assert "max_mbps" not in {p["key"] for p in remote_by_id["traffic"]["params"]}
        for effect in catalog:
            assert effect["params"] == remote_by_id[effect["id"]]["params"], f"Stale controls: {effect['id']}"
        for effect in catalog:
            params = {p["key"]: p["default"] for p in effect["params"]}
            state = request("PUT", "/effect", {"id": effect["id"], "params": params})
            assert state["zones"][0]["effect"]["id"] == effect["id"]

        state = request("PUT", "/effect", {"id": "marquee", "params": {
            "color": "#eab977", "background": "#122436", "random_background": True,
            "color_transition": 6.5, "brightness_color": 73, "brightness_background": 21,
        }})
        zone_id = state["zones"][0]["id"]
        state = request("PATCH", f"/zones/{zone_id}/params", {"params": {"color_transition": 3.5}})
        expected = state["zones"][0]["effect"]["params"]
        assert expected["color"] == "#eab977" and expected["random_color"] is False
        assert expected["background"] == "#122436" and expected["random_background"] is True
        assert expected["color_transition"] == 3.5
        assert expected["brightness_color"] == 73 and expected["brightness_background"] == 21
        request("POST", "/presets", {"name": "Random secondary"})
        request("PUT", "/effect", {"id": "static", "params": {"color": "#ffffff"}})
        state = request("POST", "/presets/Random%20secondary/apply")
        assert state["zones"][0]["effect"]["params"] == expected
        zone_id = state["zones"][0]["id"]
        state = request("PATCH", f"/zones/{zone_id}/params", {"params": {"random_background": False}})
        restored = state["zones"][0]["effect"]["params"]
        assert restored["background"] == "#122436" and restored["random_background"] is False
        assert restored["color"] == "#eab977"
        request("DELETE", "/presets/Random%20secondary")

        state = request("PUT", "/ports", {"ports": {"2": {"color": "#A1B2C3", "brightness": 75}}})
        assert state["overrides"]["2"]["color"].lower() == "#a1b2c3"
        request("DELETE", "/ports/2")
        zones = [
            {"id": "desk", "name": "Desk", "ports": [1, 2], "effect": {"id": "rainbow", "params": {}}},
            {"id": "other", "name": "Other ports", "ports": [3, 4, 5, 6, 7, 8], "effect": {"id": "static", "params": {"color": "#ffffff"}}},
        ]
        request("PUT", "/zones", {"zones": zones})
        state = request("PATCH", "/zones/desk/params", {"params": {"reverse": True}})
        assert state["zones"][0]["effect"]["params"]["reverse"]
        rules = [
            {"id": "alert", "name": "Anomaly", "enabled": True, "match": "all", "ports": None, "conditions": [{"field": "anomaly", "op": "is", "value": True}], "look": {"color": "#ffaa00", "style": "pulse", "rate": 1, "brightness": None}},
            {"id": "night", "name": "After hours", "enabled": True, "match": "all", "ports": None, "conditions": [{"field": "time", "op": "between", "value": ["22:00", "07:00"]}], "look": {"color": "#c6a0e5", "style": "solid", "rate": 1, "brightness": 25}},
            {"id": "power", "name": "PoE", "enabled": True, "match": "all", "ports": [1, 2], "conditions": [{"field": "poe", "op": "in", "value": ["poe+", "poe++"]}], "look": {"color": "#bb88ff", "style": "solid", "rate": 1, "brightness": 80}},
        ]
        assert len(request("PUT", "/rules", {"rules": rules})) == 3
        saved = request("POST", "/presets", {"name": "Contract test"})
        assert "Contract test" in saved
        state = request("POST", "/presets/Contract%20test/apply")
        assert len(state["zones"]) == 2 and len(state["rules"]) == 3
        request("DELETE", "/presets/Contract%20test")
        assert request("POST", "/release")["mode"] == "released"
        assert request("POST", "/resume")["mode"] == "running"
        with client.websocket_connect("ws://127.0.0.1/api/ws") as socket:
            messages = [socket.receive_json() for _ in range(3)]
            assert {m["type"] for m in messages} == {"session", "state", "frame"}
            checks += 1
        request("POST", "/session/logout")

faulthandler.cancel_dump_traceback_later()
print(f"{checks} backend contract checks passed; all device actions used the isolated simulator.")
