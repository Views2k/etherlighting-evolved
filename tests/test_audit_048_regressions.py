import json
from copy import deepcopy
from datetime import datetime
from types import SimpleNamespace

import httpx
import pytest

from etherlighting_evolved.api import create_app
from etherlighting_evolved.engine import Engine
from etherlighting_evolved.presets import PresetStore, ProfileStoreError, write_json
from etherlighting_evolved.schedules import Scheduler, ScheduleStore
from etherlighting_evolved.switch import EtherlightSwitch
from etherlighting_evolved.transport import SimulatedTransport


@pytest.mark.anyio
@pytest.mark.parametrize("method,path,payload", [
    ("POST", "/api/rules", {"name": "Invalid", "position": "first"}),
    ("POST", "/api/rules", {"name": "Invalid", "position": {}}),
    ("POST", "/api/rules", {"name": "Invalid", "look": [1]}),
    ("PUT", "/api/rules", {"rules": [{"name": "Invalid", "look": [1]}]}),
    ("PUT", "/api/zones", {"zones": [{"ports": [1], "effect": [1]}]}),
    ("PUT", "/api/zones", {"zones": [{"ports": [1], "effect": {"id": "static", "params": ["color"]}}]}),
    ("POST", "/api/rules", {"look": {"rate": 10 ** 400}}),
    ("PUT", "/api/zones", {"zones": [{"ports": [1], "effect": {"params": {"brightness_color": 10 ** 400}}}]}),
    ("POST", "/api/schedules", {"at": "12:00", "action": {"type": "brightness", "value": 10 ** 400}}),
])
async def test_malformed_nested_mutations_are_rejected_without_changing_scene(config, method, path, payload):
    app = create_app(config, autoconnect=False)
    engine = Engine(EtherlightSwitch(SimulatedTransport(latency=0)), rules=[])
    app.state.sessions.engine = engine
    app.state.sessions.schedule_identity = lambda: "demo"
    before = deepcopy(engine.snapshot())
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app, raise_app_exceptions=False),
                                 base_url="http://127.0.0.1:8787") as client:
        response = await client.request(method, path, json=payload)
    assert response.status_code == 400, response.text
    assert response.json()["detail"]["code"] == "invalid"
    assert engine.snapshot() == before


@pytest.mark.anyio
@pytest.mark.parametrize("position", ["1e400", "-1e400"])
async def test_nonfinite_rule_insertion_position_is_rejected(config, position):
    app = create_app(config, autoconnect=False)
    engine = Engine(EtherlightSwitch(SimulatedTransport(latency=0)), rules=[])
    app.state.sessions.engine = engine
    before = deepcopy(engine.snapshot())
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app, raise_app_exceptions=False),
                                 base_url="http://127.0.0.1:8787") as client:
        response = await client.post("/api/rules", content='{"position":' + position + '}',
                                     headers={"Content-Type": "application/json"})
    assert response.status_code == 400, response.text
    assert response.json()["detail"]["code"] == "invalid"
    assert engine.snapshot() == before


@pytest.mark.parametrize("content", [
    b'[{"name":"Existing"},', b'{}', b'[null]', b'\xff',
    b'[{"id":"old","at":"12:00","action":{"type":"release"}},null]',
    b'[{"id":"same","at":"12:00","action":{"type":"release"}},'
    b'{"id":"same","at":"12:00","action":{"type":"release"}}]',
    json.dumps([{"at": "12:00", "action": {"type": "brightness", "value": 10 ** 400}}]).encode(),
])
@pytest.mark.parametrize("operation", ["list", "add", "update", "delete"])
def test_unreadable_saved_schedules_cannot_be_overwritten(tmp_path, content, operation):
    path = tmp_path / "schedules.json"
    path.write_bytes(content)
    store = ScheduleStore(path)
    data = {"name": "New", "at": "12:00", "action": {"type": "brightness", "value": 30}}
    args = {"list": (), "add": (data,), "update": ("old", data), "delete": ("old",)}
    with pytest.raises(OSError):
        getattr(store, operation)(*args[operation])
    assert path.read_bytes() == content
    assert store.items == []


def test_schedules_can_be_recovered_without_restarting_the_workspace(tmp_path):
    path = tmp_path / "schedules.json"
    path.write_text("broken", encoding="utf-8")
    store = ScheduleStore(path)
    with pytest.raises(OSError):
        store.list()
    write_json(path, [{"id": "restored", "name": "Restored", "at": "12:00",
                       "action": {"type": "brightness", "value": 30}}])
    assert store.list()[0]["id"] == "restored"
    store.update("restored", {"name": "Updated"})
    assert ScheduleStore(path).get("restored").name == "Updated"


