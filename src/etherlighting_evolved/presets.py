"""Small JSON stores: named presets, and the scene that was showing when EE last ran."""

from __future__ import annotations

import json
import os
import re
from pathlib import Path

_NAME_RE = re.compile(r"^[\w .\-]{1,64}$")


def write_json(path: Path, data) -> None:
    """Write through a temporary file so a crash never leaves half a file behind."""
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(path.suffix + ".tmp")
    tmp.write_text(json.dumps(data, indent=2, sort_keys=True), "utf-8")
    os.replace(tmp, path)


def read_json(path: Path, default):
    try:
        data = json.loads(Path(path).read_text("utf-8"))
    except (OSError, json.JSONDecodeError):
        return default
    return data if isinstance(data, type(default)) else default


class PresetStore:
    """Named scenes (zones, rules, brightness, overrides)."""

    def __init__(self, path: Path) -> None:
        self.path = Path(path)

    def _load(self) -> dict[str, dict]:
        return read_json(self.path, {})

    def _save(self, data: dict[str, dict]) -> None:
        write_json(self.path, data)

    def list(self) -> dict[str, dict]:
        return self._load()

    def get(self, name: str) -> dict:
        data = self._load()
        if name not in data:
            raise KeyError(name)
        return data[name]

    def save(self, name: str, snapshot: dict) -> None:
        name = name.strip()
        if not _NAME_RE.match(name):
            raise ValueError("preset names are 1-64 letters, digits, spaces, '.', '_' or '-'")
        data = self._load()
        data[name] = snapshot
        self._save(data)

    def delete(self, name: str) -> None:
        data = self._load()
        if name not in data:
            raise KeyError(name)
        del data[name]
        self._save(data)


class SceneStore:
    """The live scene, saved whenever it changes so a restart picks up where it left off."""

    def __init__(self, path: Path) -> None:
        self.path = Path(path)

    def load(self) -> dict | None:
        return read_json(self.path, {}) or None

    def save(self, scene: dict) -> None:
        write_json(self.path, scene)
