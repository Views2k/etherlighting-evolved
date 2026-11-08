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

    def retained(self, host: str, port: int, fingerprint: str, *,
                 current: OutputLease | None = None) -> dict[str, Path | None]:
        """The records that would refuse a claim: the endpoint record for this address and the device record
        for this authenticated switch. A record owned by `current` does not count."""
        endpoint, device = self.paths(host, port, fingerprint)

        def blocking(path: Path) -> bool:
            return path.exists() and not (current and path in current.paths and current.owns(path))

        return {"endpoint": endpoint if blocking(endpoint) else None, "device": device if blocking(device) else None}

    def adopt(self, host: str, port: int, fingerprint: str) -> OutputLease:
        """Take over the records of an earlier unconfirmed session with the same authenticated switch.

        The caller must then return the switch lighting to its defaults with acknowledged commands and call
        `finish()`; until that confirms, the lease stays uncertain and the records stay on disk.
        """
        paths = self.paths(host, port, fingerprint)
        if not paths[1].exists():
            raise OutputOwnershipError("There is no retained lighting record for this switch.")
        lease = OutputLease(paths, device_identity(fingerprint))
        lease.uncertain = True
        try:
            self.directory.mkdir(parents=True, exist_ok=True)
            for path in paths:
                temporary = path.with_name(f".{path.name}.{secrets.token_hex(8)}.tmp")
                try:
                    with temporary.open("x", encoding="utf-8") as stream:
                        json.dump({"version": 1, "owner": lease.owner}, stream)
                        stream.flush()
                        os.fsync(stream.fileno())
                    os.replace(temporary, path)
                finally:
                    temporary.unlink(missing_ok=True)
        except OSError as exc:
            raise OutputOwnershipError("Could not take over the retained lighting record. Keep EE output stopped "
                                       "and review the ownership records before retrying.") from exc
        return lease

    def release_endpoint(self, host: str, port: int, fingerprint: str) -> None:
        """Remove an endpoint record whose device record does not describe the switch now at that address.

        The earlier session's device record stays, so that switch is still protected when it is seen again.
        """
        endpoint, device = self.paths(host, port, fingerprint)
        if device.exists():
            raise OutputOwnershipError("The retained lighting record belongs to this switch and needs recovery.")
        try:
            endpoint.unlink(missing_ok=True)
        except OSError as exc:
            raise OutputOwnershipError("Could not remove the stale lighting ownership record. Keep EE output "
                                       "stopped and review the ownership records before retrying.") from exc

    def player_lease(self, host: str, port: int, fingerprint: str, owner: str) -> OutputLease:
        """Claim or reopen only this recorded switch player's two local ownership files."""
        lease = OutputLease(self.paths(host, port, fingerprint), device_identity(fingerprint))
        lease.owner = owner
        if any(path.exists() and not lease.owns(path) for path in lease.paths):
            raise OutputOwnershipError("Another lighting session owns this output.")
        self.directory.mkdir(parents=True, exist_ok=True)
        for path in lease.paths:
            try:
                with path.open("x", encoding="utf-8") as stream:
                    json.dump({"version": 1, "owner": owner}, stream)
                    stream.flush()
                    os.fsync(stream.fileno())
            except FileExistsError:
                if not lease.owns(path):
                    raise OutputOwnershipError("Another lighting session owns this output.") from None
        return lease

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
        self.keep_for_player = False

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
        if self.keep_for_player:
            self.closed = True
            return
        try:
            for path in self.paths:
                path.unlink()
        except OSError as exc:
            raise OutputOwnershipError("Could not finish the lighting ownership record. Keep EE output "
                                       "stopped until the record is reviewed.") from exc
        self.closed = True
