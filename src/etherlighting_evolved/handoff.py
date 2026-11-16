"""Durable switch connection intent, independent of saved credentials and scenes."""

from __future__ import annotations

import json
from pathlib import Path

from .storage import atomic_write_json


class HandoffIntent:
    def __init__(self, path: Path) -> None:
        self.path = path
        self.state = "none"
        try:
            record = json.loads(path.read_text(encoding="utf-8"))
            if (not isinstance(record, dict) or record.get("version") != 1
                    or record.get("state") not in {"none", "pending", "complete", "unconfirmed"}):
                raise ValueError("invalid connection intent")
            self.state = record["state"]
            if self.state == "pending":
                self.state = "unconfirmed"
        except FileNotFoundError:
            pass
        except (OSError, ValueError, UnicodeError):
            self.state = "unconfirmed"

    @property
    def paused(self) -> bool:
        return self.state != "none"

    def save(self, state: str) -> None:
        if state not in {"none", "pending", "complete", "unconfirmed"}:
            raise ValueError("invalid connection intent")
        atomic_write_json(self.path, {"version": 1, "state": state})
        self.state = state
