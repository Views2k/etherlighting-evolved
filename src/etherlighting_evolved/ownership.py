"""Durable records of output whose shutdown has not yet been confirmed."""

from __future__ import annotations

import hashlib
import json
import os
import secrets
from pathlib import Path

from .transport import TransportError


class OutputOwnershipError(TransportError):
    code = "output_recovery_required"


def device_identity(fingerprint: str | None) -> str:
    if not isinstance(fingerprint, str) or not fingerprint.startswith("SHA256:") or len(fingerprint) < 16:
        raise OutputOwnershipError("The authenticated switch identity could not be verified.")
    return hashlib.sha256(fingerprint.encode("utf-8")).hexdigest()


def default_journal_dir() -> Path:
    # Ownership must survive a changed data directory or a forgotten known-host entry.
    return Path.home() / ".etherlighting-evolved" / "output-ownership"


class OutputJournal:
    def __init__(self, directory: Path | None = None) -> None:
        self.directory = Path(directory) if directory is not None else default_journal_dir()

    def paths(self, host: str, port: int, fingerprint: str) -> tuple[Path, Path]:
        endpoint = hashlib.sha256(json.dumps([host.strip().casefold(), port]).encode("utf-8")).hexdigest()
        identity = device_identity(fingerprint)
        return (self.directory / f"endpoint-{endpoint}.json", self.directory / f"device-{identity}.json")

    def check(self, host: str, port: int, fingerprint: str, *, current: OutputLease | None = None) -> None:
        for path in self.paths(host, port, fingerprint):
            if path.exists() and not (current and path in current.paths and current.owns(path)):
                raise OutputOwnershipError(
                    "A previous lighting session has no confirmed shutdown. Keep EE output stopped and "
                    "complete the lighting recovery review before reconnecting."
                )

    def claim(self, host: str, port: int, fingerprint: str) -> OutputLease:
        paths = self.paths(host, port, fingerprint)
        self.check(host, port, fingerprint)
        lease = OutputLease(paths, device_identity(fingerprint))
        try:
            self.directory.mkdir(parents=True, exist_ok=True)
            for path in paths:
                with path.open("x", encoding="utf-8") as stream:
                    json.dump({"version": 1, "owner": lease.owner}, stream)
                    stream.flush()
                    os.fsync(stream.fileno())
        except OSError as exc:
            # A partial record is deliberately retained. No command may follow an incomplete claim.
            raise OutputOwnershipError("Could not reserve lighting output. Keep EE output stopped and review "
                                       "the ownership records before retrying.") from exc
        return lease


class OutputLease:
    def __init__(self, paths: tuple[Path, Path], identity: str) -> None:
        self.paths, self.identity = paths, identity
        self.owner = secrets.token_hex(24)
        self.uncertain = False
        self.closed = False

    def owns(self, path: Path) -> bool:
        try:
            return json.loads(path.read_text("utf-8")) == {"version": 1, "owner": self.owner}
        except (OSError, ValueError):
            return False

    def before_write(self, fingerprint: str | None) -> None:
        if self.closed or device_identity(fingerprint) != self.identity:
            self.uncertain = True
            raise OutputOwnershipError("The output owner no longer matches this switch connection.")

    def finish(self) -> None:
        if self.closed:
            return
        if self.uncertain or not all(self.owns(path) for path in self.paths):
            raise OutputOwnershipError("Lighting shutdown could not be confirmed. The output ownership "
                                       "record has been retained for recovery review.")
        try:
            for path in self.paths:
                path.unlink()
        except OSError as exc:
            raise OutputOwnershipError("Could not finish the lighting ownership record. Keep EE output "
                                       "stopped until the record is reviewed.") from exc
        self.closed = True
