import json

import pytest

from etherlighting_evolved import storage


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
