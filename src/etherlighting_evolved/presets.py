"""Small JSON stores: named presets, and the scene that was showing when EE last ran."""

from __future__ import annotations

import copy
import json
import re
import uuid
from pathlib import Path
from threading import RLock

from .storage import atomic_write_json as write_json
from .storage import quarantine, read_json

_NAME_RE = re.compile(r"^[\w .\-]{1,64}$")


class ProfileStoreError(OSError):
    """Existing profiles could not be read or a change could not be saved."""

    code = "profile_storage_unavailable"


class ProfileConflict(ValueError):
    """The saved profile changed after the editor opened it."""


class PresetStore:
    """Named scenes with stable identities and optimistic revisions."""

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
        if not isinstance(data, dict):
            raise ProfileStoreError("The saved profile file is invalid. "
                                        "Restore a valid copy before editing profiles.")
        if data.get("format") == "ee-profiles-v2":
            records = data.get("profiles")
            if (not isinstance(records, list) or any(not isinstance(row, dict)
                    or not isinstance(row.get("id"), str) or not re.fullmatch(r"[0-9a-f]{32}", row["id"])
                    or not isinstance(row.get("name"), str) or not isinstance(row.get("scene"), dict)
                    or not isinstance(row.get("revision"), int) or isinstance(row["revision"], bool)
                    or row["revision"] < 1 for row in records)
                    or len({row["id"] for row in records}) != len(records)
                    or len({row["name"] for row in records}) != len(records)):
                raise ProfileStoreError("The saved profile file is invalid. "
                                        "Restore a valid copy before editing profiles.")
            return {row["name"]: row for row in records}
        if any(not isinstance(scene, dict) for scene in data.values()):
            raise ProfileStoreError("The saved profile file is invalid. "
                                        "Restore a valid copy before editing profiles.")
        records = {name: {"id": uuid.uuid4().hex, "revision": 1, "name": name, "scene": scene}
                   for name, scene in data.items()}
        # Commit identities before exposing them to schedules or another client.
        if records:
            self._save(records)
        return records

    def _save(self, data: dict[str, dict]) -> None:
        try:
            write_json(self.path, {"format": "ee-profiles-v2", "profiles": list(data.values())})
        except OSError as exc:
            raise ProfileStoreError("The profile change could not be saved. "
                                    "Check the data folder and try again.") from exc

    def records(self) -> list[dict]:
        with self._lock:
            return copy.deepcopy(list(self._load().values()))

    def list(self) -> dict[str, dict]:
        with self._lock:
            return {name: copy.deepcopy(row["scene"]) for name, row in self._load().items()}

    def record(self, *, name: str | None = None, profile_id: str | None = None) -> dict:
        with self._lock:
            data = self._load()
            row = data.get(name) if name is not None else next(
                (row for row in data.values() if row["id"] == profile_id), None)
            if row is None:
                raise KeyError(name if name is not None else profile_id)
            return copy.deepcopy(row)

    def get(self, name: str) -> dict:
        return self.record(name=name)["scene"]

    def get_id(self, profile_id: str) -> dict:
        return self.record(profile_id=profile_id)["scene"]

    def save(self, name: str, snapshot: dict, *, overwrite: bool = True) -> None:
        name = self._name(name)
        with self._lock:
            data = self._load()
            if not overwrite and name in data:
                raise FileExistsError("A profile with this name already exists.")
            old = data.get(name)
            data[name] = {"id": old["id"] if old else uuid.uuid4().hex,
                          "revision": old["revision"] + 1 if old else 1,
                          "name": name, "scene": copy.deepcopy(snapshot)}
            self._save(data)

    def change(self, profile_id: str, expected_revision: int, *, scene: dict | None = None,
               name: str | None = None, delete: bool = False, duplicate: bool = False) -> dict | None:
        with self._lock:
            data = self._load()
            row = next((row for row in data.values() if row["id"] == profile_id), None)
            if row is None or row["revision"] != expected_revision:
                raise ProfileConflict("This profile changed or was removed. Refresh profiles before trying again.")
            target = self._name(name) if name is not None else row["name"]
            if (target != row["name"] or duplicate) and target in data:
                raise FileExistsError("A profile with this name already exists.")
            if duplicate:
                result = {"id": uuid.uuid4().hex, "revision": 1, "name": target,
                          "scene": copy.deepcopy(row["scene"])}
            else:
                del data[row["name"]]
                result = {**row, "name": target, "revision": row["revision"] + 1,
                          "scene": copy.deepcopy(scene if scene is not None else row["scene"])}
            if not delete:
                data[target] = result
            self._save(data)
            return None if delete else copy.deepcopy(result)

    def delete(self, name: str) -> None:
        with self._lock:
            row = self.record(name=name)
            self.change(row["id"], row["revision"], delete=True)

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
            row = self.record(name=source)
            self.change(row["id"], row["revision"], name=target, duplicate=not remove_source)


class SceneStore:
    """The live scene, saved whenever it changes so a restart picks up where it left off."""

    def __init__(self, path: Path) -> None:
        self.path = Path(path)

    def load(self) -> dict | None:
        return read_json(self.path, {}) or None

    def save(self, scene: dict) -> None:
        write_json(self.path, scene)


class UnusableSceneError(ValueError):
    """The saved scene file is damaged or belongs to another device or port layout."""


class DeviceSceneStore(SceneStore):
    """A scene explicitly assigned to an authenticated device and qualified port layout."""

    def __init__(self, path: Path, identity: str) -> None:
        super().__init__(path)
        self.identity = identity

    def load(self) -> dict | None:
        """The saved scene, or None when there is none.

        An unusable file raises UnusableSceneError and stays where it is: the caller decides whether to set it aside
        and start over, because a scene saved to the switch may still depend on it.
        """
        try:
            envelope = json.loads(self.path.read_text("utf-8"))
        except FileNotFoundError:
            return None
        except OSError as exc:
            raise ValueError("The saved lighting for this switch could not be read.") from exc
        except ValueError as exc:
            raise UnusableSceneError("The saved lighting for this switch could not be verified.") from exc
        version = envelope.get("version") if isinstance(envelope, dict) else None
        if type(version) is int and version > 1:
            raise ValueError("The saved lighting for this switch was saved by a newer version of EE. "
                             "Update EE to use it.")
        if (version != 1 or envelope.get("identity") != self.identity
                or not isinstance(envelope.get("scene"), dict) or not envelope["scene"]):
            raise UnusableSceneError("The saved lighting for this switch could not be verified.")
        return envelope["scene"]

    def set_aside(self) -> None:
        """Move an unusable file aside under a dated name, so a new setup can be saved without losing it."""
        quarantine(self.path, "The saved lighting for this switch could not be verified")

    def save(self, scene: dict) -> None:
        write_json(self.path, {"version": 1, "identity": self.identity, "scene": scene})
