from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from etherlighting_evolved.api import create_app
from etherlighting_evolved.presets import PresetStore, ProfileStoreError, write_json


def test_duplicate_and_rename_preserve_saved_content(tmp_path):
    store = PresetStore(tmp_path / "profiles.json")
    scene = {"brightness": 37, "overrides": {"1": {"color": "#123456"}}, "zones": [], "rules": [], "sfp": {}}
    store.save("Evening", scene)
    store.duplicate("Evening", "  Evening copy  ")
    assert store.get("Evening copy") == scene
    store.rename("Evening copy", "Night")
    assert store.list() == {"Evening": scene, "Night": scene}


@pytest.mark.parametrize("operation", ["rename", "duplicate"])
def test_transfer_never_overwrites_existing_target(tmp_path, operation):
    path = tmp_path / "profiles.json"
    store = PresetStore(path)
    store.save("A", {"brightness": 10})
    store.save("B", {"brightness": 90})
    original = path.read_bytes()
    with pytest.raises(FileExistsError):
        getattr(store, operation)("A", " B ")
    assert path.read_bytes() == original


@pytest.mark.parametrize("target", ["", "  ", ".", "..", "A" * 65, "Desk / Night", "Desk: Night", "Desk\nNight"])
def test_transfer_validates_canonical_target(tmp_path, target):
    path = tmp_path / "profiles.json"
    store = PresetStore(path)
    store.save("A", {"brightness": 10})
    original = path.read_bytes()
    with pytest.raises(ValueError):
        store.rename("A", target)
    assert path.read_bytes() == original


def test_failed_atomic_replace_preserves_source(tmp_path, monkeypatch):
    path = tmp_path / "profiles.json"
    store = PresetStore(path)
    store.save("A", {"brightness": 10})
    original = path.read_bytes()

    def fail_replace(*args):
        raise OSError("Disk unavailable")

    monkeypatch.setattr("etherlighting_evolved.presets.os.replace", fail_replace)
    with pytest.raises(OSError):
        store.rename("A", "B")
    assert path.read_bytes() == original
    assert store.list() == {"A": {"brightness": 10}}


def test_concurrent_duplicates_accept_exactly_one_target(tmp_path):
    store = PresetStore(tmp_path / "profiles.json")
    store.save("A", {"brightness": 10})

    def duplicate(_):
        try:
            store.duplicate("A", "B")
            return True
        except FileExistsError:
            return False

    with ThreadPoolExecutor(max_workers=4) as pool:
        assert sum(pool.map(duplicate, range(4))) == 1
    assert store.list() == {"A": {"brightness": 10}, "B": {"brightness": 10}}


@pytest.mark.parametrize("content", [b'{"Existing":{"brightness":73},', b'[]', b'{"Existing":null}', b'\xff'])
@pytest.mark.parametrize("operation,args", [
    ("list", ()), ("get", ("Existing",)), ("save", ("New", {"brightness": 10})),
    ("delete", ("Existing",)), ("rename", ("Existing", "New")), ("duplicate", ("Existing", "New")),
])
def test_invalid_profile_storage_is_preserved_for_every_operation(tmp_path, content, operation, args):
    path = tmp_path / "profiles.json"
    path.write_bytes(content)
    store = PresetStore(path)
    with pytest.raises(ProfileStoreError):
        getattr(store, operation)(*args)
    assert path.read_bytes() == content
    assert not path.with_suffix(".json.tmp").exists()


def test_unreadable_profiles_are_preserved_and_recovery_allows_normal_save(tmp_path, monkeypatch):
    path = tmp_path / "profiles.json"
    store = PresetStore(path)
    store.save("Existing", {"brightness": 73})
    original = path.read_bytes()
    read = Path.read_text
    with monkeypatch.context() as patch:
        def denied(candidate, *args, **kwargs):
            if candidate == path:
                raise PermissionError("fixture private path")
            return read(candidate, *args, **kwargs)
        patch.setattr(Path, "read_text", denied)
        with pytest.raises(ProfileStoreError) as error:
            store.save("New", {"brightness": 10})
        assert "fixture private path" not in str(error.value)
        assert path.read_bytes() == original
    store.save("New", {"brightness": 10})
    assert store.list() == {"Existing": {"brightness": 73}, "New": {"brightness": 10}}


