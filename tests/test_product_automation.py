import json
from datetime import datetime
from types import SimpleNamespace

import pytest
from fastapi.testclient import TestClient

from etherlighting_evolved.api import create_app
from etherlighting_evolved.presets import PresetStore, ProfileConflict
from etherlighting_evolved.schedules import Schedule, Scheduler, ScheduleStore, ScheduleStoreError
from etherlighting_evolved.transport import TransportError

NOW = datetime(2026, 11, 4, 18, 0, 10)


def scheduler_fixture(tmp_path):
    profiles = PresetStore(tmp_path / "profiles.json")
    store = ScheduleStore(tmp_path / "schedules.json")
    calls = []

    async def confirm_sfp_output(expected):
        return None

    engine = SimpleNamespace(
        set_brightness=lambda value: calls.append(value), apply_snapshot=lambda scene: calls.append(scene),
        sfp_revisions=lambda: {}, confirm_sfp_output=confirm_sfp_output,
    )
    sessions = SimpleNamespace(engine=engine, schedule_identity=lambda: "demo")
    return profiles, store, calls, sessions


@pytest.mark.anyio
async def test_profile_migration_identity_survives_rename_and_name_reuse(tmp_path):
    profiles, store, calls, sessions = scheduler_fixture(tmp_path)
    profiles.path.write_text(json.dumps({"Evening": {"brightness": 23}}), encoding="utf-8")
    schedule = store.add({"at": "18:00", "device_binding": "demo", "action": {"type": "preset", "preset": "Evening"}})
    scheduler = Scheduler(store, profiles, sessions)
    original = profiles.record(name="Evening")
    profiles.rename("Evening", "Night")
    profiles.save("Evening", {"brightness": 82}, overwrite=False)
    assert store.get(schedule.id).action["profile_id"] == original["id"]
    await scheduler.tick_confirmed(NOW)
    assert calls == [{"brightness": 23}]
    assert scheduler.list()[0]["action"]["preset"] == "Night"
    assert PresetStore(profiles.path).record(name="Night")["id"] == original["id"]


def test_stale_profile_replacement_and_removed_identity_never_recreate(tmp_path):
    profiles, _, _, _ = scheduler_fixture(tmp_path)
    profiles.save("Evening", {"brightness": 23})
    original = profiles.record(name="Evening")
    renamed = profiles.change(original["id"], original["revision"], name="Night")
    with pytest.raises(ProfileConflict):
        profiles.change(original["id"], original["revision"], scene={"brightness": 99})
    profiles.change(renamed["id"], renamed["revision"], delete=True)
    profiles.save("Evening", {"brightness": 82})
    with pytest.raises(ProfileConflict):
        profiles.change(original["id"], original["revision"], scene={"brightness": 99})
    assert profiles.get("Evening") == {"brightness": 82}


@pytest.mark.parametrize("days", [[], None, "mon"])
def test_explicit_invalid_days_are_not_defaulted(days):
    with pytest.raises(ValueError):
        Schedule.parse({"at": "18:00", "days": days, "action": {"type": "release"}})
    assert len(Schedule.parse({"at": "18:00", "action": {"type": "release"}}).days) == 7


@pytest.mark.anyio
async def test_occurrence_and_history_survive_restart_and_dst_fold(tmp_path):
    profiles, store, calls, sessions = scheduler_fixture(tmp_path)
    item = store.add({"at": "18:00", "device_binding": "demo", "action": {"type": "brightness", "value": 23}})
    scheduler = Scheduler(store, profiles, sessions)
    assert await scheduler.tick_confirmed(NOW) == [item.id]
    again = Scheduler(ScheduleStore(store.path), profiles, sessions)
    assert await again.tick_confirmed(NOW.replace(second=50, fold=1)) == []
    assert calls == [23] and again.history[0]["result"] == "ok"
    again.run(again.store.get(item.id), NOW)
    assert calls == [23, 23] and again.history[0]["trigger"] == "manual"
    assert await again.tick_confirmed(NOW) == []


@pytest.mark.anyio
async def test_claim_failure_prevents_output_and_pending_restart_never_repeats(tmp_path, monkeypatch):
    profiles, store, calls, sessions = scheduler_fixture(tmp_path)
    store.add({"at": "18:00", "device_binding": "demo", "action": {"type": "brightness", "value": 23}})
    scheduler = Scheduler(store, profiles, sessions)
    original = scheduler._save_journal
    monkeypatch.setattr(scheduler, "_save_journal", lambda: (_ for _ in ()).throw(ScheduleStoreError("fixture")))
    with pytest.raises(ScheduleStoreError):
        await scheduler.tick_confirmed(NOW)
    assert calls == [] and scheduler._fired == {}
    saves = 0

    def fail_final():
        nonlocal saves
        saves += 1
        if saves == 2:
            raise ScheduleStoreError("fixture")
        original()

    monkeypatch.setattr(scheduler, "_save_journal", fail_final)
    with pytest.raises(ScheduleStoreError):
        await scheduler.tick_confirmed(NOW)
    again = Scheduler(ScheduleStore(store.path), profiles, sessions)
    assert await again.tick_confirmed(NOW.replace(second=40)) == [] and calls == [23]
    assert again.history[0]["result"].startswith("unconfirmed")


