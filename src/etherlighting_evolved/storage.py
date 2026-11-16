"""Saved files: atomic replacement, reading, format versions, and setting aside files that cannot be used.

A store refuses a file whose format version is newer than the one it knows. It does not read such a file in part
and never overwrites it, so the fields a newer release added are still there when that release runs again.
"""

from __future__ import annotations

import json
import logging
import os
import tempfile
from datetime import UTC, datetime
from pathlib import Path

log = logging.getLogger(__name__)


class NewerFormatError(OSError):
    """A saved file was written by a newer EE release in a format this release does not know."""


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


def check_version(version, supported: int, subject: str) -> None:
    if type(version) is not int or version < 1:
        raise ValueError(f"{subject} have an invalid format version.")
    if version > supported:
        raise NewerFormatError(f"{subject} were saved by a newer version of EE. Update EE to use them.")


def quarantine(path: Path, problem: str) -> None:
    """Move an unusable saved file aside under a dated name, so its store can start over without losing it."""
    target = path.with_name(f"{path.name}.corrupt-{datetime.now(UTC):%Y%m%dT%H%M%SZ}")
    try:
        path.rename(target)
    except OSError as exc:
        log.warning("%s, and %s could not be moved aside: %s", problem, path, exc)
        return
    log.warning("%s; it was moved to %s", problem, target)
