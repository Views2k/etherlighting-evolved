import json
from copy import deepcopy

import pytest
from fastapi.testclient import TestClient

from etherlighting_evolved.api import create_app
from etherlighting_evolved.port_names import PortNameStore


@pytest.fixture
def client(config):
    with TestClient(create_app(config, demo=True, autoconnect=False), base_url="http://127.0.0.1:8787") as client:
        yield client


def save(client, names, use_unifi=False, revision=None, **kwargs):
    if revision is None:
        revision = client.get("/api/port-names").json()["revision"]
    return client.put("/api/port-names", json={
        "expected_revision": revision, "use_unifi": use_unifi, "names": names}, **kwargs)


def test_manual_names_survive_service_restart_and_preserve_lighting(client, config):
    before = client.get("/api/scene").json()
    response = save(client, {"1": "  Desk & dock  ", "9": "Fiber uplink"})
    assert response.status_code == 200
    assert response.json()["names"] == {"1": "Desk & dock", "9": "Fiber uplink"}
    assert client.get("/api/ports").json()[0]["display_name"] == "Desk & dock"
    assert client.get("/api/state").json()["sfp"][0]["display_name"] == "Fiber uplink"
    assert client.get("/api/scene").json() == before
    restored = PortNameStore(config.data_dir / "port-names")
    assert restored.get("demo") == response.json()


def test_mode_changes_retain_manual_names_and_clear_restores_number(client):
    save(client, {"1": "Desk", "2": "Console"})
    auto = save(client, {}, use_unifi=True).json()
    assert auto["names"] == {"1": "Desk", "2": "Console"}
    assert client.get("/api/ports").json()[0]["name_mode"] == "unifi"
    save(client, {}, use_unifi=False)
    assert client.get("/api/ports").json()[0]["display_name"] == "Desk"
    cleared = save(client, {"1": " "}).json()
    assert cleared["names"] == {"2": "Console"}
    port = client.get("/api/ports").json()[0]
    assert port["display_name"] is None and port["name_mode"] == "manual"


def test_names_are_bound_to_device_and_stale_workspace_is_rejected(client, monkeypatch):
    workspace = client.get("/api/session").json()["workspace_id"]
    save(client, {"1": "Original"})
    monkeypatch.setattr(client.app.state.sessions, "schedule_identity", lambda: "other-device")
    assert client.get("/api/port-names").json()["names"] == {}
    assert client.get("/api/ports").json()[0]["display_name"] is None
    monkeypatch.setattr(client.app.state.sessions, "workspace_id", lambda: "next-workspace")
    assert save(client, {"1": "Wrong device"}, headers={"X-EE-Workspace": workspace}).status_code == 409
    assert client.get("/api/port-names").json()["names"] == {}
    monkeypatch.setattr(client.app.state.sessions, "schedule_identity", lambda: "demo")
    assert client.get("/api/port-names").json()["names"] == {"1": "Original"}


def test_concurrent_edit_keeps_first_saved_names(client):
    stale = client.get("/api/port-names").json()["revision"]
    save(client, {"1": "First"}, revision=stale)
    response = save(client, {"1": "Stale"}, revision=stale)
    assert response.status_code == 409 and response.json()["detail"]["code"] == "port_names_changed"
    assert client.get("/api/port-names").json()["names"] == {"1": "First"}


@pytest.mark.parametrize("names", [
    {"1": "a" * 65}, {"1": "two\nlines"}, {"1": "hidden\x00"}, {"1": "bidi\u202e"},
    {"1": 12}, {"1": None}, {"0": "Zero"}, {"11": "Absent"}, {"01": "Leading"},
    {"1.0": "Fraction"}, {"257": "Too far"}, [], None,
])
def test_invalid_names_do_not_write(client, names):
    before = client.get("/api/port-names").json()
    assert save(client, names).status_code == 400
    assert client.get("/api/port-names").json() == before


@pytest.mark.parametrize("mode", [1, "true", None])
def test_mode_requires_boolean(client, mode):
    assert save(client, {}, use_unifi=mode).status_code == 400


def test_corrupt_storage_is_not_overwritten_and_does_not_stop_frames(client, config):
    store = client.app.state.controller.port_names
    store.path("demo").parent.mkdir(parents=True, exist_ok=True)
    store.path("demo").write_text("{broken", encoding="utf-8")
    assert client.get("/api/port-names").status_code == 503
    assert save(client, {"1": "Lost"}, revision="0").status_code == 503
    assert client.get("/api/ports").status_code == 200
    assert store.path("demo").read_text() == "{broken"


def test_failed_write_keeps_saved_names_and_memory(client, monkeypatch):
    save(client, {"1": "Original"})
    store = client.app.state.controller.port_names
    disk = store.path("demo").read_bytes()

    def fail(*args):
        raise OSError("Fixture storage failure")

    monkeypatch.setattr("etherlighting_evolved.port_names.write_json", fail)
    assert save(client, {"1": "Unsaved"}).status_code == 503
    assert store.path("demo").read_bytes() == disk
    assert client.get("/api/ports").json()[0]["display_name"] == "Original"


def test_websocket_frames_use_same_names_as_rest_without_disk_reads(client, monkeypatch):
    save(client, {"1": "Desk", "9": "Fiber"})
    store = client.app.state.controller.port_names

    def no_read(*args):
        raise AssertionError("Frame delivery must use cached preferences")

    monkeypatch.setattr(store, "get", no_read)
    with client.websocket_connect("/api/ws", headers={"Host": "127.0.0.1:8787"}) as ws:
        for _ in range(20):
            message = ws.receive_json()
            if message["type"] == "frame":
                assert message["ports"][0]["display_name"] == "Desk"
                assert message["sfp"][0]["display_name"] == "Fiber"
                break
        else:
            pytest.fail("No named frame delivered")


def test_unifi_port_name_wins_without_changing_clients_or_input(tmp_path):
    store = PortNameStore(tmp_path)
    ports = [{"port": 1, "name": "Client", "facts": {"uplink": True},
              "controller": {"port_name": "Office uplink", "clients": [{"name": "Client"}]}}]
    before = deepcopy(ports)
    assert store.decorate(ports, "demo")[0]["display_name"] == "Office uplink"
    assert ports == before
    record = store.get("demo")
    store.put("demo", {"expected_revision": record["revision"], "use_unifi": False,
                       "names": {"1": "Manual"}}, {1})
    result = store.decorate(ports, "demo")[0]
    assert result["display_name"] == "Manual"
    assert result["controller"] == ports[0]["controller"]


def test_token_and_workspace_guards_apply(config):
    config.api.token = "x" * 32
    with TestClient(create_app(config, demo=True, autoconnect=False), base_url="http://127.0.0.1:8787") as client:
        assert client.get("/api/port-names").status_code == 401
        assert client.put("/api/port-names", json={}).status_code == 401
        response = client.get("/api/port-names", headers={"Authorization": "Bearer " + config.api.token})
        assert response.status_code == 200
        assert "names" in json.loads(response.text)
