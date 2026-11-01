"""Durable switch connection intent, independent of saved credentials and scenes."""

from __future__ import annotations

import json
import os
import secrets
from pathlib import Path


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
        self.path.parent.mkdir(parents=True, exist_ok=True)
        temporary = self.path.with_name(f".{self.path.name}.{secrets.token_hex(8)}.tmp")
        try:
            with temporary.open("x", encoding="utf-8") as stream:
                json.dump({"version": 1, "state": state}, stream)
                stream.flush()
                os.fsync(stream.fileno())
            temporary.replace(self.path)
        finally:
            temporary.unlink(missing_ok=True)
        self.state = state