@pytest.mark.anyio
async def test_schedule_storage_failure_reaches_api_and_preserves_other_routes(config):
    config.data_dir.mkdir(parents=True)
    path = config.data_dir / "schedules.json"
    content = b'[{"PRIVATE_SCHEDULE_MARKER":'
    path.write_bytes(content)
    app = create_app(config, autoconnect=False)
    app.state.sessions.schedule_identity = lambda: "demo"
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app, raise_app_exceptions=False),
                                 base_url="http://127.0.0.1:8787") as client:
        for method, route, payload in [
            ("GET", "/api/schedules", None),
            ("POST", "/api/schedules", {"at": "12:00", "action": {"type": "release"}}),
            ("PATCH", "/api/schedules/old", {"enabled": False}),
            ("DELETE", "/api/schedules/old", None),
        ]:
            response = await client.request(method, route, json=payload)
            assert response.status_code == 409
            assert response.json()["detail"]["code"] == "schedule_storage_unavailable"
            assert "PRIVATE_SCHEDULE_MARKER" not in response.text
        assert (await client.get("/api/health")).status_code == 200
    assert path.read_bytes() == content


def test_unwritable_schedule_storage_preserves_saved_and_memory_state(tmp_path, monkeypatch):
    store = ScheduleStore(tmp_path / "schedules.json")
    schedule = store.add({"at": "12:00", "action": {"type": "brightness", "value": 30}})
    original = store.path.read_bytes()
    def cannot_write(*_):
        raise PermissionError("PRIVATE_STORAGE_MARKER")
    monkeypatch.setattr("etherlighting_evolved.schedules.write_json", cannot_write)
    for operation, args in [("add", ({"at": "13:00", "action": {"type": "release"}},)),
                            ("update", (schedule.id, {"enabled": False})), ("delete", (schedule.id,))]:
        with pytest.raises(OSError) as error:
            getattr(store, operation)(*args)
        assert "PRIVATE_STORAGE_MARKER" not in str(error.value)
        assert store.items == [schedule]
        assert store.path.read_bytes() == original


@pytest.mark.anyio
async def test_failed_profile_schedule_records_failure_and_does_not_skip_other_due_actions(tmp_path, monkeypatch):
    store = ScheduleStore(tmp_path / "schedules.json")
    for name, action in [("Broken profile", {"type": "preset", "preset": "Evening"}),
                         ("Brightness", {"type": "brightness", "value": 20})]:
        store.add({"name": name, "at": "12:00", "action": action, "device_binding": "demo"})
    presets = PresetStore(tmp_path / "presets.json")
    presets.save("Evening", {"brightness": 23})
    row = presets.record(name="Evening")
    store.update(store.items[0].id, {"action": {"type": "preset", "profile_id": row["id"]}})
    def failed(_):
        raise ProfileStoreError("The saved profile could not be read.")
    monkeypatch.setattr(presets, "get_id", failed)
    class Session:
        def __init__(self):
            self.engine = self
            self.values = []
            self._sfp_revisions = {}

        def schedule_identity(self):
            return "demo"

        def set_brightness(self, value):
            self.values.append(value)

        def apply_snapshot(self, scene):
            raise AssertionError("A profile with unreadable storage must not be applied")
    session = Session()
    scheduler = Scheduler(store, presets, session)
    assert len(await scheduler.tick_confirmed(datetime(2026, 10, 31, 12, 0))) == 2
    assert session.values == [20]
    assert scheduler.history[0]["result"] == "ok"
    assert scheduler.history[1]["result"].startswith("failed:")


@pytest.mark.anyio
async def test_failed_resume_schedule_records_failure_and_continues_due_actions(tmp_path):
    store = ScheduleStore(tmp_path / "schedules.json")
    for action in ({"type": "resume"}, {"type": "brightness", "value": 20}):
        store.add({"at": "12:00", "action": action, "device_binding": "demo"})
    engine = Engine(EtherlightSwitch(SimulatedTransport(latency=0)), rules=[])
    engine.connected = False
    engine._reconnect_blocked = True
    sessions = SimpleNamespace(engine=engine, schedule_identity=lambda: "demo")
    scheduler = Scheduler(store, PresetStore(tmp_path / "presets.json"), sessions)
    assert len(await scheduler.tick_confirmed(datetime(2026, 10, 31, 12, 0))) == 2
    assert engine.brightness == 20
    assert scheduler.history[0]["result"] == "ok"
    assert scheduler.history[1]["result"].startswith("failed:")
