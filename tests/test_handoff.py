import asyncio
import json

import httpx
import pytest
from fastapi.testclient import TestClient

from etherlighting_evolved.api import create_app
from etherlighting_evolved.handoff import HandoffIntent
from etherlighting_evolved.session import LoginError, SessionManager
from etherlighting_evolved.transport import AuthenticationError, SimulatedTransport, TransportError


class MemorySwitch:
    def __init__(self):
        self.simulator = SimulatedTransport(latency=0)
        self.host_key_fingerprint = "SHA256:handoff-fixture-peer-identity"
        self.close_failures = 0
        self.close_calls = 0
        self.connect_calls = 0

    def __getattr__(self, name):
        return getattr(self.simulator, name)

    async def connect(self):
        self.connect_calls += 1
        await self.simulator.connect()

    async def close(self):
        self.close_calls += 1
        if self.close_failures:
            self.close_failures -= 1
            raise TransportError("fixture close was not confirmed")
        await self.simulator.close()


async def connect(sessions):
    info = await sessions.connect("switch.invalid", "fixture", "fixture")
    if pending := info["pending_scene_choice"]:
        await sessions.choose_scene(pending["connection_id"], "start_new", pending["legacy_revision"])


@pytest.mark.anyio
async def test_handoff_preserves_scene_and_saved_login_and_pause_survives_restart(config, monkeypatch):
    transport = MemorySwitch()
    sessions = SessionManager(config, transport_factory=lambda **kw: transport)
    monkeypatch.setattr(sessions.saved, "save", lambda *args: pytest.fail("saved login rewritten"))
    monkeypatch.setattr(sessions.saved, "clear", lambda: pytest.fail("saved login deleted"))
    await connect(sessions)
    engine, store = sessions.engine, sessions._scene_store
    engine.set_brightness(47)
    lease = engine.switch.output_lease
    info = await sessions.handoff()
    assert info["handed_off"] and info["handoff_state"] == "complete"
    assert info["automatic_connect_paused"] and not info["connected"] and not info["logged_in"]
    assert not transport.connected and not engine._tasks and not sessions._owned_switches
    assert store.load()["brightness"] == 47 and lease.closed
    restarted = SessionManager(config, transport_factory=lambda **kw: pytest.fail("automatic SSH"))
    await restarted.auto_connect()
    assert (await restarted.restore_saved())["handed_off"]
    assert restarted.info()["automatic_connect_paused"]


@pytest.mark.anyio
async def test_explicit_saved_resume_clears_pause_only_after_verified_activation(config, monkeypatch):
    transport = MemorySwitch()
    sessions = SessionManager(config, transport_factory=lambda **kw: transport)
    saved = {"host": "switch.invalid", "port": 22, "username": "fixture", "password": "fixture"}
    monkeypatch.setattr(sessions.saved, "load", lambda: saved.copy())
    await connect(sessions)
    await sessions.handoff()
    count = transport.connect_calls
    await sessions.restore_saved()
    assert transport.connect_calls == count
    async def failed():
        raise AuthenticationError("fixture credentials rejected")
    with monkeypatch.context() as patch:
        patch.setattr(transport, "connect", failed)
        with pytest.raises(LoginError, match="rejected"):
            await sessions.restore_saved(resume=True)
    assert sessions.info()["automatic_connect_paused"]
    info = await sessions.restore_saved(resume=True)
    assert info["logged_in"] and not info["automatic_connect_paused"] and info["handoff_state"] == "none"
    assert HandoffIntent(sessions._handoff.path).state == "none"
    await sessions.close()


@pytest.mark.anyio
async def test_failed_close_keeps_engine_cleanup_handle_and_retry_really_closes(config):
    transport = MemorySwitch()
    sessions = SessionManager(config, transport_factory=lambda **kw: transport)
    await connect(sessions)
    engine = sessions.engine
    lease = engine.switch.output_lease
    transport.close_failures = 1
    with pytest.raises(LoginError) as failed:
        await sessions.handoff()
    assert failed.value.code == "handoff_unconfirmed"
    assert sessions.engine is None and sessions._cleanup_engines == [engine]
    assert transport.connected and all(path.exists() for path in lease.paths)
    assert sessions.info()["handoff_state"] == "unconfirmed" and not sessions.info()["handed_off"]
    calls = transport.close_calls
    with pytest.raises(LoginError):
        await connect(sessions)
    result = await sessions.handoff()
    assert result["handed_off"] and transport.close_calls > calls
    assert not transport.connected and not sessions._cleanup_engines and lease.closed


@pytest.mark.anyio
async def test_failed_pending_candidate_close_is_retained_for_retry(config):
    transport = MemorySwitch()
    sessions = SessionManager(config, transport_factory=lambda **kw: transport)
    await sessions.connect("switch.invalid", "fixture", "fixture")
    pending = sessions._pending
    transport.close_failures = 1
    with pytest.raises(LoginError):
        await sessions.handoff()
    assert sessions._pending is pending and transport.connected
    assert sessions.info()["pending_scene_choice"] is None
    assert (await sessions.handoff())["handed_off"]
    assert sessions._pending is None and not transport.connected


