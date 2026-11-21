import asyncio
import json
from copy import deepcopy
from types import SimpleNamespace

import pytest
from fastapi.testclient import TestClient
from pydantic import ValidationError
from test_controller_api import BASE, HEADERS, TOKEN
from test_controller_credentials import RECORD

from etherlighting_evolved import api, controller_api
from etherlighting_evolved.controller import ControllerError
from etherlighting_evolved.controller_api import ControllerIntegration, ControllerLogin
from etherlighting_evolved.credentials import CredentialStoreError


class Store:
    def __init__(self, saved=None):
        self.saved = deepcopy(saved)
        self.saves = self.clears = 0
        self.fail = None

    def exists(self):
        return self.saved is not None

    def load(self):
        if self.fail == "load":
            raise CredentialStoreError("fixture")
        return deepcopy(self.saved)

    def save(self, record):
        if self.fail == "save":
            raise CredentialStoreError("fixture")
        self.saved = deepcopy(record)
        self.saves += 1

    def clear(self):
        if self.fail == "clear":
            raise CredentialStoreError("fixture")
        self.saved = None
        self.clears += 1


class Manager:
    def __init__(self):
        self.record = None
        self.host = None
        self.connects = []
        self.refreshes = self.disconnects = 0
        self.error = self.refresh_error = None
        self.entered, self.gate = asyncio.Event(), asyncio.Event()
        self.gate.set()

    def status(self):
        return {"connected": self.record is not None and self.refresh_error is None,
                "configured": self.record is not None, "site": self.record["site"] if self.record else None,
                "error": self.refresh_error.detail() if self.refresh_error else None}

    async def connect(self, **kwargs):
        self.connects.append(kwargs)
        self.entered.set()
        await self.gate.wait()
        if self.error:
            raise self.error
        self.record = {key: kwargs.get(key) for key in RECORD}
        self.host = kwargs["switch_host"]
        return self.status()

    def can_save_login(self, host):
        return bool(host and host == self.host and self.status()["connected"])

    async def login_for_saving(self, host):
        if not self.can_save_login(host):
            raise ControllerError("controller_login_required", "Connect your controller before saving.", 409)
        return dict(self.record)

    async def refresh(self, host):
        self.refreshes += 1
        self.host = host
        return self.status()

    async def disconnect(self):
        self.record = self.host = None
        self.refresh_error = None
        self.disconnects += 1
        return self.status()


def setup(tmp_path, saved=None):
    now = [100.0]
    sessions = SimpleNamespace(host="switch.invalid", simulated=False,
                               engine=SimpleNamespace(connected=True))
    manager, store = Manager(), Store(saved)
    integration = ControllerIntegration(sessions, manager, data_dir=tmp_path, store=store, clock=lambda: now[0])
    return integration, sessions, manager, store, now


async def connect(integration, record=RECORD):
    return await integration.connect(ControllerLogin(**record))


@pytest.mark.anyio
async def test_save_is_explicit_uses_active_login_and_disable_preserves_connection(tmp_path):
    integration, _, manager, store, _ = setup(tmp_path)
    await connect(integration)
    assert not store.saved and not integration.status()["auto_reconnect"]
    saved = await integration.preferences(True)
    assert store.saved == RECORD and store.saves == 1
    assert saved["saved_login_matches_connection"] and saved["auto_reconnect"]
    assert RECORD["password"] not in json.dumps(saved)
    disabled = await integration.preferences(False)
    assert manager.status()["connected"] and manager.disconnects == 0
    assert not store.saved and not disabled["auto_reconnect"] and not disabled["saved_login_available"]


@pytest.mark.anyio
async def test_new_instance_restores_only_after_live_switch_and_keeps_exact_tls_pin(tmp_path):
    integration, sessions, manager, store, _ = setup(tmp_path, RECORD)
    sessions.engine = None
    await integration.tick()
    assert not manager.connects
    sessions.engine = SimpleNamespace(connected=True)
    sessions.simulated = True
    await integration.tick()
    assert not manager.connects
    sessions.simulated = False
    sessions.engine.connected = False
    await integration.tick()
    assert not manager.connects
    sessions.engine.connected = True
    await integration.tick()
    assert manager.connects == [{**RECORD, "switch_host": sessions.host}]
    assert "verification_code" not in manager.connects[0]
    assert integration.status()["saved_login_matches_connection"]
    assert store.saves == 0


