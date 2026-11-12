import asyncio
import json
import threading
from types import SimpleNamespace

import pytest
from test_controller import FakeClient
from test_controller_reconnect import Store

from etherlighting_evolved.controller import ControllerError, ControllerManager
from etherlighting_evolved.controller_api import ControllerIntegration, ControllerLogin

OLD_URL = "https://current.invalid"
NEW_URL = "https://candidate.invalid"
HOST = "192.0.2.1"
SITES = [{"name": "north", "desc": "North"}, {"name": "south", "desc": "South"}]


def make_manager():
    def factory(*args, **kwargs):
        client = FakeClient(*args, **kwargs)
        client.sites = SITES if client.url == NEW_URL else [{"name": "default"}]
        return client
    return ControllerManager(client_factory=factory)


async def connect_active(manager):
    await manager.connect(url=OLD_URL, username="current-user", password="current-secret", switch_host=HOST)
    return manager._client


async def choose_candidate(manager, kind="unifi_os"):
    return await manager.connect(url=NEW_URL, username="candidate-user", password="candidate-secret",
                                 controller_kind=kind, switch_host=HOST)


@pytest.mark.anyio
@pytest.mark.parametrize("kind", ["self_hosted", "unifi_os"])
async def test_site_prompt_preserves_active_telemetry_and_cancel_discards_only_candidate(kind):
    manager = make_manager()
    old = await connect_active(manager)
    original_ports = manager.enrich_ports([{"port": 1}], HOST)
    pending = await choose_candidate(manager, kind)
    candidate = manager._pending
    assert pending["connected"] and pending["error"] is None and pending["url"] == OLD_URL
    assert pending["controller_kind"] == "self_hosted" and pending["site"] == "default"
    assert pending["pending_site_choice"] == {"url": NEW_URL, "controller_kind": kind,
        "auth_method": "password", "console_id": None,
        "capabilities": {"client_mapping": True, "port_metadata": True},
        "sites": [{"id": "north", "name": "North"}, {"id": "south", "name": "South"}]}
    assert manager._client is old and old.authenticated and old.password == "current-secret"
    assert manager.enrich_ports([{"port": 1}], HOST) == original_ports
    assert manager.diagnostics(HOST)["available"]
    assert not manager.can_save_login(HOST)
    with pytest.raises(ControllerError, match="Connect your controller"):
        await manager.login_for_saving(HOST)
    assert "secret" not in json.dumps(pending) and "candidate-user" not in json.dumps(pending)
    refreshed = await manager.refresh(HOST)
    assert refreshed["connected"] and refreshed["pending_site_choice"] == pending["pending_site_choice"]
    for _ in range(2):
        canceled = await manager.cancel()
        assert canceled["connected"] and canceled["pending_site_choice"] is None
        assert manager._client is old and manager.can_save_login(HOST)
    assert not candidate.authenticated and not candidate.password
    await manager.disconnect()


@pytest.mark.anyio
async def test_failed_site_validation_or_topology_keeps_active_and_retry_commits_after_refresh():
    manager = make_manager()
    old = await connect_active(manager)
    await choose_candidate(manager)
    candidate = manager._pending
    with pytest.raises(ControllerError) as invalid:
        await manager.connect(url=NEW_URL, site="missing", controller_kind="unifi_os", switch_host=HOST)
    assert invalid.value.code == "site_not_found"
    assert manager._client is old and manager.status()["connected"]
    candidate.error = ControllerError("unreachable", "Could not reach the controller over HTTPS.", 503)
    with pytest.raises(ControllerError):
        await manager.connect(url=NEW_URL, site="south", controller_kind="unifi_os", switch_host=HOST)
    assert manager._client is old and manager.status()["connected"]
    assert manager.status()["pending_site_choice"] and old.authenticated
    candidate.error = None
    entered, release = threading.Event(), threading.Event()
    original_get = candidate.get

    def held_get(path):
        if path.endswith("/stat/device"):
            entered.set()
            if not release.wait(2):
                raise AssertionError("fixture topology read was not released")
        return original_get(path)

    candidate.get = held_get
    task = asyncio.create_task(manager.connect(url=NEW_URL, site="south", controller_kind="unifi_os", switch_host=HOST))
    try:
        assert await asyncio.to_thread(entered.wait, 1)
        assert manager._client is old and manager.status()["connected"]
        assert manager.enrich_ports([{"port": 1}], HOST)[0]["name"] == "Desk"
        assert old.authenticated and old.password == "current-secret"
    finally:
        release.set()
    connected = await task
    assert connected["connected"] and connected["url"] == NEW_URL and connected["site"] == "south"
    assert connected["pending_site_choice"] is None and manager._pending is None
    assert manager._client is candidate and candidate.password == "candidate-secret"
    assert not old.authenticated and not old.password
    await manager.disconnect()


