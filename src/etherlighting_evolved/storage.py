"""Saved files: atomic replacement and reading for the JSON stores."""

from __future__ import annotations

import json
import os
import tempfile
from pathlib import Path


def atomic_write_bytes(path: Path, data: bytes) -> None:
    """Replace the file so that a crash or power loss leaves either the previous content or the new content."""
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary = tempfile.mkstemp(dir=path.parent, prefix=f".{path.name}.", suffix=".tmp")
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        Path(temporary).unlink(missing_ok=True)


def atomic_write_json(path: Path, data, *, indent: int | None = 2, sort_keys: bool = True) -> None:
    atomic_write_bytes(path, json.dumps(data, indent=indent, sort_keys=sort_keys).encode("utf-8"))


def read_json(path: Path, default):
    """The saved value, or `default` when the file is missing, unreadable or holds another type."""
    try:
        data = json.loads(Path(path).read_text("utf-8"))
    except (OSError, json.JSONDecodeError):
        return default
    return data if isinstance(data, type(default)) else default