@pytest.mark.anyio
async def test_disconnect_suppresses_runtime_reconnect_but_shutdown_keeps_saved_opt_in(tmp_path):
    integration, sessions, manager, store, now = setup(tmp_path, RECORD)
    await integration.tick()
    result = await integration.disconnect()
    assert result["auto_reconnect"] and result["reconnect_state"] == "paused"
    now[0] += 1000
    sessions.engine = SimpleNamespace(connected=True)
    await integration.tick()
    assert len(manager.connects) == 1 and store.saved == RECORD
    await integration.close()
    assert store.saved == RECORD and store.clears == 0
    assert integration._saved is None


@pytest.mark.anyio
async def test_disable_reports_off_even_after_terminal_reconnect_error(tmp_path):
    integration, _, manager, store, _ = setup(tmp_path, RECORD)
    manager.error = ControllerError("authentication_failed", "Sign in again.", 401)
    await integration.tick()
    assert integration.status()["reconnect_state"] == "attention"
    status = await integration.preferences(False)
    assert status["reconnect_state"] == "off" and status["saved_login_error"] is None
    assert not store.saved
    await integration.tick()
    assert len(manager.connects) == 1


@pytest.mark.anyio
@pytest.mark.parametrize("code", ["authentication_failed", "certificate_changed", "certificate_untrusted",
                                  "two_factor_required", "verification_failed", "site_not_found",
                                  "switch_not_found"])
async def test_auth_tls_and_matching_failures_require_explicit_action(tmp_path, code):
    integration, _, manager, store, now = setup(tmp_path, RECORD)
    manager.error = ControllerError(code, "Fixture connection requires attention.")
    await integration.tick()
    for _ in range(5):
        now[0] += 1000
        await integration.tick()
    assert len(manager.connects) == 1
    assert integration.status()["reconnect_state"] == "attention"
    assert store.saved == RECORD
    manager.error = None
    await connect(integration)
    assert not integration.status()["saved_login_error"]


@pytest.mark.anyio
async def test_transient_failures_keep_retrying_once_a_minute_and_coalesce_parallel_ticks(tmp_path):
    # A controller that restarts can stay unreachable for longer than the first three waits.
    integration, _, manager, _, now = setup(tmp_path, RECORD)
    manager.error = ControllerError("unreachable", "Could not reach the controller.", 503)
    for index, delay in enumerate((5, 15, 60, 60, 60, 60), start=1):
        await asyncio.gather(integration.tick(), integration.tick())
        assert len(manager.connects) == index
        assert integration.status()["reconnect_state"] == "retrying"
        now[0] += delay - .1
        await integration.tick()
        assert len(manager.connects) == index
        now[0] += .1
    manager.error = None
    await integration.tick()
    assert len(manager.connects) == 7
    assert integration.status()["reconnect_state"] == "connected"
    assert integration.status()["saved_login_error"] is None


@pytest.mark.anyio
async def test_a_controller_that_stops_answering_is_refreshed_until_it_returns(tmp_path):
    integration, _, manager, _, now = setup(tmp_path, RECORD)
    await integration.tick()
    manager.refresh_error = ControllerError("unreachable", "Could not reach the controller over HTTPS.", 503)
    now[0] += 15
    for refreshes in range(1, 8):
        await integration.tick()
        assert manager.refreshes == refreshes and integration.status()["reconnect_state"] == "retrying"
        now[0] += 60
    manager.refresh_error = None
    await integration.tick()
    assert manager.refreshes == 8 and integration.status()["reconnect_state"] == "connected"


@pytest.mark.anyio
async def test_refresh_auth_failure_stops_polling_until_manual_connect(tmp_path):
    integration, _, manager, _, now = setup(tmp_path, RECORD)
    await integration.tick()
    manager.refresh_error = ControllerError("authentication_failed", "Sign in again.", 401)
    now[0] += 15
    await integration.tick()
    assert manager.refreshes == 1
    now[0] += 1000
    await integration.tick()
    assert manager.refreshes == 1


@pytest.mark.anyio
@pytest.mark.parametrize("operation,code", [("save", "controller_login_save_failed"),
                                           ("clear", "controller_login_remove_failed")])
