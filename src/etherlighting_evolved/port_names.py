"""Device-bound display names, independent of lighting scenes."""
from __future__ import annotations

import copy
import hashlib
import json
import re
import unicodedata
import uuid
from pathlib import Path
from threading import RLock

from fastapi import Body, HTTPException

from .presets import write_json


def validate_names(names):
    if not isinstance(names, dict) or len(names) > 256:
        raise ValueError("Choose valid port names.")
    result = {}
    for key, value in names.items():
        if (not isinstance(key, str) or not re.fullmatch(r"[1-9][0-9]{0,2}", key)
                or int(key) > 256 or not isinstance(value, str) or len(value) > 64
                or any(unicodedata.category(char).startswith("C") for char in value)):
            raise ValueError("Use up to 64 characters for each port name, on a single line.")
        if value.strip():
            result[key] = value.strip()
    return result


class PortNameStore:
    def __init__(self, directory: Path):
        self.directory = directory
        self._lock = RLock()
        self._cache = {}

    @staticmethod
    def defaults():
        return {"revision": "0", "use_unifi": True, "names": {}}

    def path(self, binding):
        return self.directory / (hashlib.sha256(binding.encode()).hexdigest() + ".json")

    def get(self, binding):
        with self._lock:
            try:
                record = json.loads(self.path(binding).read_text("utf-8"))
                if (not isinstance(record, dict) or set(record) != {"revision", "use_unifi", "names"}
                        or not isinstance(record["revision"], str)
                        or not re.fullmatch(r"[0-9a-f]{32}", record["revision"])
                        or type(record["use_unifi"]) is not bool):
                    raise ValueError()
                record["names"] = validate_names(record["names"])
            except FileNotFoundError:
                record = self.defaults()
            except (OSError, ValueError) as exc:
                raise OSError("Port names could not be read. Check the saved port names and try again.") from exc
            self._cache[binding] = record
            return copy.deepcopy(record)

    def put(self, binding, body, available):
        if (set(body) != {"expected_revision", "use_unifi", "names"}
                or type(body.get("use_unifi")) is not bool
                or not isinstance(body.get("expected_revision"), str)):
            raise ValueError("Choose a naming mode and valid port names.")
        names = validate_names(body["names"])
        if any(int(key) not in available for key in body["names"]):
            raise ValueError("Choose ports on this switch.")
        with self._lock:
            old = self.get(binding)
            if body["expected_revision"] != old["revision"]:
                raise FileExistsError("Port names changed in another window. Reload names before saving.")
            updated = {**old["names"], **names}
            for key in body["names"]:
                if key not in names:
                    updated.pop(key, None)
            record = {"revision": uuid.uuid4().hex, "use_unifi": body["use_unifi"], "names": updated}
            write_json(self.path(binding), record)
            self._cache[binding] = record
            return copy.deepcopy(record)

    def decorate(self, ports, binding):
        if not binding:
            return ports
        with self._lock:
            if binding not in self._cache:
                try:
                    self.get(binding)
                except OSError:
                    # Keep frame delivery running; the editor reports storage errors on load/save.
                    self._cache[binding] = self.defaults()
            record = self._cache[binding]
            return [{**port, "name_mode": "unifi" if record["use_unifi"] else "manual",
                     "display_name": (port.get("controller", {}).get("port_name") if record["use_unifi"]
                                      else record["names"].get(str(port["port"])))} for port in ports]


def attach_port_name_routes(api, engine, sessions, store):
    def scope():
        e = engine()
        binding = sessions.schedule_identity()
        if not binding:
            raise HTTPException(409, detail={"message": "Connect a switch before naming its ports."})
        return e, binding

    def run(fn, *args):
        try:
            return fn(*args)
        except FileExistsError as exc:
            raise HTTPException(409, detail={"code": "port_names_changed", "message": str(exc)}) from exc
        except ValueError as exc:
            raise HTTPException(400, detail={"message": str(exc)}) from exc
        except OSError as exc:
            raise HTTPException(503, detail={"code": "port_names_unavailable", "message":
                                            "Port names could not be saved or loaded. Check the data folder, "
                                            "then reload names and try again."}) from exc

    @api.get("/port-names")
    async def get_names():
        _, binding = scope()
        return run(store.get, binding)

    @api.put("/port-names")
    async def put_names(body: dict = Body(...)):
        e, binding = scope()
        available = set(range(1, e.switch.num_ports + 1)) | set(e.switch.sfp_ports)
        return run(store.put, binding, body, available)
