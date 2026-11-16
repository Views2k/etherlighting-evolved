"""Fan settings follow the switch: one saved fan mode per switch, and Handoff to UniFi returns the fans."""
import asyncio
import json
from dataclasses import asdict
from types import SimpleNamespace

import pytest
from fastapi.testclient import TestClient
from test_fans import CountingFans
from test_handoff import MemorySwitch

from etherlighting_evolved import session as session_module
from etherlighting_evolved.api import create_app
from etherlighting_evolved.fans import SAVE_FAILED, FanSettings, FanStore
from etherlighting_evolved.presets import DeviceSceneStore
from etherlighting_evolved.session import LoginError, SessionManager
from etherlighting_evolved.transport import TransportError, UnreachableError

pytestmark = pytest.mark.anyio

CURVE_HIGH = FanSettings(mode="curve", curve="high")
CURVE_MEDIUM = FanSettings(mode="curve", curve="medium")


@pytest.fixture
def switches(monkeypatch):
    switches = {"a.invalid": MemorySwitch(), "b.invalid": MemorySwitch()}
    switches["a.invalid"].host_key_fingerprint = "SHA256:fan-fixture-switch-a"
    switches["b.invalid"].host_key_fingerprint = "SHA256:fan-fixture-switch-b"
    for switch in switches.values():
        switch.fans = CountingFans()
    monkeypatch.setattr(session_module, "SwitchFans", lambda transport: transport.fans)
    return switches


@pytest.fixture
async def sessions(config, switches):
    sessions = SessionManager(config, transport_factory=lambda **kw: switches[kw["host"]])
    yield sessions
    await sessions.close()


async def connect(sessions, host):
    info = await sessions.connect(host, "fixture", "fixture")
    if pending := info["pending_scene_choice"]:
        info = await sessions.choose_scene(pending["connection_id"], "start_new", pending["legacy_revision"])
    return info


def fan_files(config):
    folder = config.data_dir / "device-fans"
    return sorted(folder.iterdir()) if folder.exists() else []


def saved_fans(sessions):
    return json.loads(sessions.engine.fans.store.path.read_text("utf-8"))


async def disconnected(engine, switch, monkeypatch):
    """The switch connection is lost and reconnecting fails."""
    async def unreachable():
        raise UnreachableError("fixture: the switch cannot be reached")

    monkeypatch.setattr(switch, "connect", unreachable)
    engine.connected = False
    await asyncio.sleep(0)


async def test_fan_mode_is_saved_per_switch(sessions, switches, config):
    sim_a, sim_b = switches["a.invalid"].fans, switches["b.invalid"].fans
    await connect(sessions, "a.invalid")
    await sessions.engine.update_fans(asdict(CURVE_HIGH))
    await sessions.disconnect()
    await connect(sessions, "b.invalid")
    assert sessions.engine.fans.settings == FanSettings() and sim_b.line is None
    await sessions.disconnect()
    await connect(sessions, "a.invalid")
    assert sessions.engine.fans.settings == CURVE_HIGH
    sim_a.applied = 0
    await sessions.engine.fans.poll()
    assert sim_a.applied == 0 and sim_a.line == CURVE_HIGH.config_line()
    assert fan_files(config) == [sessions.engine.fans.store.path]


class ReleasedFans(CountingFans):
    """A controller started by EE 0.5.1, which runs that release's fans.sh until EE installs its own."""

    current = False

    async def read(self):
        return {**await super().read(), "script_current": self.current}

    async def apply(self, line, owner=None):
        self.current = True
        return await super().apply(line, owner)


async def test_legacy_fans_json_carries_over_and_restarts_fan_control_once(sessions, switches, config):
    legacy = write_legacy(config, CURVE_MEDIUM)
    sim_a = switches["a.invalid"].fans = ReleasedFans()
    await sim_a.apply(CURVE_MEDIUM.config_line())
    sim_a.applied, sim_a.current = 0, False
    await connect(sessions, "a.invalid")
    saved = saved_fans(sessions)
    assert legacy.exists() and saved["settings"] == asdict(CURVE_MEDIUM) and len(saved["owner"]) == 48
    # fans.sh changed after 0.5.1, so the first check replaces the running controller with this version's, with the
    # same setting and the new owner line. Later checks leave it running.
    for _ in range(2):
        await sessions.engine.fans.poll()
    assert (sim_a.applied, sim_a.stopped) == (1, 0) and sim_a.line == CURVE_MEDIUM.config_line()
    assert sim_a.owner == saved["owner"] and sessions.engine.fans.to_dict()["hold"] is None
    await sessions.disconnect()
    await connect(sessions, "b.invalid")
    assert sessions.engine.fans.settings == FanSettings()


def write_legacy(config, settings):
    config.data_dir.mkdir(parents=True, exist_ok=True)
    legacy = config.data_dir / "fans.json"
    legacy.write_text(json.dumps(asdict(settings)), "utf-8")
    return legacy


async def test_switch_without_fan_control_does_not_adopt(config):
    legacy = write_legacy(config, CURVE_MEDIUM)
    store = DeviceSceneStore(config.data_dir / "device-scenes" / f"{'0' * 64}.json", "0" * 64)
    control = SessionManager(config)._fan_control(SimpleNamespace(hardware_profile=None, transport=object()), False,
                                                  store)
    assert not control.to_dict()["supported"]
    assert legacy.exists() and not (config.data_dir / "device-fans").exists()