@pytest.mark.anyio
async def test_queued_automatic_connection_rechecks_pause_under_lock(config):
    sessions = SessionManager(config, transport_factory=lambda **kw: pytest.fail("queued automatic SSH"))
    await sessions._lock.acquire()
    queued = asyncio.create_task(sessions.connect("switch.invalid", "fixture", "fixture", automatic=True))
    await asyncio.sleep(0)
    handoff = asyncio.create_task(sessions.handoff())
    await asyncio.sleep(0)
    sessions._lock.release()
    await queued
    assert (await handoff)["handed_off"]


@pytest.mark.anyio
async def test_cancelled_handoff_caller_does_not_cancel_cleanup_and_retry_coalesces(config):
    sessions = SessionManager(config)
    await sessions.connect("demo")
    engine = sessions.engine
    original_stop = engine.stop
    entered, release = asyncio.Event(), asyncio.Event()
    calls = 0
    async def stop():
        nonlocal calls
        calls += 1
        entered.set()
        await release.wait()
        await original_stop()
    engine.stop = stop
    caller = asyncio.create_task(sessions.handoff())
    await entered.wait()
    assert sessions.engine is None and sessions.schedule_identity() is None
    assert sessions.info()["automatic_connect_paused"]
    caller.cancel()
    with pytest.raises(asyncio.CancelledError):
        await caller
    retry = asyncio.create_task(sessions.handoff())
    release.set()
    assert (await retry)["handed_off"] and calls == 1


@pytest.mark.anyio
async def test_http_cannot_resume_or_replace_engine_during_pending_cleanup(config):
    app = create_app(config, autoconnect=False)
    sessions = app.state.sessions
    await sessions.connect("demo")
    engine = sessions.engine
    original_stop = engine.stop
    entered, release = asyncio.Event(), asyncio.Event()
    async def stop():
        entered.set()
        await release.wait()
        await original_stop()
    engine.stop = stop
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app),
                                base_url="http://127.0.0.1:8787") as client:
        handoff = asyncio.create_task(client.post("/api/session/handoff"))
        await entered.wait()
        resumed = await client.post("/api/resume")
        assert resumed.status_code == 409 and resumed.json()["detail"]["code"] == "handoff_unconfirmed"
        login = await client.post("/api/session/login", json={"host": "demo"})
        assert login.status_code == 409 and login.json()["detail"]["code"] == "handoff_unconfirmed"
        state = (await client.get("/api/session")).json()
        assert state["handoff_state"] == "pending" and state["automatic_connect_paused"]
        assert not state["handed_off"] and not state["logged_in"]
        release.set()
        assert (await handoff).json()["handed_off"]


@pytest.mark.anyio
async def test_persist_failure_still_stops_output_but_never_claims_saved_handoff(config, monkeypatch):
    sessions = SessionManager(config)
    await sessions.connect("demo")
    transport = sessions.engine.switch.transport
    original = sessions._handoff.save
    def failed(state):
        raise OSError("fixture storage unavailable")
    monkeypatch.setattr(sessions._handoff, "save", failed)
    with pytest.raises(LoginError) as result:
        await sessions.handoff()
    assert result.value.code == "handoff_save_failed"
    assert not transport.connected and sessions.info()["automatic_connect_paused"]
    assert not sessions.info()["handed_off"]
    monkeypatch.setattr(sessions._handoff, "save", original)
    assert (await sessions.handoff())["handed_off"]


@pytest.mark.anyio
async def test_scene_save_failure_still_closes_ssh_and_retries_original_scene(config, monkeypatch):
    sessions = SessionManager(config)
    await sessions.connect("demo")
    engine, store = sessions.engine, sessions._scene_store
    engine.set_brightness(43)
    original_save = store.save
    def failed(scene):
        raise OSError("fixture scene storage unavailable")
    monkeypatch.setattr(store, "save", failed)
    with pytest.raises(LoginError) as result:
        await sessions.handoff()
    assert result.value.code == "handoff_save_failed"
    assert not engine.switch.connected and not engine._tasks
    assert sessions._unsaved_scenes and not sessions.info()["handed_off"]
    monkeypatch.setattr(store, "save", original_save)
    assert (await sessions.handoff())["handed_off"]
    assert store.load()["brightness"] == 43


@pytest.mark.anyio
async def test_failed_scene_save_is_recovered_before_reconnect_and_handoff(config, monkeypatch):
    sessions = SessionManager(config)
    await sessions.connect("demo")
    store = sessions._scene_store
    store.save(sessions.engine.snapshot())
    sessions.engine.set_brightness(77)
    with monkeypatch.context() as patch:
        def failed(scene):
            raise OSError("fixture scene storage unavailable")
        patch.setattr(store, "save", failed)
        await sessions.disconnect()
    assert store.load()["brightness"] == 100 and sessions._unsaved_scenes
    await sessions.connect("demo")
    assert sessions.engine.brightness == 77 and store.load()["brightness"] == 77
    assert not sessions._unsaved_scenes
    assert (await sessions.handoff())["handed_off"]
    assert store.load()["brightness"] == 77