async def test_storage_failure_preserves_session_and_actual_saved_state(tmp_path, operation, code):
    integration, _, manager, store, _ = setup(tmp_path, RECORD if operation == "clear" else None)
    await connect(integration)
    before = deepcopy(store.saved)
    store.fail = operation
    with pytest.raises(ControllerError) as error:
        await integration.preferences(operation == "save")
    assert error.value.code == code and error.value.status == 500
    assert store.saved == before and manager.status()["connected"]
    assert integration.status()["auto_reconnect"] is (before is not None)


@pytest.mark.anyio
async def test_active_different_controller_requires_explicit_resave(tmp_path):
    integration, _, manager, store, _ = setup(tmp_path, RECORD)
    different = {**RECORD, "url": "https://second.invalid", "password": "different-secret"}
    status = await connect(integration, different)
    assert status["auto_reconnect"] and not status["saved_login_matches_connection"]
    assert store.saved == RECORD and store.saves == 0
    saved = await integration.preferences(True)
    assert saved["saved_login_matches_connection"] and store.saved == different
    assert manager.status()["connected"]


@pytest.mark.anyio
async def test_remember_replaces_verified_login_once_and_keeps_saved_target_separate(tmp_path):
    integration, _, manager, store, _ = setup(tmp_path, RECORD)
    changed = {**RECORD, "url": "https://replacement.invalid", "password": "fixture-replacement"}
    before = integration.status()
    assert before["remember_on_connect_supported"] and not before["connected"]
    assert before["saved_target"] == {"url": RECORD["url"], "controller_kind": "self_hosted",
                                      "auth_method": "password", "console_id": None}
    request = ControllerLogin(**changed, remember=True)
    first = await integration.connect(request)
    assert first["saved_login_matches_connection"] and first["saved_target"]["url"] == changed["url"]
    assert changed["password"] not in json.dumps(first)
    assert "remember" not in manager.connects[-1] and store.saved == changed
    again = await integration.connect(request)
    assert again["saved_login_matches_connection"] and store.saves == 1
    disabled = await integration.preferences(False)
    assert disabled["saved_target"] is None and disabled["connected"]


@pytest.mark.anyio
async def test_remember_failure_keeps_new_session_reports_retry_and_preserves_old_record(tmp_path):
    integration, _, _, store, now = setup(tmp_path, RECORD)
    changed = {**RECORD, "url": "https://replacement.invalid", "password": "fixture-replacement"}
    store.fail = "save"
    result = await integration.connect(ControllerLogin(**changed, remember=True))
    assert result["connected"] and result["can_save_login"] and not result["saved_login_matches_connection"]
    assert result["saved_login_error"]["code"] == "controller_login_save_failed"
    assert result["saved_target"]["url"] == RECORD["url"] and store.saved == RECORD
    now[0] += 16
    await integration.tick()
    assert integration.status()["saved_login_error"] == result["saved_login_error"]
    store.fail = None
    saved = await integration.preferences(True)
    assert saved["saved_login_matches_connection"] and saved["saved_login_error"] is None
    assert store.saved == changed


@pytest.mark.anyio
@pytest.mark.parametrize("switch_changed", [True, False])
async def test_interrupted_connect_finishes_remember_only_on_original_live_switch(tmp_path, switch_changed):
    integration, sessions, manager, store, _ = setup(tmp_path)
    manager.gate.clear()
    request = asyncio.create_task(integration.connect(ControllerLogin(**RECORD, remember=True)))
    await manager.entered.wait()
    request.cancel()
    if switch_changed:
        sessions.engine = SimpleNamespace(connected=True)
    manager.gate.set()
    with pytest.raises(asyncio.CancelledError):
        await request
    assert store.saved == (None if switch_changed else RECORD)
    assert integration.status()["saved_login_matches_connection"] is (not switch_changed)
    assert manager.status()["connected"] is (not switch_changed)


@pytest.mark.anyio
async def test_corrupt_store_blocks_automatic_attempt_without_hiding_manual_save(tmp_path):
    integration, sessions, manager, store, _ = setup(tmp_path, RECORD)
    store.fail = "load"
    integration = ControllerIntegration(sessions, manager, data_dir=tmp_path, store=store)
    await integration.tick()
    assert not manager.connects and integration.status()["auto_reconnect"]
    assert not integration.status()["saved_login_available"]
    assert integration.status()["saved_login_error"]["code"] == "saved_controller_login_unavailable"
    await connect(integration)
    store.fail = None
    await integration.preferences(True)
    assert integration.status()["saved_login_available"]


