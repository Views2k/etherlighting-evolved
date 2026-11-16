import json

import pytest
from test_session_restore import LocalTransport

from etherlighting_evolved import storage
from etherlighting_evolved.session import SessionManager


def test_atomic_write_replaces_the_file_and_leaves_no_temporary_file(tmp_path):
    path = tmp_path / "nested" / "store.json"
    storage.atomic_write_json(path, {"b": 1, "a": [1, 2]})
    storage.atomic_write_json(path, {"b": 2})
    assert json.loads(path.read_text("utf-8")) == {"b": 2}
    assert [item.name for item in path.parent.iterdir()] == ["store.json"]


def test_failed_replace_keeps_the_previous_file_and_removes_the_temporary_file(tmp_path, monkeypatch):
    path = tmp_path / "store.json"
    storage.atomic_write_json(path, {"saved": True})
    original = path.read_bytes()

    def fail(*args):
        raise PermissionError("fixture storage failure")

    monkeypatch.setattr(storage.os, "replace", fail)
    with pytest.raises(PermissionError):
        storage.atomic_write_json(path, {"saved": False})
    assert path.read_bytes() == original
    assert not list(tmp_path.glob("*.tmp"))


def test_failed_flush_to_disk_keeps_the_previous_file(tmp_path, monkeypatch):
    path = tmp_path / "store.json"
    storage.atomic_write_bytes(path, b"previous")

    def fail(descriptor):
        raise OSError("fixture storage failure")

    monkeypatch.setattr(storage.os, "fsync", fail)
    with pytest.raises(OSError):
        storage.atomic_write_bytes(path, b"next")
    assert path.read_bytes() == b"previous"
    assert not list(tmp_path.glob("*.tmp"))


@pytest.mark.anyio
@pytest.mark.parametrize("content", [b"", b'{"version": 1, "identity": "other", "scene": {"brightness": 5}}'])
async def test_unusable_device_scene_is_moved_aside_and_login_offers_the_scene_choice(config, caplog, content):
    sessions = SessionManager(config, transport_factory=lambda **kwargs: LocalTransport())
    try:
        pending = (await sessions.connect("switch.invalid", "fixture", "fixture-only"))["pending_scene_choice"]
        await sessions.choose_scene(pending["connection_id"], "start_new", pending["legacy_revision"])
        await sessions.disconnect()
        [path] = (config.data_dir / "device-scenes").glob("*.json")
        path.write_bytes(content)
        info = await sessions.connect("switch.invalid", "fixture", "fixture-only")
        assert info["pending_scene_choice"] and not info["logged_in"] and info["error"] is None
        [moved] = path.parent.glob(path.name + ".corrupt-*")
        assert moved.read_bytes() == content and not path.exists()
        assert str(moved) in caplog.text
        pending = info["pending_scene_choice"]
        info = await sessions.choose_scene(pending["connection_id"], "start_new", pending["legacy_revision"])
        assert info["logged_in"] and path.exists()
    finally:
        await sessions.close()
