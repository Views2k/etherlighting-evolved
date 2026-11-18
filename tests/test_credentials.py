import asyncio
import json
import sys

import pytest
from fastapi.testclient import TestClient

from etherlighting_evolved import credentials, storage
from etherlighting_evolved.api import create_app
from etherlighting_evolved.credentials import CredentialStoreError, SavedLogin
from etherlighting_evolved.session import SessionManager
from etherlighting_evolved.transport import AuthenticationError, SimulatedTransport

FIXTURE = {"host": "switch.invalid", "port": 22, "username": "fixture-user", "password": "fixture-only-secret"}
windows = pytest.mark.skipif(sys.platform != "win32", reason="Windows DPAPI integration")


@windows
def test_windows_saved_login_protects_the_entire_payload(tmp_path):
    store = SavedLogin(tmp_path / "login.json")
    store.save(**FIXTURE)
    raw = store.path.read_text("utf-8")
    envelope = json.loads(raw)
    assert set(envelope) == {"version", "protection", "data"}
    assert envelope["protection"] == "windows-dpapi"
    assert all(value not in raw for value in (FIXTURE["host"], FIXTURE["username"], FIXTURE["password"]))
    assert store.load() == FIXTURE
    assert not list(tmp_path.glob(".login-*.tmp"))


@windows
def test_legacy_opt_in_record_is_replaced_only_with_protected_payload(tmp_path):
    store = SavedLogin(tmp_path / "login.json")
    store.path.write_text(json.dumps(FIXTURE), "utf-8")
    assert store.load() == FIXTURE
    assert json.loads(store.path.read_text("utf-8"))["protection"] == "windows-dpapi"
    assert FIXTURE["password"] not in store.path.read_text("utf-8")


@pytest.mark.parametrize("legacy", [False, True])
def test_protection_failure_never_writes_plaintext_or_removes_existing_record(tmp_path, monkeypatch, legacy):
    store = SavedLogin(tmp_path / "login.json")
    old = json.dumps(FIXTURE).encode() if legacy else b"existing-record"
    store.path.write_bytes(old)

    def fail(*args, **kwargs):
        raise CredentialStoreError("protection failed")

    monkeypatch.setattr(credentials, "_dpapi", fail)
    with pytest.raises(CredentialStoreError):
        store.load() if legacy else store.save(**FIXTURE)
    assert store.path.read_bytes() == old
    assert not list(tmp_path.glob(".login-*.tmp"))


def test_non_windows_has_no_plaintext_fallback(tmp_path, monkeypatch):
    store = SavedLogin(tmp_path / "login.json")
    monkeypatch.setattr(credentials.sys, "platform", "linux")
    with pytest.raises(CredentialStoreError, match="Windows"):
        store.save(**FIXTURE)
    assert not store.path.exists()


@windows
def test_failed_atomic_replace_preserves_previous_login_and_removes_temp_file(tmp_path, monkeypatch):
    store = SavedLogin(tmp_path / "login.json")
    store.save(**FIXTURE)
    original = store.path.read_bytes()

    def fail(*args, **kwargs):
        raise PermissionError("fixture failure")

    monkeypatch.setattr(storage.os, "replace", fail)
    with pytest.raises(CredentialStoreError, match="Could not save"):
        store.save(**{**FIXTURE, "password": "different-fixture"})
    assert store.path.read_bytes() == original
    assert not list(tmp_path.glob("*.tmp"))


@windows
def test_corrupted_ciphertext_is_not_deleted_or_exposed(tmp_path):
    store = SavedLogin(tmp_path / "login.json")
    store.save(**FIXTURE)
    envelope = json.loads(store.path.read_text("utf-8"))
    envelope["data"] = "AA=="
    store.path.write_text(json.dumps(envelope), "utf-8")
    corrupted = store.path.read_bytes()
    with pytest.raises(CredentialStoreError):
        store.load()
    assert store.path.read_bytes() == corrupted