@pytest.mark.anyio
async def test_cancelled_restore_drains_before_clearing_manager_and_keeps_record(tmp_path):
    integration, _, manager, store, _ = setup(tmp_path, RECORD)
    manager.gate.clear()
    task = asyncio.create_task(integration.tick())
    await manager.entered.wait()
    task.cancel()
    await asyncio.sleep(0)
    assert not task.done()
    close = asyncio.create_task(integration.close())
    await asyncio.sleep(0)
    assert manager.disconnects == 0
    manager.gate.set()
    with pytest.raises(asyncio.CancelledError):
        await task
    await close
    assert manager.disconnects == 1 and not manager.status()["connected"]
    assert store.saved == RECORD


@pytest.mark.anyio
async def test_switch_change_during_restore_discards_stale_controller_session(tmp_path):
    integration, sessions, manager, _, _ = setup(tmp_path, RECORD)
    manager.gate.clear()
    task = asyncio.create_task(integration.tick())
    await manager.entered.wait()
    sessions.engine = None
    manager.gate.set()
    await task
    assert manager.disconnects == 1 and not manager.status()["connected"]


@pytest.mark.parametrize("body", [{"auto_reconnect": "true"}, {"auto_reconnect": 1}, {},
                                 {"auto_reconnect": True, "password": "fixture-hidden-secret"}])
def test_preference_validation_is_strict_and_redacts_extra_inputs(config, monkeypatch, body):
    integration, sessions, manager, store, _ = setup(config.data_dir)
    config.api.token = TOKEN
    sessions.info = lambda: {"logged_in": True, "simulated": False}
    async def close():
        pass
    sessions.close = close
    monkeypatch.setattr(api, "SessionManager", lambda *args, **kwargs: sessions)
    monkeypatch.setattr(controller_api, "ControllerManager", lambda: manager)
    monkeypatch.setattr(controller_api, "ControllerLoginStore", lambda path: store)
    app = api.create_app(config, autoconnect=False)
    with TestClient(app, base_url=BASE, headers=HEADERS) as client:
        response = client.put("/api/controller/preferences", json=body)
        assert response.status_code == 422
        assert "fixture-hidden-secret" not in response.text
        assert not store.saves and not store.clears


@pytest.mark.parametrize("value", ["false", "true", 1, 0, None])
def test_connect_remember_requires_a_boolean(value):
    with pytest.raises(ValidationError):
        ControllerLogin(**RECORD, remember=value)


def test_login_repr_omits_password_and_verification_code():
    login = ControllerLogin(**RECORD, verification_code="123456")
    assert RECORD["password"] not in repr(login) and "123456" not in repr(login)


def test_preferences_api_saves_only_after_apply_and_disable_keeps_active_session(config, monkeypatch):
    _, sessions, manager, store, _ = setup(config.data_dir)
    config.api.token = TOKEN

    async def close():
        pass

    sessions.close = close
    monkeypatch.setattr(api, "SessionManager", lambda *args, **kwargs: sessions)
    monkeypatch.setattr(controller_api, "ControllerManager", lambda: manager)
    monkeypatch.setattr(controller_api, "ControllerLoginStore", lambda path: store)
    app = api.create_app(config, autoconnect=False)
    with TestClient(app, base_url=BASE, headers=HEADERS) as client:
        rejected = client.put("/api/controller/preferences", json={"auto_reconnect": True})
        assert rejected.status_code == 409 and not store.saved
        connected = client.post("/api/controller/connect", json={**RECORD, "verification_code": "123456"})
        assert connected.status_code == 200 and not store.saved
        saved = client.put("/api/controller/preferences", json={"auto_reconnect": True})
        assert saved.status_code == 200
        assert saved.json()["auto_reconnect"] and saved.json()["saved_login_matches_connection"]
        assert store.saved == RECORD and "verification_code" not in store.saved
        assert RECORD["password"] not in saved.text and "123456" not in saved.text
        assert client.get("/api/controller").json()["saved_login_available"]
        disabled = client.put("/api/controller/preferences", json={"auto_reconnect": False})
        assert disabled.status_code == 200 and disabled.json()["connected"]
        assert disabled.json()["reconnect_state"] == "off" and not store.saved
        assert manager.disconnects == 0
    assert manager.disconnects == 1
