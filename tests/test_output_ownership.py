import argparse
import asyncio
from concurrent.futures import ThreadPoolExecutor
from contextlib import contextmanager, nullcontext

import pytest
from fastapi.testclient import TestClient

from etherlighting_evolved import cli
from etherlighting_evolved import ownership as ownership_module
from etherlighting_evolved import session as session_module
from etherlighting_evolved.api import create_app
from etherlighting_evolved.app_lock import AlreadyRunningError
from etherlighting_evolved.color import Color
from etherlighting_evolved.config import Config
from etherlighting_evolved.engine import Engine
from etherlighting_evolved.ownership import OutputJournal, OutputOwnershipError
from etherlighting_evolved.session import LoginError, SessionManager
from etherlighting_evolved.switch import EtherlightSwitch
from etherlighting_evolved.transport import SimulatedTransport, SSHTransport, TransportError

PEER = "SHA256:fixture-authenticated-device-key"


class MemoryDevice:
    def __init__(self, fingerprint=PEER):
        self.host_key_fingerprint = fingerprint
        self.simulator = SimulatedTransport(latency=0)
        self.writes = []
        self.on_write = lambda: None

    def __getattr__(self, name):
        return getattr(self.simulator, name)

    async def write_behavior(self, behavior):
        self.on_write()
        self.writes.append(behavior)
        await self.simulator.write_behavior(behavior)

    async def run(self, command, **kwargs):
        if "ubus call etherlight.mcu set" in command:
            self.on_write()
            self.writes.append(command)
        return await self.simulator.run(command, **kwargs)


async def activate(sessions, choice="start_new"):
    pending = sessions.info()["pending_scene_choice"]
    return await sessions.choose_scene(pending["connection_id"], choice, pending["legacy_revision"])


def test_crash_marker_survives_alias_key_change_and_new_journal(tmp_path):
    journal = OutputJournal(tmp_path)
    lease = journal.claim("switch.invalid", 22, PEER)
    assert all(path.exists() for path in lease.paths)
    text = "".join(path.read_text() for path in lease.paths)
    assert PEER not in text and "switch.invalid" not in text
    for host, peer in [("alias.invalid", PEER), ("switch.invalid", PEER + "changed")]:
        with pytest.raises(OutputOwnershipError):
            OutputJournal(tmp_path).claim(host, 22, peer)
    lease.finish()
    OutputJournal(tmp_path).claim("switch.invalid", 22, PEER).finish()


def test_concurrent_owners_cannot_both_claim(tmp_path):
    def claim():
        try:
            return OutputJournal(tmp_path).claim("fixture.invalid", 22, PEER)
        except OutputOwnershipError:
            return None
    with ThreadPoolExecutor(max_workers=2) as pool:
        results = list(pool.map(lambda _: claim(), range(2)))
    assert sum(result is not None for result in results) == 1
    next(result for result in results if result is not None).finish()


def test_corrupt_record_and_wrong_owner_fail_closed(tmp_path):
    journal = OutputJournal(tmp_path)
    lease = journal.claim("fixture.invalid", 22, PEER)
    lease.paths[0].write_text("partial")
    with pytest.raises(OutputOwnershipError):
        journal.check("fixture.invalid", 22, PEER, current=lease)
    with pytest.raises(OutputOwnershipError):
        lease.finish()
    assert all(path.exists() for path in lease.paths)


def test_unconfirmed_journal_flush_prevents_claim(tmp_path, monkeypatch):
    def failed_flush(descriptor):
        raise OSError("fixture storage unavailable")
    monkeypatch.setattr(ownership_module.os, "fsync", failed_flush)
    journal = OutputJournal(tmp_path)
    with pytest.raises(OutputOwnershipError):
        journal.claim("fixture.invalid", 22, PEER)
    assert any(path.exists() for path in journal.paths("fixture.invalid", 22, PEER))


