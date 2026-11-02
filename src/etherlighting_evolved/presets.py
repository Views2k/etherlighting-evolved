"""Small JSON stores: named presets, and the scene that was showing when EE last ran."""

from __future__ import annotations

import json
import os
import re
from pathlib import Path
from threading import RLock

_NAME_RE = re.compile(r"^[\w .\-]{1,64}$")


class ProfileStoreError(OSError):
    """Existing profiles could not be read or a change could not be saved."""

    code = "profile_storage_unavailable"


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
        self._lock = RLock()

    def _load(self) -> dict[str, dict]:
        try:
            data = json.loads(self.path.read_text("utf-8"))
        except FileNotFoundError:
            return {}
        except (OSError, ValueError) as exc:
            raise ProfileStoreError("Saved profiles could not be read. Check the data folder and "
                                    "restore a valid profile file before editing profiles.") from exc
        if not isinstance(data, dict) or any(not isinstance(scene, dict) for scene in data.values()):
            raise ProfileStoreError("The saved profile file is invalid. Restore a valid copy before editing profiles.")
        return data

    def _save(self, data: dict[str, dict]) -> None:
        try:
            write_json(self.path, data)
        except OSError as exc:
            raise ProfileStoreError("The profile change could not be saved. "
                                    "Check the data folder and try again.") from exc

    def list(self) -> dict[str, dict]:
        with self._lock:
            return self._load()

    def get(self, name: str) -> dict:
        with self._lock:
            data = self._load()
            if name not in data:
                raise KeyError(name)
            return data[name]

    def save(self, name: str, snapshot: dict, *, overwrite: bool = True) -> None:
        name = self._name(name)
        with self._lock:
            data = self._load()
            if not overwrite and name in data:
                raise FileExistsError("A profile with this name already exists.")
            data[name] = snapshot
            self._save(data)

    def delete(self, name: str) -> None:
        with self._lock:
            data = self._load()
            if name not in data:
                raise KeyError(name)
            del data[name]
            self._save(data)

    @staticmethod
    def _name(name: str, *, allow_legacy: bool = False) -> str:
        name = name.strip()
        if not allow_legacy and name in {".", ".."}:
            raise ValueError("Choose a profile name other than '.' or '..'.")
        if not _NAME_RE.fullmatch(name):
            raise ValueError("Profile names use 1-64 letters, numbers, spaces, periods, underscores, or hyphens.")
        return name

    def duplicate(self, source: str, target: str) -> None:
        self._transfer(source, target, remove_source=False)

    def rename(self, source: str, target: str) -> None:
        self._transfer(source, target, remove_source=True)

    def _transfer(self, source: str, target: str, *, remove_source: bool) -> None:
        source, target = self._name(source, allow_legacy=True), self._name(target)
        with self._lock:
            data = self._load()
            if source not in data:
                raise KeyError(source)
            if target in data:
                raise FileExistsError("A profile with this name already exists.")
            data[target] = data[source]
            if remove_source:
                del data[source]
            self._save(data)


class SceneStore:
    """The live scene, saved whenever it changes so a restart picks up where it left off."""

    def __init__(self, path: Path) -> None:
        self.path = Path(path)

    def load(self) -> dict | None:
        return read_json(self.path, {}) or None

    def save(self, scene: dict) -> None:
        write_json(self.path, scene)


class DeviceSceneStore(SceneStore):
    """A scene explicitly assigned to an authenticated device and qualified port layout."""

    def __init__(self, path: Path, identity: str) -> None:
        super().__init__(path)
        self.identity = identity

    def load(self) -> dict | None:
        if not self.path.exists():
            return None
        envelope = read_json(self.path, {})
        if (envelope.get("version") != 1 or envelope.get("identity") != self.identity
                or not isinstance(envelope.get("scene"), dict) or not envelope["scene"]):
            raise ValueError("The saved lighting for this switch could not be verified.")
        return envelope["scene"]

    def save(self, scene: dict) -> None:
        write_json(self.path, {"version": 1, "identity": self.identity, "scene": scene})
