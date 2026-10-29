from datetime import datetime

import pytest

from etherlighting_evolved.presets import PresetStore
from etherlighting_evolved.schedules import Schedule, Scheduler, ScheduleStore

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

    def apply_snapshot(self, scene):
        self.calls.append(("scene", scene))

    def set_brightness(self, value):
        self.calls.append(("brightness", value))


class FakeSessions:
    def __init__(self):
        self.engine = FakeEngine()


def test_scheduler_runs_each_schedule_once_per_minute(tmp_path):
    presets = PresetStore(tmp_path / "presets.json")
    presets.save("Evening", {"version": 2, "zones": []})
    store = ScheduleStore(tmp_path / "schedules.json")
    store.add({"name": "Evening", "at": "19:00", "action": {"type": "preset", "preset": "Evening"}})
    store.add({"name": "Missing", "at": "19:00", "action": {"type": "preset", "preset": "Gone"}})
    sessions = FakeSessions()
    scheduler = Scheduler(store, presets, sessions)

    assert len(scheduler.tick(WEDNESDAY_1900)) == 2
    assert scheduler.tick(WEDNESDAY_1900.replace(second=50)) == []
    assert sessions.engine.calls == [("scene", {"version": 2, "zones": []})]
    assert scheduler.history[0]["result"].startswith("failed")


def test_store_persists(tmp_path):
    store = ScheduleStore(tmp_path / "schedules.json")
    s = store.add({"name": "Lights out", "at": "23:30", "action": {"type": "release"}})
    store.update(s.id, {"at": "23:45"})
    again = ScheduleStore(tmp_path / "schedules.json")
    assert [x["at"] for x in again.list()] == ["23:45"]
    again.delete(s.id)
    assert ScheduleStore(tmp_path / "schedules.json").list() == []