@pytest.mark.anyio
@pytest.mark.parametrize("error", [TransportError("write timed out"), asyncio.CancelledError()])
async def test_failed_or_cancelled_write_retains_record(tmp_path, error):
    transport = MemoryDevice()
    switch = EtherlightSwitch(transport)
    lease = switch.output_lease = OutputJournal(tmp_path).claim("fixture.invalid", 22, PEER)

    async def failed():
        raise error

    with pytest.raises(type(error)):
        await switch._paced_call(failed)
    assert lease.uncertain
    with pytest.raises(OutputOwnershipError):
        lease.finish()
    assert all(path.exists() for path in lease.paths)


@pytest.mark.anyio
async def test_real_transport_cannot_write_without_journal():
    switch = EtherlightSwitch(SSHTransport("fixture.invalid"))

    async def forbidden():
        pytest.fail("must not submit a device command")

    with pytest.raises(OutputOwnershipError):
        await switch._paced_call(forbidden)


@pytest.mark.anyio
async def test_changed_authenticated_peer_cannot_write(tmp_path):
    transport = MemoryDevice()
    switch = EtherlightSwitch(transport)
    switch.output_lease = OutputJournal(tmp_path).claim("fixture.invalid", 22, PEER)
    transport.host_key_fingerprint = PEER + "changed"
    with pytest.raises(OutputOwnershipError):
        await switch.set_port(1, Color.parse("blue"))
    assert not transport.writes and switch.output_lease.uncertain


@pytest.mark.anyio
async def test_marker_precedes_first_initialization_and_clears_after_stop(config):
    transport = MemoryDevice()
    sessions = SessionManager(config, transport_factory=lambda **kw: transport)
    await sessions.connect("fixture.invalid", "fixture", "fixture")
    assert not transport.writes
    def observed_write():
        assert all(path.exists() for path in sessions.output_journal.paths("fixture.invalid", 22, PEER))
    transport.on_write = observed_write
    await activate(sessions)
    lease = sessions.engine.switch.output_lease
    assert transport.writes[0] == "steady"
    await sessions.close()
    assert not any(path.exists() for path in lease.paths)


@pytest.mark.anyio
async def test_uncertain_previous_owner_blocks_new_session_without_reset(config):
    sessions = SessionManager(config, transport_factory=lambda **kw: MemoryDevice())
    sessions.output_journal.claim("fixture.invalid", 22, PEER)
    with pytest.raises(LoginError) as exc:
        await sessions.connect("alias.invalid", "fixture", "fixture")
    assert exc.value.code == "output_recovery_required" and sessions.engine is None


def test_custom_data_directory_does_not_change_output_journal(tmp_path):
    a = SessionManager(Config(data_dir=tmp_path / "a"))
    b = SessionManager(Config(data_dir=tmp_path / "b"))
    assert a.output_journal.directory == b.output_journal.directory == OutputJournal().directory


@pytest.mark.anyio
@pytest.mark.parametrize("action", [cli._set, cli._benchmark])
async def test_cli_acquires_owner_before_credentials(monkeypatch, action):
    events = []
    @contextmanager
    def occupied():
        events.append("lock")
        raise AlreadyRunningError("another controller owns output")
        yield
    monkeypatch.setattr(cli, "AppInstanceLock", occupied)
    monkeypatch.setattr(cli, "_switch", lambda args: pytest.fail("must not read credentials"))
    args = argparse.Namespace(port=1, ports=8, color="blue", level=100, count=1)
    with pytest.raises(AlreadyRunningError):
        await action(args)
    assert events == ["lock"]


@pytest.mark.anyio
async def test_cli_acknowledged_completion_clears_journal(config, monkeypatch):
    transport = MemoryDevice()
    monkeypatch.setattr(cli, "AppInstanceLock", nullcontext)
    monkeypatch.setattr(cli, "_switch", lambda args: EtherlightSwitch(transport))
    args = argparse.Namespace(host="fixture.invalid", ssh_port=22, port=1, ports=8, color="blue", level=100)
    assert await cli._set(args) == 0
    assert transport.writes
    assert not any(path.exists() for path in OutputJournal().paths(args.host, 22, PEER))


