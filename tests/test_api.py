import time

import pytest
from fastapi.testclient import TestClient

from etherlighting_evolved.api import create_app
from etherlighting_evolved.fans import FanControl, FanStore, SimulatedFans

LOCAL = "http://127.0.0.1:8787"


@pytest.fixture
def client(config):
    with TestClient(create_app(config, autoconnect=False), base_url=LOCAL) as c:
        yield c


@pytest.fixture
def demo(client):
    r = client.post("/api/session/login", json={"host": "demo"})
    assert r.status_code == 200, r.text
    return client


def eventually(fn, timeout=2.0):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        if fn():
            return True
        time.sleep(0.05)
    return fn()


def test_a_fan_setting_that_cannot_be_saved_reports_fans_not_saved(demo, config, monkeypatch):
    engine = demo.app.state.sessions.engine
    store = FanStore(config.data_dir / "fans-fixture.json")

    def cannot_write(settings):
        raise OSError("fixture write failure")

    monkeypatch.setattr(store, "save", cannot_write)
    engine.attach_fans(FanControl(SimulatedFans(), store, supported=True))
    response = demo.put("/api/fans", json={"mode": "manual", "manual_percent": 60})
    assert response.status_code == 503
    assert response.json()["detail"]["code"] == "fans_not_saved"
    assert engine.fans.settings.mode == "manual"


def test_login_fields_and_request_lists_have_size_limits(demo):
    refused = demo.post("/api/session/login", json={"host": "h" * 254, "username": "fixture"})
    assert refused.status_code == 422 and refused.json()["detail"]["code"] == "invalid_credentials"
    assert demo.put("/api/zones", json={"zones": [{}] * 65}).status_code == 422
    rules = [{"id": f"rule-{n}", "name": f"Rule {n}", "look": {"color": "#ffffff"}} for n in range(257)]
    assert demo.put("/api/rules", json={"rules": rules}).status_code == 422
    assert demo.put("/api/rules", json={"rules": rules[:256]}).status_code == 200
    refused = demo.post("/api/rules", json=rules[256])
    assert refused.status_code == 400 and refused.json()["detail"]["code"] == "invalid"


def test_about_says_unofficial(client):
    about = client.get("/api/about").json()
    assert about["name"] == "Etherlighting Evolved" and about["short_name"] == "EE"
    assert "Not affiliated" in about["disclaimer"]


def test_catalogs_work_before_login(client):
    assert len(client.get("/api/effects").json()) == 33
    fields = client.get("/api/rules/fields").json()
    assert {"link", "poe", "anomaly", "time", "weekday"} <= {f["key"] for f in fields["fields"]}
    assert any(t["name"] == "PoE+++" for t in client.get("/api/rules/templates").json())
    assert client.get("/api/colors").json()["white"] == "#ffffff"


def test_everything_else_needs_a_login(client):
    r = client.get("/api/state")
    assert r.status_code == 409 and r.json()["detail"]["code"] == "not_logged_in"


@pytest.mark.parametrize("body, code", [
    ({"host": ""}, "missing_host"),
    ({"host": "10.0.0.1"}, "missing_username"),
])
def test_login_errors(client, body, code):
    r = client.post("/api/session/login", json=body)
    assert r.status_code == 400 and r.json()["detail"]["code"] == code


def test_demo_login(demo):
    session = demo.get("/api/session").json()
    assert session["logged_in"] and session["simulated"]
    assert session["switch"]["hostname"] == "USWProXG8PoE-SIM"
    state = demo.get("/api/state").json()
    assert state["num_ports"] == 8
    assert [r["name"] for r in state["rules"]] == ["Anomaly", "Offline"]
    assert state["zones"][0]["effect"]["id"] == "static"


def test_ports_report_link_poe_and_source(demo):
    assert eventually(lambda: demo.get("/api/ports").json()[1]["poe"] is not None)
    ports = demo.get("/api/ports").json()
    assert len(ports) == 8
    assert ports[1]["facts"]["poe"] == "poe+"
    assert ports[0]["source"]["kind"] == "zone"


def test_offline_port_turns_red(demo):
    demo.put("/api/simulator/links/4", json={"up": False})
    assert eventually(lambda: demo.get("/api/ports").json()[3]["color"] == "#ff0000")
    assert demo.get("/api/ports").json()[3]["source"]["name"] == "Offline"


def test_set_any_color_on_any_port(demo):
    r = demo.put("/api/ports/5", json={"color": "#ff00ff", "brightness": 60})
    assert r.status_code == 200
    assert r.json()["overrides"]["5"] == {"color": "#ff00ff", "brightness": 60}
    assert eventually(lambda: demo.get("/api/ports").json()[4]["source"]["kind"] == "override")
    port = demo.get("/api/ports").json()[4]
    assert port["color"] == "#ff00ff"
    assert port["output"] == {"color": "#530053", "level": 100}  # brightness is encoded in RGB
    assert demo.put("/api/ports/9", json={"color": "red"}).status_code == 400
    assert demo.put("/api/ports/1", json={"color": "not a color"}).status_code == 400
    demo.delete("/api/ports/5")
    assert demo.get("/api/state").json()["overrides"] == {}