@pytest.mark.parametrize("record", [
    "not-json", "[]", '{"version":999,"protection":"unknown","data":"AA=="}',
    '{"version":1,"protection":"windows-dpapi","data":"not base64!"}',
])
def test_unreadable_records_are_reported_without_deletion(tmp_path, record):
    store = SavedLogin(tmp_path / "login.json")
    store.path.write_text(record, "utf-8")
    with pytest.raises(CredentialStoreError):
        store.load()
    assert store.path.read_text("utf-8") == record


@windows
def test_login_save_and_forget_api_preserve_session_and_redact_password(config):
    app = create_app(config, autoconnect=False, transport_factory=lambda **kwargs: SimulatedTransport(latency=0))
    with TestClient(app, base_url="http://localhost") as client:
        response = client.post("/api/session/login", json={**FIXTURE, "remember": True})
        assert response.status_code == 200
        assert FIXTURE["password"] not in response.text
        assert response.json()["has_saved_login"]
        assert response.json()["remembered"] == {key: value for key, value in FIXTURE.items() if key != "password"}
        engine = app.state.sessions.engine
        with client.websocket_connect("/api/ws", headers={"Host": "localhost"}) as socket:
            for _ in range(3):
                assert FIXTURE["password"] not in json.dumps(socket.receive_json())
            forgotten = client.post("/api/session/forget")
            assert forgotten.status_code == 200
            assert forgotten.json()["logged_in"] and forgotten.json()["connected"]
            assert not forgotten.json()["has_saved_login"]
            assert forgotten.json()["remembered"] is None
            assert app.state.sessions.engine is engine
            for _ in range(20):
                message = socket.receive_json()
                assert FIXTURE["password"] not in json.dumps(message)
                if message["type"] == "session":
                    assert not message["session"]["has_saved_login"]
                    break
            else:
                pytest.fail("saved login change was not published")
        assert not app.state.sessions.saved.path.exists()


def test_save_failure_preserves_existing_controller_and_closes_new_transport(config, monkeypatch):
    transports = []

    def factory(**kwargs):
        transport = SimulatedTransport(latency=0)
        transports.append(transport)
        return transport

    def fail(*args, **kwargs):
        raise CredentialStoreError("Windows could not protect the login. Turn off Save login.")

    app = create_app(config, autoconnect=False, transport_factory=factory)
    with TestClient(app, base_url="http://localhost") as client:
        assert client.post("/api/session/login", json={"host": "demo"}).status_code == 200
        original = app.state.sessions.engine
        monkeypatch.setattr(credentials, "_dpapi", fail)
        rejected = client.post("/api/session/login", json={**FIXTURE, "remember": True})
        assert rejected.status_code == 500
        assert rejected.json()["detail"]["code"] == "saved_login_failed"
        assert FIXTURE["password"] not in rejected.text
        assert app.state.sessions.engine is original and original.connected
        assert not transports[-1].connected
        assert not app.state.sessions.saved.path.exists()


@windows
def test_save_requires_opt_in_and_demo_does_not_erase_it(config):
    app = create_app(config, autoconnect=False, transport_factory=lambda **kwargs: SimulatedTransport(latency=0))
    with TestClient(app, base_url="http://localhost") as client:
        assert client.post("/api/session/login", json=FIXTURE).status_code == 200
        assert not app.state.sessions.saved.path.exists()
        assert client.post("/api/session/login", json={**FIXTURE, "remember": True}).status_code == 200
        original = app.state.sessions.saved.path.read_bytes()
        assert client.post("/api/session/login", json={"host": "demo"}).status_code == 200
        assert app.state.sessions.saved.path.read_bytes() == original
        assert client.post("/api/session/login", json={**FIXTURE, "remember": False}).status_code == 200
        assert not app.state.sessions.saved.path.exists()


