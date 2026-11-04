import json
from copy import deepcopy

import pytest
from fastapi.testclient import TestClient

from etherlighting_evolved.api import create_app
from etherlighting_evolved.lighting_library import LibraryStore


@pytest.fixture
def library(config):
    with TestClient(create_app(config, demo=True, autoconnect=False), base_url="http://127.0.0.1:8787") as client:
        yield client


def save(client, kind="effect", name="Quiet rack", value=None):
    value = {"id": "static", "params": {"color": "#123456"}} if value is None else value
    return client.post(f"/api/library/{kind}", json={"name": name, "value": value})


def test_named_effect_configs_survive_reload_without_changing_lighting(library, config):
    before = library.get("/api/scene").json()
    first = save(library, name="Quiet rack", value={"id": "activity_glow", "params": {"include_uplinks": False}})
    second = save(library, name="Busy rack", value={"id": "activity_glow", "params": {"include_uplinks": True}})
    assert first.status_code == second.status_code == 201
    items = library.get("/api/library/effect").json()["items"]
    assert len(items) == 2 and items[0]["id"] != items[1]["id"]
    assert {item["value"]["params"]["include_uplinks"] for item in items} == {False, True}
    assert library.get("/api/scene").json() == before
    assert LibraryStore(config.data_dir / "lighting-library.json").list("effect", None) == items


@pytest.mark.parametrize("value", [
    {"id": "static", "params": {"color": "invalid"}},
    {"id": "static", "params": {"unknown_parameter": True}},
    {"id": "does_not_exist", "params": {}},
    {"id": "static", "params": {}, "ports": [1]},
    {"id": "static", "params": {}, "rules": []},
    {"id": "static", "params": []},
])
def test_saved_effect_settings_reject_invalid_or_scene_fields(library, value):
    before = library.get("/api/scene").json()
    assert save(library, value=value).status_code == 400
    assert library.get("/api/library/effect").json()["items"] == []
    assert library.get("/api/scene").json() == before


@pytest.mark.parametrize("name", [None, True, 12, [], {}])
def test_library_names_must_be_text(library, name):
    assert save(library, name=name).status_code == 400
    assert library.get("/api/library/effect").json()["items"] == []


def test_saved_selection_preserves_scene_and_is_bound_to_switch(library, monkeypatch):
    before = library.get("/api/scene").json()
    response = save(library, "selection", "Rack uplinks", {"ports": [9, 1, 9]})
    assert response.status_code == 201, response.text
    selection = response.json()
    assert selection["value"]["ports"] == [1, 9]
    assert selection["value"]["topology"] == before["scene"]["topology"]
    assert library.get("/api/library/selection").json()["items"] == [selection]
    assert library.get("/api/scene").json() == before
    monkeypatch.setattr(library.app.state.sessions, "schedule_identity", lambda: "other-switch")
    assert library.get("/api/library/selection").json()["items"] == []
    changed = library.put("/api/library/selection/" + selection["id"], json={
        "name": "Changed", "value": {"ports": [1]}, "expected_revision": selection["revision"]})
    assert changed.status_code == 409
    assert library.get("/api/scene").json() == before


@pytest.mark.parametrize("ports", [[], [0], [11], [True], [1.5], ["1"]])
def test_saved_selection_validates_actual_lighting_ports(library, ports):
    assert save(library, "selection", "Selected", {"ports": ports}).status_code == 400
    assert library.get("/api/library/selection").json()["items"] == []


def test_library_edits_and_deletes_require_current_revision_without_resurrection(library):
    first = save(library).json()
    body = {"name": "Renamed", "value": first["value"], "expected_revision": first["revision"]}
    response = library.put("/api/library/effect/" + first["id"], json=body)
    assert response.status_code == 200
    changed = response.json()
    assert changed["id"] == first["id"] and changed["revision"] != first["revision"]
    assert changed["value"] == first["value"]
    assert library.put("/api/library/effect/" + first["id"], json=body).status_code == 409
    stale_delete = library.request("DELETE", "/api/library/effect/" + first["id"], json={
        "expected_revision": first["revision"]})
    assert stale_delete.status_code == 409
    assert library.get("/api/library/effect").json()["items"] == [changed]
    assert library.request("DELETE", "/api/library/effect/" + first["id"], json={
        "expected_revision": changed["revision"]}).status_code == 200
    body["expected_revision"] = changed["revision"]
    assert library.put("/api/library/effect/" + first["id"], json=body).status_code == 409
    assert library.get("/api/library/effect").json()["items"] == []


def test_duplicate_name_is_rejected_without_replacing_saved_value(library):
    first = save(library, name="Quiet rack").json()
    assert save(library, name="QUIET RACK", value={"id": "static", "params": {"color": "#abcdef"}}).status_code == 409
    assert library.get("/api/library/effect").json()["items"] == [first]


@pytest.mark.parametrize("item", [
    {"id": "broken"},
    {"id": {"invalid": "identity"}},
    {"id": "saved", "revision": "one", "name": "Saved", "kind": "effect", "binding": None, "value": {}},
])
def test_library_invalid_storage_is_retained_and_has_an_actionable_failure(library, config, item):
    path = config.data_dir / "lighting-library.json"
    original = json.dumps({"version": 1, "items": [item]})
    path.write_text(original, encoding="utf-8")
    response = library.get("/api/library/effect")
    assert response.status_code == 503, response.text
    assert "saved" in response.json()["detail"]["message"].lower()
    assert path.read_text("utf-8") == original


def test_library_failed_write_preserves_saved_items(library, monkeypatch):
    import etherlighting_evolved.lighting_library as module

    first = save(library).json()
    before = deepcopy(library.get("/api/library/effect").json())

    def denied(*_args, **_kwargs):
        raise PermissionError("blocked fixture")

    monkeypatch.setattr(module, "write_json", denied)
    body = {"name": "Changed", "value": first["value"], "expected_revision": first["revision"]}
    assert library.put("/api/library/effect/" + first["id"], json=body).status_code == 503
    assert library.get("/api/library/effect").json() == before