@pytest.mark.anyio
async def test_initial_site_prompt_cancel_discards_candidate_and_resets_configuration():
    manager = make_manager()
    pending = await choose_candidate(manager)
    candidate = manager._pending
    assert not pending["connected"] and pending["error"]["code"] == "site_required"
    canceled = await manager.cancel()
    assert not canceled["configured"] and canceled["error"] is None
    assert canceled["pending_site_choice"] is None and canceled["sites"] == []
    assert not candidate.authenticated and not candidate.password


@pytest.mark.anyio
async def test_integration_pending_choice_keeps_saved_record_and_refresh_cancel_state(tmp_path):
    manager, store = make_manager(), Store()
    now = [100.0]
    sessions = SimpleNamespace(host=HOST, simulated=False, engine=SimpleNamespace(connected=True))
    integration = ControllerIntegration(sessions, manager, data_dir=tmp_path, store=store, clock=lambda: now[0])
    await integration.connect(ControllerLogin(url=OLD_URL, username="current-user", password="current-secret"))
    await integration.preferences(True)
    saved = dict(store.saved)
    old = manager._client
    pending = await integration.connect(ControllerLogin(url=NEW_URL, username="candidate-user",
        password="candidate-secret", controller_kind="unifi_os", remember=True))
    assert pending["connected"] and pending["pending_site_choice"] and not pending["can_save_login"]
    assert not integration._blocked and not integration._paused
    with pytest.raises(ControllerError):
        await integration.preferences(True)
    calls = len(old.calls)
    now[0] += 16
    await integration.tick()
    assert len(old.calls) > calls and manager._client is old and store.saved == saved
    canceled = await integration.cancel()
    assert canceled["connected"] and canceled["saved_login_matches_connection"]
    assert canceled["auto_reconnect"] and not canceled["pending_site_choice"]
    assert store.saved == saved and store.saves == 1
    await integration.close()


@pytest.mark.anyio
@pytest.mark.parametrize("remember", [True, False])
async def test_remember_site_choice_uses_final_choice_after_target_validation(tmp_path, remember):
    sessions = SimpleNamespace(host=HOST, simulated=False, engine=SimpleNamespace(connected=True))
    store = Store()
    integration = ControllerIntegration(sessions, make_manager(), data_dir=tmp_path, store=store)
    candidate = {"url": NEW_URL, "controller_kind": "unifi_os"}
    pending = await integration.connect(ControllerLogin(**candidate, username="fixture-user",
        password="fixture-secret", remember=True))
    assert pending["pending_site_choice"] and not store.saves
    with pytest.raises(ControllerError):
        await integration.connect(ControllerLogin(**candidate, site="absent", remember=True))
    assert not store.saves
    result = await integration.connect(ControllerLogin(**candidate, site="south", remember=remember))
    assert result["connected"] and not result["pending_site_choice"]
    assert result["saved_login_matches_connection"] is remember
    assert store.saves == int(remember)
    if remember:
        assert store.saved["site"] == "south" and store.saved["url"] == NEW_URL


@pytest.mark.anyio
async def test_pending_first_connection_prevents_saved_login_restore_from_replacing_choice(tmp_path):
    manager = make_manager()
    saved = {"url": OLD_URL, "username": "current-user", "password": "current-secret", "site": "default",
             "certificate_sha256": None, "controller_kind": "self_hosted"}
    store = Store(saved)
    sessions = SimpleNamespace(host=HOST, simulated=False, engine=SimpleNamespace(connected=True))
    integration = ControllerIntegration(sessions, manager, data_dir=tmp_path, store=store)
    await integration.connect(ControllerLogin(url=NEW_URL, username="candidate-user",
        password="candidate-secret", controller_kind="unifi_os"))
    candidate = manager._pending
    await integration.tick()
    assert manager._pending is candidate and manager._client is None
    assert candidate.calls == ["/api/self/sites"] and store.saved == saved
    await integration.close()