@windows
@pytest.mark.anyio
async def test_protected_login_restores_on_startup_with_fake_transport(config):
    sessions = SessionManager(config, transport_factory=lambda **kwargs: SimulatedTransport(latency=0))
    sessions.saved.save(**FIXTURE)
    try:
        await sessions.auto_connect()
        assert sessions.engine is not None and sessions.engine.connected
        assert FIXTURE["password"] not in json.dumps(sessions.info())
    finally:
        await sessions.close()


@pytest.mark.anyio
async def test_unreadable_saved_login_never_attempts_auto_connection(config):
    attempts = []
    sessions = SessionManager(config, transport_factory=lambda **kwargs: attempts.append(True))
    sessions.saved.path.parent.mkdir(parents=True)
    sessions.saved.path.write_text("corrupt", "utf-8")
    await sessions.auto_connect()
    assert not attempts
    assert sessions.saved.path.read_text("utf-8") == "corrupt"
    state = sessions.info()
    assert state["has_saved_login"] and state["saved_login_error"]
    assert state["remembered"] is None
    assert state["error"]["code"] == "saved_login_unavailable"
    await sessions.forget_saved_login()
    assert not sessions.info()["has_saved_login"]
    assert sessions.info()["error"] is None


@windows
def test_saved_login_survives_exit_and_publishes_delayed_restart_connection(config):
    first = create_app(config, autoconnect=False, transport_factory=lambda **kwargs: SimulatedTransport(latency=0))
    with TestClient(first, base_url="http://localhost") as client:
        assert client.post("/api/session/login", json={**FIXTURE, "remember": True}).status_code == 200
        protected = first.state.sessions.saved.path.read_bytes()
    assert first.state.sessions.engine is None
    assert first.state.sessions.saved.path.read_bytes() == protected

    gate = asyncio.Event()

    class DelayedTransport(SimulatedTransport):
        async def connect(self):
            await gate.wait()
            await super().connect()

    restarted = create_app(config, transport_factory=lambda **kwargs: DelayedTransport(latency=0))
    with TestClient(restarted, base_url="http://localhost") as client:
        initial = client.get("/api/session").json()
        assert initial["has_saved_login"] and initial["connecting"] and not initial["logged_in"]
        with client.websocket_connect("/api/ws", headers={"Host": "localhost"}) as socket:
            pending = socket.receive_json()
            assert pending["type"] == "session" and pending["session"]["connecting"]
            client.portal.call(gate.set)
            for _ in range(20):
                message = socket.receive_json()
                assert FIXTURE["password"] not in json.dumps(message)
                if message["type"] == "session" and message["session"]["logged_in"]:
                    break
            else:
                pytest.fail("the saved connection was not published")
        final = client.get("/api/session").json()
        assert final["logged_in"] and final["connected"] and not final["connecting"]
    assert restarted.state.sessions.saved.path.read_bytes() == protected


@windows
@pytest.mark.anyio
async def test_failed_saved_connection_clears_progress_and_keeps_protected_record(config):
    class RejectedTransport(SimulatedTransport):
        async def connect(self):
            raise AuthenticationError("Fixture authentication rejection")

    sessions = SessionManager(config, transport_factory=lambda **kwargs: RejectedTransport(latency=0))
    sessions.saved.save(**FIXTURE)
    protected = sessions.saved.path.read_bytes()
    await sessions.auto_connect()
    state = sessions.info()
    assert not state["connecting"] and not state["logged_in"]
    assert state["error"]["code"] == "auth_failed"
    assert state["has_saved_login"] and sessions.saved.path.read_bytes() == protected
    assert FIXTURE["password"] not in json.dumps(state)


@pytest.mark.anyio
async def test_cancelled_startup_clears_progress(config, monkeypatch):
    sessions = SessionManager(config)
    started = asyncio.Event()

    async def wait():
        started.set()
        await asyncio.Event().wait()

    monkeypatch.setattr(sessions, "_auto_connect", wait)
    task = asyncio.create_task(sessions.auto_connect())
    await started.wait()
    assert sessions.info()["connecting"]
    task.cancel()
    with pytest.raises(asyncio.CancelledError):
        await task
    assert not sessions.info()["connecting"]