@pytest.mark.anyio
async def test_release_failure_records_unconfirmed_result(tmp_path):
    profiles, store, calls, sessions = scheduler_fixture(tmp_path)
    item = store.add({"at": "18:00", "device_binding": "demo", "action": {"type": "release"}})
    sessions.engine.release = lambda: calls.append("release")

    async def failed():
        raise TransportError("Cleanup was not confirmed")

    sessions.engine.wait_for_output_idle = failed
    scheduler = Scheduler(store, profiles, sessions)
    result = await scheduler.run_confirmed(item)
    assert result["result"].startswith("unconfirmed")
    assert Scheduler(store, profiles, sessions).history[0]["result"] == result["result"]


def test_profile_api_revision_guard_dependencies_and_create_only(config):
    app = create_app(config, autoconnect=False)
    with TestClient(app, base_url="http://localhost") as client:
        client.post("/api/session/login", json={"host": "demo"})
        assert client.post("/api/presets", json={"name": "Evening"}).status_code == 201
        record = client.get("/api/profiles").json()["profiles"][0]
        schedule = client.post(
            "/api/schedules", json={"at": "18:00", "action": {"type": "preset", "profile_id": record["id"]}}
        )
        assert schedule.status_code == 201
        path = f"/api/profiles/{record['id']}"
        assert (
            client.patch(
                path, json={"action": "rename", "name": "Night", "expected_revision": record["revision"]}
            ).status_code
            == 200
        )
        assert (
            client.patch(path, json={"action": "replace", "expected_revision": record["revision"]}).status_code == 409
        )
        current = client.get("/api/profiles").json()["profiles"][0]
        assert client.post("/api/presets", json={"name": "Night", "overwrite": True}).status_code == 409
        assert (
            client.patch(path, json={"action": "delete", "expected_revision": current["revision"]}).status_code == 409
        )
        deleted = client.patch(
            path, json={"action": "delete", "expected_revision": current["revision"], "disable_schedules": True}
        )
        assert deleted.status_code == 200 and deleted.json()["profiles"] == []
        assert not client.get("/api/schedules").json()["schedules"][0]["enabled"]
        assert (
            client.patch(path, json={"action": "replace", "expected_revision": current["revision"]}).status_code == 409
        )


def test_rule_tester_uses_priority_and_manual_override_without_writes(config):
    app = create_app(config, autoconnect=False)
    with TestClient(app, base_url="http://localhost") as client:
        client.post("/api/session/login", json={"host": "demo"})
        engine = app.state.sessions.engine
        engine.set_override(1, "#123456")
        before = engine.snapshot()
        rules = [
            {
                "id": name,
                "name": name,
                "conditions": [{"field": "time", "op": "between", "value": ["18:00", "19:00"]}],
                "look": {"color": "#ffffff"},
            }
            for name in ["First", "Second"]
        ]
        result = client.post("/api/rules/test", json={"port": 1, "rules": rules, "at": "2026-11-04T18:30"}).json()
        assert result["winner"]["id"] == "First" and result["source"] == "individual_color"
        assert result["rules"][0]["conditions"][0]["passes"]
        reordered = client.post(
            "/api/rules/test", json={"port": 1, "rules": list(reversed(rules)), "at": "2026-11-04T18:30"}
        ).json()
        assert reordered["winner"]["id"] == "Second"
        outside = client.post("/api/rules/test", json={"port": 1, "rules": rules, "at": "2026-11-04T20:30"}).json()
        assert outside["winner"] is None
        assert engine.snapshot() == before


def test_rule_tester_refuses_rules_that_saving_refuses(config):
    with TestClient(create_app(config, autoconnect=False), base_url="http://localhost") as client:
        client.post("/api/session/login", json={"host": "demo"})
        rule = {"name": "Text ports", "ports": "12", "look": {"color": "#ffffff"}}
        assert client.put("/api/rules", json={"rules": [rule]}).status_code == 400
        tested = client.post("/api/rules/test", json={"port": 1, "rules": [rule]})
        assert tested.status_code == 400 and tested.json()["detail"]["code"] == "invalid"


@pytest.mark.anyio
async def test_schedule_does_not_certify_pending_optical_acknowledgment(tmp_path):
    profiles, store, _, sessions = scheduler_fixture(tmp_path)
    item = store.add({"at": "18:00", "device_binding": "demo", "action": {"type": "brightness", "value": 23}})
    revisions, requested = {9: 1}, []

    def change(_):
        revisions[9] += 1

    async def pending(expected):
        requested.append(expected)
        raise TransportError("Lighting settings were saved, but SFP output is still pending.")

    sessions.engine.set_brightness = change
    sessions.engine.sfp_revisions = lambda: dict(revisions)
    sessions.engine.confirm_sfp_output = pending
    scheduler = Scheduler(store, profiles, sessions)
    result = await scheduler.run_confirmed(item)
    assert requested == [{9: 2}]
    assert result["result"].startswith("unconfirmed")
    assert "pending" in result["result"]
