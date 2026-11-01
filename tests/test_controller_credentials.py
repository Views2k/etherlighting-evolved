import json
import sys

import pytest

from etherlighting_evolved import controller_credentials as storage
from etherlighting_evolved.controller_credentials import ControllerLoginStore
from etherlighting_evolved.credentials import CredentialStoreError

RECORD = {"url": "https://controller.invalid:8443", "username": "fixture-user",
          "password": "fixture-secret", "site": "default", "certificate_sha256": "ab" * 32,
          "controller_kind": "self_hosted"}
windows = pytest.mark.skipif(sys.platform != "win32", reason="Windows DPAPI integration")


@windows
def test_protected_roundtrip_uses_separate_envelope_and_no_cleartext(tmp_path):
    store = ControllerLoginStore(tmp_path / "controller-login.json")
    assert store.load() is None and not store.exists()
    store.save(RECORD)
    raw = store.path.read_text("utf-8")
    envelope = json.loads(raw)
    assert set(envelope) == {"version", "protection", "purpose", "data"}
    assert envelope["purpose"] == "unifi-controller-login"
    assert all(value not in raw for value in RECORD.values())
    assert store.load() == RECORD
    store.clear()
    assert store.load() is None


@pytest.mark.parametrize("record", [
    {**RECORD, "verification_code": "123456"}, {**RECORD, "url": "http://controller.invalid"},
    {**RECORD, "url": "https://fixture:secret@controller.invalid"}, {**RECORD, "password": ""},
    {**RECORD, "certificate_sha256": "wrong"}, {**RECORD, "site": "../default"},
    {**RECORD, "password": "a" * 4097}, {**RECORD, "certificate_sha256": True},
])
def test_invalid_fields_and_otp_are_rejected_before_protection(tmp_path, monkeypatch, record):
    calls = []
    monkeypatch.setattr(storage, "_dpapi", lambda *a, **k: calls.append(a))
    store = ControllerLoginStore(tmp_path / "controller-login.json")
    with pytest.raises(CredentialStoreError):
        store.save(record)
    assert not calls and not store.path.exists()


@pytest.mark.parametrize("raw", [
    json.dumps(RECORD), "[]", "broken", '{"version":1,"protection":"windows-dpapi","data":"AA=="}',
    '{"version":1,"protection":"windows-dpapi","purpose":"unifi-controller-login","data":"bad!"}',
])
def test_plaintext_legacy_or_corrupt_records_never_load_or_migrate(tmp_path, raw):
    store = ControllerLoginStore(tmp_path / "controller-login.json")
    store.path.write_text(raw, "utf-8")
    with pytest.raises(CredentialStoreError):
        store.load()
    assert store.path.read_text("utf-8") == raw


def test_protection_failure_preserves_existing_record(tmp_path, monkeypatch):
    store = ControllerLoginStore(tmp_path / "controller-login.json")
    store.path.write_bytes(b"existing encrypted data")

    def failed(*args, **kwargs):
        raise CredentialStoreError("fixture")

    monkeypatch.setattr(storage, "_dpapi", failed)
    with pytest.raises(CredentialStoreError):
        store.save(RECORD)
    assert store.path.read_bytes() == b"existing encrypted data"
    assert not list(tmp_path.glob(".controller-login-*.tmp"))


@windows
def test_atomic_failure_keeps_previous_record_and_removes_encrypted_temp(tmp_path, monkeypatch):
    store = ControllerLoginStore(tmp_path / "controller-login.json")
    store.save(RECORD)
    previous = store.path.read_bytes()

    def failed(*args):
        raise PermissionError("fixture")

    monkeypatch.setattr(storage.os, "replace", failed)
    with pytest.raises(CredentialStoreError):
        store.save({**RECORD, "password": "replacement"})
    assert store.path.read_bytes() == previous
    assert not list(tmp_path.glob(".controller-login-*.tmp"))


@windows
def test_corrupt_ciphertext_never_returns_payload_or_deletes_record(tmp_path):
    store = ControllerLoginStore(tmp_path / "controller-login.json")
    store.save(RECORD)
    envelope = json.loads(store.path.read_text("utf-8"))
    envelope["data"] = "AA=="
    store.path.write_text(json.dumps(envelope), "utf-8")
    with pytest.raises(CredentialStoreError) as error:
        store.load()
    assert RECORD["password"] not in str(error.value)
    assert store.exists()
