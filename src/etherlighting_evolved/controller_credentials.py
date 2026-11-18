"""Explicit controller reconnect credentials protected for the current Windows user."""

from __future__ import annotations

import base64
import json
import re
from pathlib import Path

from .controller import (
    ControllerError,
    certificate_pin,
    controller_access,
    controller_origin,
    validate_api_key,
    validate_controller_kind,
)
from .credentials import CredentialStoreError, _dpapi
from .storage import atomic_write_bytes


class ControllerLoginStore:
    def __init__(self, path: Path) -> None:
        self.path = Path(path)

    @staticmethod
    def _validate(value: object) -> dict:
        if isinstance(value, dict) and value.get("auth_method") == "api_key":
            return ControllerLoginStore._validate_key(value)
        fields = {"url", "username", "password", "site", "certificate_sha256"}
        if not isinstance(value, dict) or set(value) not in (fields, fields | {"controller_kind"}):
            raise CredentialStoreError("The saved controller login is invalid. Connect and save it again.")
        if (not isinstance(value["url"], str) or len(value["url"]) > 2048
                or not isinstance(value["username"], str) or not 1 <= len(value["username"]) <= 256
                or not isinstance(value["password"], str) or not 1 <= len(value["password"]) <= 4096
                or not isinstance(value["site"], str) or not re.fullmatch(r"[A-Za-z0-9_-]{1,64}", value["site"])
                or (value["certificate_sha256"] is not None and not isinstance(value["certificate_sha256"], str))):
            raise CredentialStoreError("The saved controller login is invalid. Connect and save it again.")
        try:
            origin = controller_origin(value["url"])
            pin = certificate_pin(value["certificate_sha256"])
            kind = validate_controller_kind(value.get("controller_kind", "self_hosted"))
            controller_access(kind, "password", origin, None)
        except ControllerError as exc:
            raise CredentialStoreError("The saved controller login is invalid. Connect and save it again.") from exc
        return {**value, "url": origin, "certificate_sha256": pin, "controller_kind": kind}

    @staticmethod
    def _validate_key(value: dict) -> dict:
        fields = {"url", "controller_kind", "auth_method", "api_key", "console_id", "site", "certificate_sha256"}
        if (set(value) != fields or not isinstance(value["url"], str) or len(value["url"]) > 2048
                or not isinstance(value["site"], str) or not re.fullmatch(r"[A-Za-z0-9_-]{1,64}", value["site"])
                or (value["certificate_sha256"] is not None and not isinstance(value["certificate_sha256"], str))):
            raise CredentialStoreError("The saved controller login is invalid. Connect and save it again.")
        try:
            origin = controller_origin(value["url"])
            kind = validate_controller_kind(value["controller_kind"])
            console_id = controller_access(kind, "api_key", origin, value["console_id"])
            key = validate_api_key(value["api_key"])
            pin = certificate_pin(value["certificate_sha256"])
            if kind == "cloud_connector" and pin is not None:
                raise ValueError
        except (ControllerError, ValueError) as exc:
            raise CredentialStoreError("The saved controller login is invalid. Connect and save it again.") from exc
        return {**value, "url": origin, "api_key": key, "console_id": console_id, "certificate_sha256": pin}

    def exists(self) -> bool:
        try:
            return self.path.is_file()
        except OSError as exc:
            raise CredentialStoreError("Could not read the saved controller login. Connect and save it again.") from exc

    def load(self) -> dict | None:
        try:
            if self.path.stat().st_size > 65536:
                raise CredentialStoreError("The saved controller login is invalid. Connect and save it again.")
            envelope = json.loads(self.path.read_text("utf-8"))
        except FileNotFoundError:
            return None
        except (OSError, UnicodeError, ValueError) as exc:
            raise CredentialStoreError("Could not read the saved controller login. Connect and save it again.") from exc
        if (not isinstance(envelope, dict)
                or set(envelope) != {"version", "protection", "purpose", "data"}
                or envelope.get("version") != 1 or envelope.get("protection") != "windows-dpapi"
                or envelope.get("purpose") != "unifi-controller-login"):
            raise CredentialStoreError("The saved controller login is invalid. Connect and save it again.")
        try:
            encrypted = base64.b64decode(envelope["data"], validate=True)
            payload = json.loads(_dpapi(encrypted, decrypt=True).decode("utf-8"))
            return self._validate(payload)
        except (CredentialStoreError, TypeError, ValueError, UnicodeError) as exc:
            raise CredentialStoreError(
                "Could not unlock the saved controller login. Connect and save it again.") from exc

    def save(self, record: dict) -> None:
        payload = self._validate(record)
        try:
            encrypted = _dpapi(json.dumps(payload, ensure_ascii=False).encode("utf-8"))
        except CredentialStoreError as exc:
            raise CredentialStoreError("Windows could not protect the controller login. Try saving it again.") from exc
        envelope = json.dumps({"version": 1, "protection": "windows-dpapi", "purpose": "unifi-controller-login",
                               "data": base64.b64encode(encrypted).decode("ascii")}).encode("utf-8")
        try:
            atomic_write_bytes(self.path, envelope)
        except OSError as exc:
            raise CredentialStoreError("Could not save the protected controller login. Try again.") from exc

    def clear(self) -> None:
        try:
            self.path.unlink(missing_ok=True)
        except OSError as exc:
            raise CredentialStoreError(
                "Could not remove the saved controller login. Check access to the data folder.") from exc
