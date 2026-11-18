"""Named effect settings and device-bound port selections."""
from __future__ import annotations

import copy
import json
import uuid
from pathlib import Path
from threading import RLock

from fastapi import Body, HTTPException

from .effects import create_effect
from .presets import PresetStore
from .storage import atomic_write_json as write_json


class LibraryStoreError(OSError):
    pass


class LibraryStore:
    def __init__(self, path: Path):
        self.path = path
        self.lock = RLock()

    def load(self):
        try:
            data = json.loads(self.path.read_text("utf-8"))
        except FileNotFoundError:
            return {"version": 1, "items": []}
        except (OSError, ValueError) as exc:
            raise LibraryStoreError("Saved selections and effects could not be read.") from exc
        if (not isinstance(data, dict) or data.get("version") != 1
                or not isinstance(data.get("items"), list)
                or any(not isinstance(item, dict) or not item.get("id") for item in data["items"])):
            raise LibraryStoreError("The saved selections and effects file is invalid.")
        items = data["items"]
        def valid(item):
            if (not isinstance(item.get("id"), str) or not item["id"]
                    or not isinstance(item.get("revision"), str) or not item["revision"]
                    or not isinstance(item.get("name"), str) or not item["name"]
                    or item.get("kind") not in {"effect", "selection"}
                    or not isinstance(item.get("value"), dict)):
                return False
            value = item["value"]
            if item["kind"] == "effect":
                if item.get("binding") is not None or set(value) != {"id", "params"}:
                    return False
                if not isinstance(value["id"], str) or not isinstance(value["params"], dict):
                    return False
                try:
                    create_effect(value["id"], 1, value["params"])
                except (ValueError, TypeError, KeyError, OverflowError):
                    return False
                return True
            ports = value.get("ports")
            return (isinstance(item.get("binding"), str) and bool(item["binding"])
                    and isinstance(ports, list) and bool(ports)
                    and all(type(port) is int and 1 <= port <= 256 for port in ports)
                    and isinstance(value.get("topology"), dict))
        if (len(items) > 256 or not all(valid(item) for item in items)
                or len({item["id"] for item in items}) != len(items)):
            raise LibraryStoreError("The saved selections and effects file is invalid.")
        return data

    def list(self, kind, binding):
        with self.lock:
            return copy.deepcopy([item for item in self.load()["items"]
                                  if item["kind"] == kind
                                  and (kind == "effect" or item.get("binding") == binding)])

    def put(self, kind, name, payload, binding, item_id=None, expected_revision=None):
        if not isinstance(name, str):
            raise ValueError("Enter a name using text.")
        name = PresetStore._name(name)
        with self.lock:
            data = self.load()
            old = next((item for item in data["items"] if item["id"] == item_id), None)
            if item_id and (not old or old["kind"] != kind
                            or old.get("binding") != binding
                            or old["revision"] != expected_revision):
                raise FileExistsError("This saved item changed. Refresh it before editing.")
            if any(item["name"].casefold() == name.casefold() and item["kind"] == kind
                   and item.get("binding") == binding and item["id"] != item_id for item in data["items"]):
                raise FileExistsError("A saved item with this name already exists.")
            if not old and len(data["items"]) >= 256:
                raise ValueError("Remove an unused saved item before adding another.")
            item = {"id": item_id or uuid.uuid4().hex, "revision": uuid.uuid4().hex,
                    "kind": kind, "name": name, "binding": binding, "value": copy.deepcopy(payload)}
            data["items"] = [entry for entry in data["items"] if entry["id"] != item["id"]] + [item]
            write_json(self.path, data)
            return item

    def delete(self, kind, binding, item_id, revision):
        with self.lock:
            data = self.load()
            old = next((item for item in data["items"] if item["id"] == item_id), None)
            if not old or old["kind"] != kind or old.get("binding") != binding or old["revision"] != revision:
                raise FileExistsError("This saved item changed. Refresh it before deleting.")
            data["items"].remove(old)
            write_json(self.path, data)


def attach_library_routes(api, engine, sessions, config):
    store = LibraryStore(config.data_dir / "lighting-library.json")

    def scope(kind):
        if kind not in {"effect", "selection"}:
            raise HTTPException(404, detail={"message": "Choose a saved effect or port selection."})
        e = engine()
        if kind == "effect":
            return e, None
        identity = sessions.schedule_identity()
        if not identity:
            raise HTTPException(409, detail={"message": "Connect a switch before saving a selection."})
        return e, identity

    def run(fn, *args, **kwargs):
        try:
            return fn(*args, **kwargs)
        except FileExistsError as exc:
            raise HTTPException(409, detail={"code": "library_changed", "message": str(exc)}) from exc
        except (ValueError, TypeError, KeyError) as exc:
            raise HTTPException(400, detail={"message": str(exc)}) from exc
        except OSError as exc:
            raise HTTPException(503, detail={"message":
                                            "The saved library could not be accessed. "
                                            "Check the saved file and data folder."}) from exc

    def value(kind, raw, e):
        if not isinstance(raw, dict):
            raise ValueError("Choose valid saved settings.")
        if kind == "effect":
            if set(raw) != {"id", "params"} or not isinstance(raw["params"], dict):
                raise ValueError("Choose an effect and its settings.")
            effect = create_effect(raw["id"], 1, raw["params"])
            return {"id": raw["id"], "params": effect.params_json()}
        ports = raw.get("ports")
        if set(raw) != {"ports"} or not isinstance(ports, list) or not ports:
            raise ValueError("Select at least one port.")
        available = set(range(1, e.switch.num_ports + 1)) | set(e.switch.sfp_ports)
        if any(type(port) is not int or port not in available for port in ports):
            raise ValueError("Choose ports on this switch.")
        return {"ports": sorted(set(ports)), "topology": e.snapshot().get("topology")}

    @api.get("/library/{kind}")
    async def list_items(kind: str):
        _, binding = scope(kind)
        return {"items": run(store.list, kind, binding)}

    @api.post("/library/{kind}", status_code=201)
    async def save_item(kind: str, body: dict = Body(...)):
        e, binding = scope(kind)
        payload = run(value, kind, body.get("value"), e)
        return run(store.put, kind, body.get("name", ""), payload, binding)

    @api.put("/library/{kind}/{item_id}")
    async def replace_item(kind: str, item_id: str, body: dict = Body(...)):
        e, binding = scope(kind)
        payload = run(value, kind, body.get("value"), e)
        return run(store.put, kind, body.get("name", ""), payload, binding,
                   item_id=item_id, expected_revision=body.get("expected_revision"))

    @api.delete("/library/{kind}/{item_id}")
    async def delete_item(kind: str, item_id: str, body: dict = Body(...)):
        _, binding = scope(kind)
        run(store.delete, kind, binding, item_id, body.get("expected_revision"))
        return {"deleted": True}