def test_effects_and_zones(demo):
    state = demo.put("/api/effect", json={"id": "rainbow", "params": {"speed": 1}}).json()
    assert state["zones"][0]["effect"]["id"] == "rainbow"
    state = demo.put("/api/zones", json={"zones": [
        {"name": "Desk", "ports": [1, 2, 3, 4], "effect": {"id": "fire"}},
        {"name": "Rack", "ports": [5, 6, 7, 8], "effect": {"id": "comet", "params": {"color": "#30d158"}}},
    ]}).json()
    assert [z["id"] for z in state["zones"]] == ["desk", "rack"]
    r = demo.patch("/api/zones/rack/params", json={"params": {"speed": 12}})
    assert r.status_code == 200
    assert r.json()["zones"][1]["effect"]["params"]["speed"] == 12.0
    r = demo.patch("/api/zones/rack/params", json={"params": {"speed": -1}})
    assert r.status_code == 200
    assert r.json()["zones"][1]["effect"]["params"]["speed"] == 0.25
    assert demo.put("/api/zones/attic/effect", json={"id": "static"}).status_code == 404
    assert demo.put("/api/zones/desk/effect", json={"id": "disco"}).status_code == 400


def test_rules_crud(demo):
    templates = demo.get("/api/rules/templates").json()
    poe_plus = next(t for t in templates if t["name"] == "PoE+")
    rule = demo.post("/api/rules", json={**poe_plus, "position": 0}).json()
    rules = demo.get("/api/rules").json()
    assert rules[0]["id"] == rule["id"]
    assert eventually(lambda: demo.get("/api/ports").json()[1]["color"] == "#64d2ff")

    demo.patch(f"/api/rules/{rule['id']}", json={"look": {"color": "#ffffff", "style": "pulse"}})
    assert demo.get("/api/rules").json()[0]["look"]["style"] == "pulse"

    ids = [r["id"] for r in demo.get("/api/rules").json()]
    assert [r["id"] for r in demo.put("/api/rules/order", json={"ids": ids[::-1]}).json()] == ids[::-1]

    assert demo.delete(f"/api/rules/{rule['id']}").status_code == 200
    assert demo.delete(f"/api/rules/{rule['id']}").status_code == 404
    assert demo.post("/api/rules", json={"look": {"style": "wobble"}}).status_code == 400


def test_presets(demo):
    demo.put("/api/brightness", json={"value": 25})
    assert demo.post("/api/presets", json={"name": "Movie night"}).status_code == 201
    demo.put("/api/brightness", json={"value": 90})
    state = demo.post("/api/presets/Movie night/apply").json()
    assert state["brightness"] == 25
    assert demo.post("/api/presets/nope/apply").status_code == 404
    assert "Movie night" not in demo.delete("/api/presets/Movie night").json()


def test_schedules(demo):
    demo.post("/api/presets", json={"name": "Dim"})
    s = demo.post("/api/schedules", json={"name": "Bedtime", "at": "23:00",
                                          "action": {"type": "brightness", "value": 5}}).json()
    assert s["next_run"]
    assert demo.post(f"/api/schedules/{s['id']}/run").json()["result"] == "ok"
    assert demo.get("/api/state").json()["brightness"] == 5
    listing = demo.get("/api/schedules").json()
    assert listing["schedules"][0]["name"] == "Bedtime" and listing["history"]
    assert demo.patch(f"/api/schedules/{s['id']}", json={"at": "noon"}).status_code == 400
    assert demo.delete(f"/api/schedules/{s['id']}").json() == []


def test_scene_survives_a_restart(config):
    with TestClient(create_app(config, autoconnect=False), base_url=LOCAL) as c:
        c.post("/api/session/login", json={"host": "demo"})
        c.put("/api/effect", json={"id": "breathing"})
        c.put("/api/ports/2", json={"color": "#123456"})
    with TestClient(create_app(config, autoconnect=False), base_url=LOCAL) as c:
        c.post("/api/session/login", json={"host": "demo"})
        state = c.get("/api/state").json()
        assert state["zones"][0]["effect"]["id"] == "breathing"
        assert state["overrides"]["2"]["color"] == "#123456"


def test_websocket(demo):
    with demo.websocket_connect("/api/ws", headers={"Host": "127.0.0.1:8787"}) as ws:
        kinds = {ws.receive_json()["type"] for _ in range(3)}
        assert kinds == {"session", "state", "frame"}
        ws.send_json({"type": "set_port", "port": 3, "color": "#00ff00", "id": 7})
        for _ in range(50):
            msg = ws.receive_json()
            if msg["type"] == "ack":
                assert msg["id"] == 7
                break
        else:
            pytest.fail("no ack")
        ws.send_json({"type": "set_effect", "id": "nope", "id_": 1})
        for _ in range(50):
            msg = ws.receive_json()
            if msg["type"] == "error":
                assert msg["code"] == "invalid"
                break
        else:
            pytest.fail("no error")
    assert demo.get("/api/state").json()["overrides"]["3"]["color"] == "#00ff00"


def test_api_token(config):
    config.api.token = "s3cret"
    with TestClient(create_app(config, autoconnect=False), base_url=LOCAL) as c:
        assert c.get("/api/health").status_code == 401
        assert c.get("/api/health", headers={"Authorization": "Bearer s3cret"}).status_code == 200
