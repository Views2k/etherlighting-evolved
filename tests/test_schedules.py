import json
from datetime import datetime

import pytest

from etherlighting_evolved.presets import PresetStore
from etherlighting_evolved.schedules import Schedule, Scheduler, ScheduleStore, ScheduleStoreError

WEDNESDAY_1900 = datetime(2026, 6, 3, 19, 0, 20)


def test_due_and_next_run():
    s = Schedule.parse({"name": "Evening", "at": "19:00", "days": ["mon", "tue", "wed", "thu", "fri"],
                        "action": {"type": "brightness", "value": 30}})
    assert s.due(WEDNESDAY_1900)
    assert not s.due(datetime(2026, 6, 6, 19, 0))  # Saturday
    assert s.next_run(datetime(2026, 6, 5, 20, 0)) == datetime(2026, 6, 8, 19, 0)  # Friday night -> Monday


@pytest.mark.parametrize("bad", [
    {"at": "19:00", "action": {"type": "dance"}},
    {"at": "7pm", "action": {"type": "release"}},
    {"at": "19:00", "action": {"type": "preset"}},
    {"at": "19:00", "action": {"type": "brightness", "value": 300}},
    {"at": "19:00", "days": ["someday"], "action": {"type": "release"}},
])
def test_bad_schedules(bad):
    with pytest.raises(ValueError):
        Schedule.parse(bad)


class FakeEngine:
    def __init__(self):
        self.calls = []
        self._sfp_revisions = {}

    def apply_snapshot(self, scene):
        self.calls.append(("scene", scene))

    def set_brightness(self, value):
        self.calls.append(("brightness", value))


class FakeSessions:
    def __init__(self):
        self.engine = FakeEngine()

    def schedule_identity(self):
        return "demo"


@pytest.mark.anyio
async def test_scheduler_runs_each_schedule_once_per_minute(tmp_path):
    presets = PresetStore(tmp_path / "presets.json")
    presets.save("Evening", {"version": 2, "zones": []})
    store = ScheduleStore(tmp_path / "schedules.json")
    store.add({"name": "Evening", "at": "19:00", "action": {"type": "preset", "preset": "Evening"},
               "device_binding": "demo"})
    store.add({"name": "Missing", "at": "19:00", "action": {"type": "preset", "preset": "Gone"},
               "device_binding": "demo"})
    sessions = FakeSessions()
    scheduler = Scheduler(store, presets, sessions)

    assert len(await scheduler.tick_confirmed(WEDNESDAY_1900)) == 1
    assert await scheduler.tick_confirmed(WEDNESDAY_1900.replace(second=50)) == []
    assert sessions.engine.calls == [("scene", {"version": 2, "zones": []})]
    missing = next(item for item in scheduler.list() if item["name"] == "Missing")
    assert missing["profile_missing"] and not missing["enabled"]
    presets.save("Gone", {"brightness": 99})
    assert scheduler.list()[1]["profile_missing"]
    assert len(scheduler.history) == 1 and scheduler.history[0]["result"] == "ok"


def test_store_persists(tmp_path):
    store = ScheduleStore(tmp_path / "schedules.json")
    s = store.add({"name": "Lights out", "at": "23:30", "action": {"type": "release"}})
    store.update(s.id, {"at": "23:45"})
    again = ScheduleStore(tmp_path / "schedules.json")
    assert [x["at"] for x in again.list()] == ["23:45"]
    again.delete(s.id)
    assert ScheduleStore(tmp_path / "schedules.json").list() == []


def test_unversioned_schedules_are_read_and_saved_with_a_version(tmp_path):
    path = tmp_path / "schedules.json"
    path.write_text(json.dumps([{"id": "legacy", "name": "Legacy", "at": "07:30", "action": {"type": "release"}}]),
                    "utf-8")
    store = ScheduleStore(path)
    assert [item["id"] for item in store.list()] == ["legacy"]
    store.update("legacy", {"name": "Morning"})
    saved = json.loads(path.read_text("utf-8"))
    assert saved["version"] == 1 and [entry["name"] for entry in saved["schedules"]] == ["Morning"]
    assert ScheduleStore(path).get("legacy").name == "Morning"