async def test_demo_keeps_fans_demo_json(config):
    legacy = write_legacy(config, CURVE_MEDIUM)
    sessions = SessionManager(config)
    try:
        await sessions.connect("demo")
        await sessions.engine.update_fans({"mode": "manual", "manual_percent": 50})
        assert isinstance(sessions.engine.fans.store, FanStore)
        assert sessions.engine.fans.store.path == config.data_dir / "fans-demo.json"
    finally:
        await sessions.close()
    assert json.loads(legacy.read_text("utf-8")) == asdict(CURVE_MEDIUM)
    assert not (config.data_dir / "device-fans").exists()


async def test_handoff_returns_fans_and_reconnect_restores_mode(sessions, switches):
    sim_a = switches["a.invalid"].fans
    await connect(sessions, "a.invalid")
    await sessions.engine.update_fans(asdict(CURVE_HIGH))
    path = sessions.engine.fans.store.path
    owner = saved_fans(sessions)["owner"]
    info = await sessions.handoff()
    assert info["handed_off"] and info["fan_handoff"] == "returned" and sim_a.line is None
    assert json.loads(path.read_text("utf-8"))["settings"] == asdict(CURVE_HIGH)
    info = await connect(sessions, "a.invalid")
    assert info["fan_handoff"] is None
    await sessions.engine.fans.poll()
    assert sim_a.line == CURVE_HIGH.config_line() and sim_a.owner == owner


async def test_handoff_completes_when_fans_are_unconfirmed(sessions, switches, monkeypatch):
    sim_a = switches["a.invalid"].fans
    await connect(sessions, "a.invalid")
    await sessions.engine.update_fans(asdict(CURVE_HIGH))

    async def refused():
        raise TransportError("fixture: the switch did not confirm")

    monkeypatch.setattr(sim_a, "stop", refused)
    info = await sessions.handoff()
    assert info["handed_off"] and info["fan_handoff"] == "unconfirmed"
    assert sim_a.line == CURVE_HIGH.config_line()


async def test_handoff_while_disconnected_reports_unconfirmed(sessions, switches, monkeypatch):
    sim_a = switches["a.invalid"].fans
    await connect(sessions, "a.invalid")
    await sessions.engine.update_fans(asdict(CURVE_HIGH))
    await disconnected(sessions.engine, switches["a.invalid"], monkeypatch)
    sim_a.reads = sim_a.applied = sim_a.stopped = 0
    info = await sessions.handoff()
    assert info["fan_handoff"] == "unconfirmed"
    assert (sim_a.reads, sim_a.applied, sim_a.stopped) == (0, 0, 0)


async def test_handoff_while_disconnected_in_switch_control_reports_nothing(sessions, switches, monkeypatch):
    await connect(sessions, "a.invalid")
    await sessions.engine.fans.poll()
    assert sessions.engine.fans.reading["controller"] == "absent"
    await disconnected(sessions.engine, switches["a.invalid"], monkeypatch)
    assert (await sessions.handoff())["fan_handoff"] is None


async def test_handoff_retry_keeps_the_fan_result(sessions, switches):
    await connect(sessions, "a.invalid")
    await sessions.engine.update_fans({"mode": "curve"})
    switches["a.invalid"].close_failures = 1
    with pytest.raises(LoginError) as failed:
        await sessions.handoff()
    assert failed.value.code == "handoff_unconfirmed" and sessions.info()["fan_handoff"] == "returned"
    info = await sessions.handoff()
    assert info["handed_off"] and info["fan_handoff"] == "returned"


async def test_a_new_handoff_does_not_repeat_an_earlier_fan_result(sessions):
    await connect(sessions, "a.invalid")
    await sessions.engine.update_fans({"mode": "curve"})
    assert (await sessions.handoff())["fan_handoff"] == "returned"
    pending = (await sessions.connect("b.invalid", "fixture", "fixture"))["pending_scene_choice"]
    await sessions.choose_scene(pending["connection_id"], "cancel", pending["legacy_revision"])
    assert (await sessions.handoff())["fan_handoff"] is None


async def test_owner_token_never_serialized(sessions):
    await connect(sessions, "a.invalid")
    mailbox = sessions.listen()
    engine = sessions.engine
    await engine.update_fans({"mode": "curve"})
    engine.set_brightness(40)
    owner = engine.fans.store.owner
    messages = {}
    while not {"fans", "state"} <= messages.keys():
        message = await asyncio.wait_for(mailbox.get(), 5)
        messages[message["type"]] = message
    for value in (engine.fans.to_dict(), engine.state(), messages["fans"], messages["state"], sessions.info()):
        assert owner not in json.dumps(value)
    sessions.unlisten(mailbox)


def test_api_reports_hold_save_failure_and_fan_handoff(config, monkeypatch):
    with TestClient(create_app(config, autoconnect=False), base_url="http://127.0.0.1:8787") as client:
        assert client.post("/api/session/login", json={"host": "demo"}).status_code == 200
        assert client.put("/api/fans", json={"mode": "manual", "manual_percent": 40}).status_code == 200
        fans = client.get("/api/fans").json()
        assert fans["hold"] is None and "owner" not in fans["reading"]

        def cannot_write(self, settings):
            raise OSError("fixture: the disk is full")

        with monkeypatch.context() as patch:
            patch.setattr(FanStore, "save", cannot_write)
            response = client.put("/api/fans", json={"mode": "manual", "manual_percent": 50})
        assert response.status_code == 500
        assert response.json() == {"detail": {"code": "fan_settings_save_failed", "message": SAVE_FAILED}}
        fans = client.get("/api/fans").json()
        assert fans["error"] == SAVE_FAILED and fans["settings"]["manual_percent"] == 50
        assert client.put("/api/fans", json={"mode": "switch"}).json()["error"] is None
        response = client.post("/api/session/handoff")
        assert response.status_code == 200 and "fan_handoff" in response.json()
        assert response.json()["fan_handoff"] is None