@pytest.mark.anyio
async def test_scene_choice_preserves_legacy_and_binds_only_selected_device(config):
    transports = []
    def factory(**kw):
        device = MemoryDevice(PEER + kw["host"])
        transports.append(device)
        return device
    sessions = SessionManager(config, transport_factory=factory)
    legacy = Engine(EtherlightSwitch(SimulatedTransport()), brightness=37).snapshot()
    sessions.scenes[False].save(legacy)
    original = sessions.scenes[False].path.read_bytes()
    info = await sessions.connect("one.invalid", "fixture", "fixture")
    assert not info["logged_in"] and info["pending_scene_choice"]["legacy_available"]
    assert not transports[-1].writes
    await activate(sessions, "use_saved")
    assert sessions.engine.brightness == 37
    sessions.engine.set_brightness(42)
    await sessions.disconnect()
    assert sessions.scenes[False].path.read_bytes() == original
    assert (await sessions.connect("one.invalid", "fixture", "fixture"))["connected"]
    assert sessions.engine.brightness == 42
    old = sessions.engine
    info = await sessions.connect("two.invalid", "fixture", "fixture")
    assert info["logged_in"] and info["pending_scene_choice"] and sessions.engine is old
    assert not transports[-1].writes
    pending = info["pending_scene_choice"]
    info = await sessions.choose_scene(pending["connection_id"], "cancel", "")
    assert info["pending_scene_choice"] is None and sessions.engine is old
    assert not transports[-1].connected
    await sessions.close()


@pytest.mark.anyio
async def test_scene_choice_rejects_stale_token_revision_and_changed_peer(config):
    transport = MemoryDevice()
    sessions = SessionManager(config, transport_factory=lambda **kw: transport)
    sessions.scenes[False].save(Engine(EtherlightSwitch(SimulatedTransport())).snapshot())
    await sessions.connect("fixture.invalid", "fixture", "fixture")
    pending = sessions.info()["pending_scene_choice"]
    with pytest.raises(LoginError) as exc:
        await sessions.choose_scene("wrong", "use_saved", pending["legacy_revision"])
    assert exc.value.code == "scene_choice_expired"
    sessions.scenes[False].save({"brightness": 18})
    with pytest.raises(LoginError) as exc:
        await sessions.choose_scene(pending["connection_id"], "use_saved", pending["legacy_revision"])
    assert exc.value.code == "scene_choice_stale"
    transport.host_key_fingerprint = PEER + "changed"
    with pytest.raises(LoginError) as exc:
        await sessions.choose_scene(pending["connection_id"], "start_new", "")
    assert exc.value.code == "scene_choice_expired"
    assert sessions.engine is None and not transport.writes and not transport.connected


@pytest.mark.anyio
async def test_expired_and_cancelled_candidate_closes_without_writes(config, monkeypatch):
    transport = MemoryDevice()
    sessions = SessionManager(config, transport_factory=lambda **kw: transport)
    monkeypatch.setattr(session_module, "SCENE_CHOICE_TIMEOUT", 0.001)
    await sessions.connect("fixture.invalid", "fixture", "fixture")
    expiry = sessions._pending_expiry
    await expiry
    assert sessions.info()["error"]["code"] == "scene_choice_expired"
    assert not transport.connected and not transport.writes and sessions._pending is None
    assert not list(sessions.output_journal.directory.glob("*"))


@pytest.mark.anyio
async def test_scene_save_failure_leaves_read_only_candidate(config, monkeypatch):
    transport = MemoryDevice()
    sessions = SessionManager(config, transport_factory=lambda **kw: transport)
    await sessions.connect("fixture.invalid", "fixture", "fixture")
    def failed(scene):
        raise OSError("fixture disk full")
    monkeypatch.setattr(sessions._pending.store, "save", failed)
    with pytest.raises(LoginError) as exc:
        await activate(sessions)
    assert exc.value.code == "scene_save_failed" and not transport.writes
    assert sessions.engine is None and sessions._pending is not None
    await sessions.close()
    assert not transport.connected


