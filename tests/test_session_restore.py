import asyncio
import json
import re

import pytest

from etherlighting_evolved.credentials import CredentialStoreError
from etherlighting_evolved.session import LoginError, SessionManager
from etherlighting_evolved.transport import SimulatedTransport

FIXTURE = {"host": "switch.invalid", "port": 22, "username": "fixture", "password": "fixture-only"}


class LocalTransport:
    """Exercise real-session storage classification with local simulator operations."""

    def __init__(self, gate=None):
        self.host_key_fingerprint = "SHA256:local-fixture-authenticated-peer"
        self.simulator = SimulatedTransport(latency=0)
        self.gate = gate
        self.connections = 0
        self.entered = asyncio.Event()

    def __getattr__(self, key):
        return getattr(self.simulator, key)

    async def connect(self):
        self.connections += 1
        self.entered.set()
        if self.gate is not None:
            await self.gate.wait()
        await self.simulator.connect()


@pytest.mark.anyio
async def test_saved_restore_coalesces_and_preserves_saved_record(config, monkeypatch):
    gate = asyncio.Event()
    transport = LocalTransport(gate)
    sessions = SessionManager(config, transport_factory=lambda **kwargs: transport)
    monkeypatch.setattr(sessions.saved, "load", lambda: FIXTURE.copy())
    monkeypatch.setattr(sessions.saved, "save", lambda *args: pytest.fail("remembered login rewritten"))
    monkeypatch.setattr(sessions.saved, "clear", lambda: pytest.fail("remembered login cleared"))
    try:
        first = asyncio.create_task(sessions.restore_saved())
        await transport.entered.wait()
        second = asyncio.create_task(sessions.restore_saved())
        await asyncio.sleep(0)
        assert sessions.info()["connecting"]
        gate.set()
        a, b = await asyncio.gather(first, second)
        assert a["pending_scene_choice"] == b["pending_scene_choice"] and transport.connections == 1
        pending = a["pending_scene_choice"]
        a = await sessions.choose_scene(pending["connection_id"], "start_new", pending["legacy_revision"])
        assert a["logged_in"]
        assert not a["connecting"] and not b["connecting"]
        assert not sessions.info()["connecting"]
        assert FIXTURE["password"] not in json.dumps(a)
        assert (await sessions.restore_saved())["logged_in"]
        assert transport.connections == 1
    finally:
        await sessions.close()


@pytest.mark.anyio
async def test_missing_and_unavailable_saved_login_never_connect(config, monkeypatch):
    sessions = SessionManager(config, transport_factory=lambda **kwargs: pytest.fail("unexpected connection"))
    with pytest.raises(LoginError) as error:
        await sessions.restore_saved()
    assert error.value.code == "no_saved_login"
    assert not sessions.info()["connecting"]

    def unavailable():
        raise CredentialStoreError("protected login unavailable")

    monkeypatch.setattr(sessions.saved, "load", unavailable)
    with pytest.raises(LoginError) as error:
        await sessions.restore_saved()
    assert error.value.code == "saved_login_unavailable"
    assert not sessions.info()["connecting"]


@pytest.mark.anyio
async def test_session_info_reports_the_saved_login_without_reading_it_again(config, monkeypatch):
    sessions = SessionManager(config, transport_factory=lambda **kwargs: LocalTransport())
    reads = []
    monkeypatch.setattr(sessions.saved, "load", lambda: reads.append(True) or FIXTURE.copy())
    try:
        await sessions.restore_saved()
        for _ in range(5):
            info = sessions.info()
        assert len(reads) == 1
        assert info["remembered"] == {"host": FIXTURE["host"], "port": 22, "username": FIXTURE["username"]}
        assert FIXTURE["password"] not in json.dumps(info)
    finally:
        await sessions.close()


@pytest.mark.anyio
async def test_canceling_one_restore_caller_keeps_shared_attempt(config, monkeypatch):
    gate = asyncio.Event()
    transport = LocalTransport(gate)
    sessions = SessionManager(config, transport_factory=lambda **kwargs: transport)
    monkeypatch.setattr(sessions.saved, "load", lambda: FIXTURE.copy())
    try:
        first = asyncio.create_task(sessions.restore_saved())
        await transport.entered.wait()
        first.cancel()
        with pytest.raises(asyncio.CancelledError):
            await first
        second = asyncio.create_task(sessions.restore_saved())
        gate.set()
        assert (await second)["pending_scene_choice"]
        assert transport.connections == 1
    finally:
        await sessions.close()


@pytest.mark.anyio
async def test_shutdown_cancels_pending_restore_before_any_engine_starts(config, monkeypatch):
    transport = LocalTransport(asyncio.Event())
    sessions = SessionManager(config, transport_factory=lambda **kwargs: transport)
    monkeypatch.setattr(sessions.saved, "load", lambda: FIXTURE.copy())
    pending = asyncio.create_task(sessions.restore_saved())
    await transport.entered.wait()
    await sessions.close()
    with pytest.raises(asyncio.CancelledError):
        await pending
    assert sessions.engine is None
    assert not sessions.info()["connecting"]
    assert not transport.connected


@pytest.mark.anyio
async def test_live_restore_uses_real_scene_and_stable_private_workspace_scope(config, monkeypatch):
    sessions = SessionManager(config, transport_factory=lambda **kwargs: LocalTransport())
    monkeypatch.setattr(sessions.saved, "load", lambda: FIXTURE.copy())
    try:
        first = await sessions.restore_saved()
        pending = first["pending_scene_choice"]
        first = await sessions.choose_scene(pending["connection_id"], "start_new", pending["legacy_revision"])
        sessions.engine.set_brightness(31)
        demo = sessions.engine.snapshot()
        demo["brightness"] = 88
        sessions.scenes[True].save(demo)
        await sessions.disconnect()
        assert sessions.info()["workspace_id"] is None
        second = await sessions.restore_saved()
        assert sessions.engine.brightness == 31
        assert sessions.scenes[True].load()["brightness"] == 88
        assert first["workspace_id"] == second["workspace_id"]
        assert re.fullmatch(r"[0-9a-f]{64}", second["workspace_id"])
        assert FIXTURE["host"] not in second["workspace_id"]
        assert FIXTURE["username"] not in second["workspace_id"]
        sessions.username = "different-fixture"
        assert sessions.workspace_id() != second["workspace_id"]
        sessions.simulated = True
        assert sessions.workspace_id() != second["workspace_id"]
    finally:
        sessions.simulated = False
        await sessions.close()
