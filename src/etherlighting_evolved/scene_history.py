"""Bounded, device-scoped scene history with guarded restoration."""

from __future__ import annotations

import copy
import hashlib
import json
import secrets
from datetime import UTC, datetime
from pathlib import Path
from threading import RLock

from fastapi import Body, HTTPException
from pydantic import BaseModel, ConfigDict, Field

from .storage import atomic_write_json as write_json


class HistoryStoreError(OSError):
    pass


def scene_revision(scene: dict) -> str:
    def canonical(value):
        if isinstance(value, float) and value.is_integer():
            return int(value)
        if isinstance(value, dict):
            return {key: canonical(item) for key, item in value.items()}
        if isinstance(value, list):
            return [canonical(item) for item in value]
        return value
    return hashlib.sha256(json.dumps(canonical(scene), sort_keys=True, separators=(",", ":"),
                                     allow_nan=False).encode("utf-8")).hexdigest()


class SceneHistory:
    LIMIT = 50
    TOTAL_LIMIT = 250

    def __init__(self, path: Path):
        self.path = Path(path)
        self._lock = RLock()

    def _load(self) -> list[dict]:
        try:
            if self.path.stat().st_size > 20_000_000:
                raise ValueError("history is too large")
            data = json.loads(self.path.read_text("utf-8"))
        except FileNotFoundError:
            return []
        except (OSError, ValueError) as exc:
            raise HistoryStoreError("Lighting history could not be read. Check the data folder.") from exc
        if (not isinstance(data, dict) or data.get("version") != 1 or not isinstance(data.get("entries"), list)
                or len(data["entries"]) > self.TOTAL_LIMIT):
            raise HistoryStoreError("Lighting history is invalid. Restore a valid history file.")
        entries = data["entries"]
        for entry in entries:
            try:
                valid = (isinstance(entry, dict) and isinstance(entry.get("scene"), dict)
                         and all(isinstance(entry.get(key), str) for key in ("id", "binding", "at", "revision"))
                         and scene_revision(entry["scene"]) == entry["revision"])
            except (TypeError, ValueError, OverflowError):
                valid = False
            if not valid:
                raise HistoryStoreError("Lighting history is invalid. Restore a valid history file.")
        return entries

    def record(self, binding: str, scene: dict) -> None:
        with self._lock:
            entries = self._load()
            revision = scene_revision(scene)
            latest = next((entry for entry in entries if entry["binding"] == binding), None)
            if latest and latest["revision"] == revision:
                return
            entry = {"id": secrets.token_hex(16), "binding": binding,
                     "at": datetime.now(UTC).isoformat(timespec="seconds"),
                     "revision": revision, "scene": copy.deepcopy(scene)}
            same = [item for item in entries if item["binding"] == binding][:self.LIMIT - 1]
            other = [item for item in entries if item["binding"] != binding]
            kept = sorted(same + other, key=lambda item: item["at"], reverse=True)
            try:
                write_json(self.path, {"version": 1, "entries": [entry, *kept[:self.TOTAL_LIMIT - 1]]})
            except OSError as exc:
                raise HistoryStoreError("Lighting history could not be saved. Check the data folder.") from exc

    def list(self, binding: str) -> list[dict]:
        with self._lock:
            return [copy.deepcopy({key: value for key, value in item.items() if key != "binding"})
                    for item in self._load() if item["binding"] == binding]

    def get(self, binding: str, entry_id: str) -> dict:
        return next((entry for entry in self.list(binding) if entry["id"] == entry_id), None)


class IdentifyStart(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)
    port: int = Field(ge=1, le=128)


class IdentifyStop(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)
    id: str = Field(min_length=32, max_length=32)


class HistoryRestore(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)
    expected_revision: str = Field(min_length=64, max_length=64)


def attach_routes(api, engine, apply, restore_scene):
    """Use the existing authenticated router and the normal scene transaction."""
    @api.post("/identify")
    async def identify(body: IdentifyStart):
        return apply(engine().start_identify, body.port)

    @api.delete("/identify")
    async def stop_identify(body: IdentifyStop = Body(...)):
        current = engine()
        apply(current.stop_identify, body.id)
        return {"identify": current.identify_state()}

    def history_for(current):
        if current._history is None or current._history_binding is None:
            raise HTTPException(409, detail={"code": "history_unavailable", "message":
                                            "Lighting history is unavailable for this session."})
        return current._history, current._history_binding

    @api.get("/history")
    async def history():
        current = engine()
        store, binding = history_for(current)
        try:
            entries = store.list(binding)
        except HistoryStoreError as exc:
            raise HTTPException(409, detail={"code": "history_unavailable", "message": str(exc)}) from exc
        return {"entries": entries, "limit": store.LIMIT, "error": current.history_error}

    @api.post("/history/{entry_id}/restore")
    async def restore(entry_id: str, body: HistoryRestore):
        current = engine()
        store, binding = history_for(current)
        try:
            entry = store.get(binding, entry_id)
        except HistoryStoreError as exc:
            raise HTTPException(409, detail={"code": "history_unavailable", "message": str(exc)}) from exc
        if entry is None:
            raise HTTPException(404, detail={"code": "not_found", "message":
                                            "This history entry is no longer available."})
        return await restore_scene(entry["scene"], body.expected_revision)