@pytest.mark.anyio
async def test_failed_shutdown_never_clears_output_record(config, monkeypatch):
    sessions = SessionManager(config, transport_factory=lambda **kw: MemoryDevice())
    await sessions.connect("fixture.invalid", "fixture", "fixture")
    await activate(sessions)
    lease = sessions.engine.switch.output_lease
    async def unconfirmed():
        raise TransportError("fixture cleanup acknowledgment missing")
    monkeypatch.setattr(sessions.engine, "wait_for_output_idle", unconfirmed)
    with pytest.raises(LoginError) as exc:
        await sessions.disconnect()
    assert exc.value.code == "output_unconfirmed"
    assert all(path.exists() for path in lease.paths)
    with pytest.raises(LoginError) as exc:
        await sessions.connect("fixture.invalid", "fixture", "fixture")
    assert exc.value.code == "output_recovery_required"


@pytest.mark.anyio
async def test_failed_scene_copy_then_start_new_uses_defaults(config, monkeypatch):
    sessions = SessionManager(config, transport_factory=lambda **kw: MemoryDevice())
    sessions.scenes[False].save(Engine(EtherlightSwitch(SimulatedTransport()), brightness=19).snapshot())
    await sessions.connect("fixture.invalid", "fixture", "fixture")
    store = sessions._pending.store
    save = store.save
    def failed(scene):
        raise OSError("fixture disk full")
    monkeypatch.setattr(store, "save", failed)
    with pytest.raises(LoginError):
        await activate(sessions, "use_saved")
    monkeypatch.setattr(store, "save", save)
    await activate(sessions, "start_new")
    assert sessions.engine.brightness == config.engine.brightness
    await sessions.close()


@pytest.mark.anyio
async def test_changed_peer_at_same_address_cannot_reuse_scene_or_browser_drafts(config):
    devices = iter([MemoryDevice(), MemoryDevice(PEER + "replacement")])
    sessions = SessionManager(config, transport_factory=lambda **kw: next(devices))
    await sessions.connect("fixture.invalid", "fixture", "fixture")
    old = await activate(sessions)
    sessions.engine.set_brightness(17)
    await sessions.disconnect()
    pending = await sessions.connect("fixture.invalid", "fixture", "fixture")
    assert pending["pending_scene_choice"] and not pending["logged_in"]
    new = await activate(sessions)
    assert new["workspace_id"] != old["workspace_id"]
    assert sessions.engine.brightness == config.engine.brightness
    await sessions.close()


def test_scene_choice_api_is_read_only_until_explicit_activation(config):
    transport = MemoryDevice()
    app = create_app(config, autoconnect=False, transport_factory=lambda **kw: transport)
    with TestClient(app, base_url="http://localhost") as client:
        response = client.post("/api/session/login", json={"host": "fixture.invalid", "username": "fixture",
                                                         "password": "fixture"})
        assert response.status_code == 200
        pending = response.json()["pending_scene_choice"]
        assert not response.json()["logged_in"] and not transport.writes
        assert client.get("/api/state").status_code == 409
        body = {"connection_id": pending["connection_id"], "choice": "start_new",
                "legacy_revision": pending["legacy_revision"]}
        assert client.post("/api/session/scene-choice", json=body,
                           headers={"Origin": "https://other.invalid"}).status_code == 403
        assert not transport.writes
        accepted = client.post("/api/session/scene-choice", json=body)
        assert accepted.status_code == 200 and accepted.json()["logged_in"]
        assert accepted.json()["pending_scene_choice"] is None
        assert client.post("/api/session/scene-choice", json=body).status_code == 409


@pytest.mark.parametrize("demo", [False, True])
def test_server_owner_lock_precedes_app_creation(config, monkeypatch, demo):
    import uvicorn

    from etherlighting_evolved import api
    @contextmanager
    def occupied():
        raise AlreadyRunningError("another controller owns output")
        yield
    monkeypatch.setattr(cli, "AppInstanceLock", occupied)
    monkeypatch.setattr(cli, "load_config", lambda path: config)
    monkeypatch.setattr(api, "create_app", lambda *a, **kw: pytest.fail("must not open another server"))
    monkeypatch.setattr(uvicorn, "run", lambda *a, **kw: pytest.fail("must not open another listener"))
    args = argparse.Namespace(config=None, host=None, port=None, log_level="error", demo=demo, no_autoconnect=True)
    with pytest.raises(AlreadyRunningError):
        cli._serve(args)