@pytest.mark.anyio
async def test_failed_recovery_blocks_activation_and_retains_scene_for_retry(config, monkeypatch):
    sessions = SessionManager(config)
    await sessions.connect("demo")
    store = sessions._scene_store
    store.save(sessions.engine.snapshot())
    sessions.engine.set_brightness(77)
    with monkeypatch.context() as patch:
        def failed(scene):
            raise OSError("fixture scene storage unavailable")
        patch.setattr(store, "save", failed)
        await sessions.disconnect()
        async def forbidden_start(engine):
            pytest.fail("Output started before pending scene recovery")
        patch.setattr("etherlighting_evolved.session.Engine.start", forbidden_start)
        with pytest.raises(LoginError) as error:
            await sessions.connect("demo")
        assert error.value.code == "scene_save_failed"
        assert sessions.engine is None and not sessions._owned_switches
        assert sessions._unsaved_scenes and store.load()["brightness"] == 100
    await sessions.connect("demo")
    assert sessions.engine.brightness == 77
    await sessions.close()


@pytest.mark.anyio
async def test_replacement_reads_scene_after_current_session_finishes_saving(config):
    sessions = SessionManager(config)
    await sessions.connect("demo")
    sessions._scene_store.save(sessions.engine.snapshot())
    sessions.engine.set_brightness(61)
    await sessions.connect("demo")
    assert sessions.engine.brightness == 61
    assert sessions._scene_store.load()["brightness"] == 61
    await sessions.close()


@pytest.mark.anyio
async def test_current_scene_wins_over_a_stale_retained_snapshot_during_handoff(config):
    sessions = SessionManager(config)
    await sessions.connect("demo")
    store = sessions._scene_store
    current = sessions.engine.snapshot()
    store.save(current)
    sessions._unsaved_scenes[store.path] = (store, {**current, "brightness": 77})
    assert (await sessions.handoff())["handed_off"]
    assert store.load()["brightness"] == current["brightness"]
    assert not sessions._unsaved_scenes


@pytest.mark.anyio
async def test_current_scene_wins_over_a_stale_retained_snapshot_during_replacement(config):
    sessions = SessionManager(config)
    await sessions.connect("demo")
    store = sessions._scene_store
    current = sessions.engine.snapshot()
    store.save(current)
    sessions._unsaved_scenes[store.path] = (store, {**current, "brightness": 77})
    await sessions.connect("demo")
    assert sessions.engine.brightness == current["brightness"]
    assert store.load()["brightness"] == current["brightness"]
    assert not sessions._unsaved_scenes
    await sessions.close()


@pytest.mark.anyio
async def test_unexpected_idle_cleanup_error_still_closes_tasks_and_ssh_without_erasing_uncertainty(config):
    transport = MemorySwitch()
    sessions = SessionManager(config, transport_factory=lambda **kw: transport)
    await connect(sessions)
    engine = sessions.engine
    async def broken():
        raise RuntimeError("fixture internal cleanup failure")
    engine.wait_for_output_idle = broken
    with pytest.raises(LoginError):
        await sessions.handoff()
    assert not engine._tasks and not transport.connected
    assert engine.switch.output_lease.uncertain
    with pytest.raises(TransportError):
        engine.resume()
    assert not sessions.info()["handed_off"]


@pytest.mark.anyio
@pytest.mark.parametrize("content", ['{"version":1,"state":"pending"}', 'invalid', '{"version":2}'])
async def test_previous_unconfirmed_or_corrupt_intent_cannot_be_certified_by_empty_retry(config, content):
    config.data_dir.mkdir()
    (config.data_dir / "switch-connection-intent.json").write_text(content)
    sessions = SessionManager(config, transport_factory=lambda **kw: pytest.fail("unexpected SSH"))
    await sessions.auto_connect()
    assert (await sessions.restore_saved())["automatic_connect_paused"]
    with pytest.raises(LoginError) as result:
        await sessions.handoff()
    assert result.value.code == "handoff_recovery_required"
    assert sessions.info()["handoff_state"] == "unconfirmed"
    assert not sessions.info()["handed_off"]


def test_api_handoff_does_not_disconnect_controller_and_saved_restore_requires_resume(config, monkeypatch):
    app = create_app(config, autoconnect=False)
    with TestClient(app, base_url="http://127.0.0.1:8787") as client:
        assert client.post("/api/session/login", json={"host": "demo"}).status_code == 200
        with monkeypatch.context() as patch:
            async def forbidden():
                pytest.fail("controller disconnected by switch handoff")
            patch.setattr(app.state.controller, "disconnect", forbidden)
            patch.setattr(app.state.controller, "close", forbidden)
            response = client.post("/api/session/handoff")
            assert response.status_code == 200 and response.json()["handed_off"], response.text
            assert client.post("/api/session/restore").json()["automatic_connect_paused"]
            assert client.post("/api/session/restore?resume=true").status_code == 409
            assert client.post("/api/resume").status_code == 409
            assert client.post("/api/session/handoff").json()["handed_off"]
        record = json.loads(app.state.sessions._handoff.path.read_text())
        assert record == {"version": 1, "state": "complete"}