@pytest.mark.parametrize("operation", ["list", "add", "update", "delete"])
def test_schedules_from_a_newer_release_are_refused_and_never_overwritten(tmp_path, operation):
    path = tmp_path / "schedules.json"
    content = json.dumps({"version": 2, "schedules": [], "calendars": []}).encode()
    path.write_bytes(content)
    store = ScheduleStore(path)
    data = {"at": "12:00", "action": {"type": "release"}}
    args = {"list": (), "add": (data,), "update": ("old", data), "delete": ("old",)}[operation]
    with pytest.raises(ScheduleStoreError, match="newer version of EE"):
        getattr(store, operation)(*args)
    assert path.read_bytes() == content


@pytest.mark.anyio
async def test_unbound_other_device_and_demo_schedules_do_not_cross_devices(tmp_path):
    store = ScheduleStore(tmp_path / "schedules.json")
    for binding in (None, "a" * 64, "b" * 64, "demo"):
        store.add({"at": "19:00", "action": {"type": "brightness", "value": 40},
                   "device_binding": binding})
    sessions = FakeSessions()
    sessions.schedule_identity = lambda: "a" * 64
    scheduler = Scheduler(store, PresetStore(tmp_path / "presets.json"), sessions)
    assert len(await scheduler.tick_confirmed(WEDNESDAY_1900)) == 1
    assert sessions.engine.calls == [("brightness", 40)]
    assert [row["needs_device_confirmation"] for row in scheduler.list()] == [True, False, True, True]
    assert all(row["next_run"] is None for row in scheduler.list() if row["needs_device_confirmation"])
    scheduler.run(store.items[0])
    assert scheduler.history[0]["result"].startswith("skipped")
    assert sessions.engine.calls == [("brightness", 40)]


def test_failed_schedule_save_preserves_binding_and_enabled_state(tmp_path, monkeypatch):
    store = ScheduleStore(tmp_path / "schedules.json")
    schedule = store.add({"at": "19:00", "action": {"type": "brightness", "value": 40}})
    original = store.path.read_bytes()
    def failed():
        raise OSError("fixture storage failure")
    monkeypatch.setattr(store, "_save", failed)
    with pytest.raises(OSError):
        store.update(schedule.id, {"device_binding": "a" * 64})
    assert store.get(schedule.id).device_binding is None
    with pytest.raises(OSError):
        store.delete(schedule.id)
    assert store.get(schedule.id) is schedule and store.path.read_bytes() == original


def test_api_requires_explicit_schedule_apply_for_current_device(config):
    from fastapi.testclient import TestClient

    from etherlighting_evolved.api import create_app
    app = create_app(config, autoconnect=False)
    schedule = app.state.schedules.add({"at": "19:00", "action": {"type": "brightness", "value": 40}})
    with TestClient(app, base_url="http://localhost") as client:
        path = f"/api/schedules/{schedule.id}"
        assert client.patch(path, json={"enabled": True}).status_code == 409
        client.post("/api/session/login", json={"host": "demo"})
        assert client.get("/api/schedules").json()["schedules"][0]["needs_device_confirmation"]
        assert client.post(path + "/run").status_code == 409
        result = client.patch(path, json={"enabled": True, "device_binding": "a" * 64})
        assert result.status_code == 200 and not result.json()["needs_device_confirmation"]
        assert result.json()["device_binding"] == "demo"
        assert client.post(path + "/run").json()["result"] == "ok"
        client.post("/api/session/logout")
        assert client.post(path + "/run").status_code == 409
    assert ScheduleStore(app.state.schedules.path).get(schedule.id).device_binding == "demo"
