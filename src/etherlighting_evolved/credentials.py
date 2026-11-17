"""Opt-in saved login protected for the current Windows user."""

from __future__ import annotations

import base64
import binascii
import ctypes
import json
import sys
from pathlib import Path

from .storage import atomic_write_bytes


class CredentialStoreError(RuntimeError):
    """A saved login could not be protected, read or removed."""


def _dpapi(data: bytes, *, decrypt: bool = False) -> bytes:
    if sys.platform != "win32":
        raise CredentialStoreError("Save login requires Windows protection. Sign in with Save login turned off.")

    class DataBlob(ctypes.Structure):
        _fields_ = [("size", ctypes.c_uint32), ("data", ctypes.POINTER(ctypes.c_ubyte))]

    try:
        crypt32 = ctypes.WinDLL("crypt32", use_last_error=True)
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        function = crypt32.CryptUnprotectData if decrypt else crypt32.CryptProtectData
        function.argtypes = [ctypes.POINTER(DataBlob), ctypes.c_void_p, ctypes.POINTER(DataBlob),
                             ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint32, ctypes.POINTER(DataBlob)]
        function.restype = ctypes.c_int
        kernel32.LocalFree.argtypes = [ctypes.c_void_p]
        kernel32.LocalFree.restype = ctypes.c_void_p
        buffer = ctypes.create_string_buffer(data)
        incoming = DataBlob(len(data), ctypes.cast(buffer, ctypes.POINTER(ctypes.c_ubyte)))
        outgoing = DataBlob()
        try:
            # CRYPTPROTECT_UI_FORBIDDEN; omit LOCAL_MACHINE to keep protection scoped to this user.
            if not function(ctypes.byref(incoming), None, None, None, None, 1, ctypes.byref(outgoing)):
                raise CredentialStoreError(
                    "Windows could not unlock the saved login. Sign in again or forget the saved login."
                    if decrypt else
                    "Windows could not protect the login. Sign in with Save login turned off."
                )
            return ctypes.string_at(outgoing.data, outgoing.size)
        finally:
            ctypes.memset(buffer, 0, len(buffer))
            if outgoing.data:
                ctypes.memset(outgoing.data, 0, outgoing.size)
                kernel32.LocalFree(outgoing.data)
    except (OSError, AttributeError) as exc:
        raise CredentialStoreError(
            "Windows login protection is unavailable. Sign in with Save login turned off."
        ) from exc


class SavedLogin:
    def __init__(self, path: Path) -> None:
        self.path = Path(path)

    @staticmethod
    def _validate(data: object) -> dict:
        if not isinstance(data, dict) or not isinstance(data.get("host"), str) or not data["host"].strip():
            raise CredentialStoreError("The saved login has an invalid format. Forget it and sign in again.")
        port = data.get("port", 22)
        username, password = data.get("username", ""), data.get("password", "")
        if (isinstance(port, bool) or not isinstance(port, int) or not 1 <= port <= 65535
                or not isinstance(username, str) or not isinstance(password, str)):
            raise CredentialStoreError("The saved login has an invalid format. Forget it and sign in again.")
        return {"host": data["host"], "port": port, "username": username, "password": password}

    def load(self) -> dict | None:
        try:
            if self.path.stat().st_size > 1048576:
                raise CredentialStoreError("The saved login has an invalid format. Forget it and sign in again.")
            envelope = json.loads(self.path.read_text("utf-8"))
        except FileNotFoundError:
            return None
        except (OSError, UnicodeError, json.JSONDecodeError) as exc:
            raise CredentialStoreError("Could not read the saved login. Forget it and sign in again.") from exc
        if not isinstance(envelope, dict):
            raise CredentialStoreError("The saved login has an invalid format. Forget it and sign in again.")
        if "protection" in envelope or "version" in envelope:
            if envelope.get("version") != 1 or envelope.get("protection") != "windows-dpapi":
                raise CredentialStoreError("The saved login format is not supported. Sign in again.")
            try:
                encrypted = base64.b64decode(envelope["data"], validate=True)
                payload = json.loads(_dpapi(encrypted, decrypt=True).decode("utf-8"))
            except (KeyError, TypeError, ValueError, UnicodeError, binascii.Error) as exc:
                raise CredentialStoreError("Could not unlock the saved login. Forget it and sign in again.") from exc
            return self._validate(payload)
        # An older opt-in record is usable only after protection and atomic replacement succeed.
        legacy = self._validate(envelope)
        self.save(**legacy)
        return legacy

    def save(self, host: str, port: int, username: str, password: str) -> None:
        payload = self._validate({"host": host, "port": port, "username": username, "password": password})
        encrypted = _dpapi(json.dumps(payload, ensure_ascii=False).encode("utf-8"))
        envelope = json.dumps({"version": 1, "protection": "windows-dpapi",
                               "data": base64.b64encode(encrypted).decode("ascii")}).encode("utf-8")
        try:
            atomic_write_bytes(self.path, envelope)
        except OSError as exc:
            raise CredentialStoreError(
                "Could not save the protected login. Sign in with Save login turned off."
            ) from exc

    def clear(self) -> None:
        try:
            self.path.unlink(missing_ok=True)
        except OSError as exc:
            raise CredentialStoreError("Could not forget the saved login. Check access to the EE data folder.") from exc