def test_missing_profile_store_is_empty_and_explicit_file_repair_restores_operations(tmp_path):
    path = tmp_path / "profiles.json"
    store = PresetStore(path)
    assert store.list() == {}
    path.write_text("invalid", encoding="utf-8")
    with pytest.raises(ProfileStoreError):
        store.list()
    write_json(path, {"Recovered": {"brightness": 73}})
    store.duplicate("Recovered", "Copy")
    assert store.get("Copy") == {"brightness": 73}


@pytest.mark.parametrize("method,url,body", [
    ("GET", "/api/presets", None),
    ("POST", "/api/presets", {"name": "New"}),
    ("POST", "/api/presets/Existing/rename", {"name": "New"}),
    ("POST", "/api/presets/Existing/duplicate", {"name": "New"}),
    ("DELETE", "/api/presets/Existing", None),
])
def test_profile_storage_errors_reach_api_without_exposing_or_replacing_data(config, method, url, body):
    app = create_app(config, autoconnect=False)
    with TestClient(app, base_url="http://localhost") as client:
        assert client.post("/api/session/login", json={"host": "demo"}).status_code == 200
        path = app.state.presets.path
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(b'{"PRIVATE_PROFILE_MARKER":')
        response = client.request(method, url, json=body)
        assert response.status_code == 409
        assert response.json()["detail"]["code"] == "profile_storage_unavailable"
        assert "PRIVATE_PROFILE_MARKER" not in response.text and str(path) not in response.text
        assert path.read_bytes() == b'{"PRIVATE_PROFILE_MARKER":'


def test_profile_routes_organize_store_without_switch_session(config):
    app = create_app(config, autoconnect=False)
    with TestClient(app, base_url="http://127.0.0.1:8787") as client:
        snapshot = {"brightness": 42, "overrides": {"1": {"color": "#123456"}}}
        app.state.presets.save("A", snapshot)
        assert client.get("/api/state").status_code == 409
        response = client.post("/api/presets/A/duplicate", json={"name": "B"})
        assert response.status_code == 201
        assert response.json() == {"A": snapshot, "B": snapshot}
        response = client.post("/api/presets/B/rename", json={"name": "C"})
        assert response.status_code == 200
        assert response.json() == {"A": snapshot, "C": snapshot}
        assert client.post("/api/presets/A/rename", json={"name": "C"}).status_code == 409
        assert client.post("/api/presets/missing/duplicate", json={"name": "D"}).status_code == 404
        assert client.post("/api/presets/A/duplicate", json={"name": "Bad/Name"}).status_code == 400
        assert client.get("/api/state").status_code == 409
        assert app.state.presets.list() == {"A": snapshot, "C": snapshot}


@pytest.mark.parametrize("name", [".", ".."])
def test_reserved_profile_names_cannot_be_created_and_legacy_records_are_kept(config, name):
    app = create_app(config, autoconnect=False)
    with TestClient(app, base_url="http://localhost") as client:
        client.post("/api/session/login", json={"host": "demo"})
        snapshot = client.get("/api/scene").json()["scene"]
        snapshot["brightness"] = 24
        write_json(app.state.presets.path, {name: snapshot, "Copy": snapshot})
        assert client.post("/api/presets", json={"name": name}).status_code == 400
        assert client.post("/api/presets/Copy/rename", json={"name": name}).status_code == 400
        assert client.post("/api/presets/Copy/duplicate", json={"name": name}).status_code == 400
        assert app.state.presets.list() == {name: snapshot, "Copy": snapshot}
