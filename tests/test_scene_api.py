import asyncio
from copy import deepcopy

import httpx
import pytest

from etherlighting_evolved.api import create_app
from etherlighting_evolved.engine import Engine
from etherlighting_evolved.routes.context import scene_envelope
from etherlighting_evolved.switch import EtherlightSwitch
from etherlighting_evolved.transport import SimulatedTransport

LOCAL = "http://127.0.0.1:8787"


@pytest.fixture
def scene_engine():
    return Engine(EtherlightSwitch(SimulatedTransport(latency=0)), rules=[])


@pytest.fixture
def scene_app(config, scene_engine):
    app = create_app(config, autoconnect=False)
    app.state.sessions.engine = scene_engine
    return app


@pytest.fixture
async def scene_client(scene_app):
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=scene_app), base_url=LOCAL) as client:
        yield client


@pytest.mark.anyio
async def test_scene_read_stays_stable_while_animation_advances(scene_client, scene_engine):
    before = (await scene_client.get("/api/scene")).json()
    scene_engine.compose(t=3)
    after = (await scene_client.get("/api/scene")).json()
    assert before == after
    assert before["scene"] == scene_engine.snapshot()
    assert len(before["revision"]) == 64
    assert "mode" not in before["scene"]


def test_scene_revision_uses_canonical_key_order():
    scene = {"zones": [{"id": "desk", "effect": {"id": "static", "params": {"color": "#ffffff"}}}],
             "brightness": 50}
    reordered = {"brightness": 50, "zones": [{"effect": {"params": {"color": "#ffffff"}, "id": "static"},
                                              "id": "desk"}]}
    assert scene_envelope(scene)["revision"] == scene_envelope(reordered)["revision"]


def test_scene_revision_normalizes_integral_floats_without_changing_values():
    scene = {"sfp": {"9": {"period": 6.0}}, "levels": [0.0, 50.0, 1.25], "enabled": True}
    browser_scene = {"sfp": {"9": {"period": 6}}, "levels": [0, 50, 1.25], "enabled": True}
    assert scene_envelope(scene)["revision"] == scene_envelope(browser_scene)["revision"]
    assert type(scene["sfp"]["9"]["period"]) is float
    assert scene_envelope({"value": 1.25})["revision"] != scene_envelope({"value": 1})["revision"]
    assert scene_envelope({"value": True})["revision"] != scene_envelope({"value": 1})["revision"]


@pytest.mark.anyio
async def test_restoring_browser_numeric_roundtrip_recovers_original_revision(scene_client, scene_engine):
    captured = (await scene_client.get("/api/scene")).json()
    browser_scene = deepcopy(captured["scene"])
    for settings in browser_scene["sfp"].values():
        settings["period"] = int(settings["period"])
    scene_engine.set_brightness(37)
    current = (await scene_client.get("/api/scene")).json()
    result = await scene_client.put("/api/scene", json={
        "scene": browser_scene, "expected_revision": current["revision"],
    })
    assert result.status_code == 200, result.text
    assert result.json()["revision"] == captured["revision"]
    assert result.json()["scene"] == captured["scene"]


@pytest.mark.anyio
async def test_restore_scene_recovers_settings_without_resuming(scene_client, scene_engine):
    saved = deepcopy(scene_engine.snapshot())
    saved["brightness"] = 43
    saved["overrides"] = {"3": {"color": "#102030", "brightness": 71}}
    saved["sfp"]["9"].update({"mode": "color", "color": "#4da6ff", "effect": "pulse"})
    scene_engine.apply_snapshot(saved)
    captured = (await scene_client.get("/api/scene")).json()
    scene_engine.set_brightness(85)
    scene_engine.set_effect("comet")
    scene_engine.clear_override()
    scene_engine.mode = "released"
    current = (await scene_client.get("/api/scene")).json()

    result = await scene_client.put("/api/scene", json={
        "scene": captured["scene"], "expected_revision": current["revision"],
    })

    assert result.status_code == 200, result.text
    assert result.json() == captured
    assert scene_engine.mode == "released"
    assert scene_engine.snapshot() == captured["scene"]


@pytest.mark.anyio
async def test_conflict_precedes_validation_and_preserves_current_scene(scene_client, scene_engine):
    old = (await scene_client.get("/api/scene")).json()
    scene_engine.set_brightness(21)
    current = scene_engine.snapshot()
    result = await scene_client.put("/api/scene", json={"scene": {}, "expected_revision": old["revision"]})
    assert result.status_code == 409
    assert result.json()["detail"]["code"] == "scene_changed"
    assert scene_engine.snapshot() == current


@pytest.mark.anyio
@pytest.mark.parametrize("changes", [
    {"zones": []},
    {"zones": [None]},
    {"overrides": [1]},
    {"overrides": {"2": {"color": "invalid"}}},
    {"sfp": {"11": {"mode": "color"}}},
])
async def test_invalid_scene_returns_400_without_partial_changes(scene_client, scene_engine, changes):
    current = (await scene_client.get("/api/scene")).json()
    invalid = {**deepcopy(current["scene"]), "brightness": 7, **changes}
    result = await scene_client.put("/api/scene", json={
        "scene": invalid, "expected_revision": current["revision"],
    })
    assert result.status_code == 400, result.text
    assert result.json()["detail"]["code"] == "invalid"
    assert scene_engine.snapshot() == current["scene"]


@pytest.mark.anyio
async def test_competing_restores_cannot_overwrite_the_first(scene_client, scene_engine):
    current = (await scene_client.get("/api/scene")).json()
    payloads = [{"scene": {**current["scene"], "brightness": level},
                 "expected_revision": current["revision"]} for level in (23, 67)]
    responses = await asyncio.gather(*(scene_client.put("/api/scene", json=body) for body in payloads))
    assert sorted(response.status_code for response in responses) == [200, 409]
    winner = next(response.json() for response in responses if response.status_code == 200)
    assert scene_engine.snapshot() == winner["scene"]


@pytest.mark.anyio
async def test_scene_routes_keep_token_and_session_requirements(config):
    config.api.token = "scene-api-test-token"
    app = create_app(config, autoconnect=False)
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url=LOCAL) as client:
        assert (await client.get("/api/scene")).status_code == 401
        headers = {"Authorization": "Bearer scene-api-test-token"}
        assert (await client.get("/api/scene", headers=headers)).status_code == 409
        result = await client.put("/api/scene", headers=headers,
                                  json={"scene": {}, "expected_revision": "stale"})
        assert result.status_code == 409
        assert result.json()["detail"]["code"] == "not_logged_in"


def test_preserved_scene_reuses_unchanged_phase_and_sfp_intent(scene_engine):
    engine = scene_engine
    engine.switch.sfp_ports = (9, 10)
    engine.set_effect("breathing", {"color": "#112233"})
    original = engine.zones[0]
    original.started_at, original.wall_started_at = 13, 17
    engine._t0, engine._animation_origin = 19, 23
    revisions = dict(engine._sfp_revisions)
    scene = engine.snapshot()
    scene["zones"][0]["name"] = "Renamed group"
    scene["overrides"] = {"1": {"color": "#123456", "brightness": 50}}
    assert engine.apply_snapshot(scene, preserve_unchanged=True) == set()
    assert engine.zones[0].effect is original.effect
    assert (engine.zones[0].started_at, engine.zones[0].wall_started_at) == (13, 17)
    assert (engine._t0, engine._animation_origin) == (19, 23)
    assert engine._sfp_revisions == revisions


@pytest.mark.anyio
async def test_atomic_scene_waits_only_for_changed_sfp(scene_client, scene_engine, monkeypatch):
    scene_engine.switch.sfp_ports = (9, 10)
    current = (await scene_client.get("/api/scene")).json()
    scene = deepcopy(current["scene"])
    scene["sfp"]["9"].update({"mode": "color", "color": "#123456"})
    waits = []
    async def confirm(port):
        waits.append(port)
        scene_engine._sfp_acknowledged[port] = scene_engine._sfp_revisions[port]
    monkeypatch.setattr(scene_engine, "wait_for_sfp", confirm)
    result = await scene_client.put("/api/scene", json={"scene": scene, "expected_revision": current["revision"],
                                                       "preserve_unchanged": True, "confirm_output": True})
    assert result.status_code == 200 and waits == [9]
    current = result.json()
    scene = deepcopy(current["scene"])
    scene["overrides"] = {"1": {"color": "#234567", "brightness": 40}}
    waits.clear()
    result = await scene_client.put("/api/scene", json={"scene": scene, "expected_revision": current["revision"],
                                                       "preserve_unchanged": True, "confirm_output": True})
    assert result.status_code == 200 and waits == []


@pytest.mark.anyio
async def test_atomic_scene_keeps_saved_intent_when_acknowledgment_is_pending(scene_client, scene_engine, monkeypatch):
    scene_engine.switch.sfp_ports = (9, 10)
    current = (await scene_client.get("/api/scene")).json()
    scene = deepcopy(current["scene"])
    scene["sfp"]["9"].update({"mode": "color", "color": "#123456"})
    async def pending(port):
        pass
    monkeypatch.setattr(scene_engine, "wait_for_sfp", pending)
    result = await scene_client.put("/api/scene", json={"scene": scene, "expected_revision": current["revision"],
                                                       "preserve_unchanged": True, "confirm_output": True})
    assert result.status_code == 409 and result.json()["detail"]["code"] == "output_unconfirmed"
    assert scene_engine.sfp[9].color.to_hex() == "#123456"


@pytest.mark.anyio
async def test_workspace_header_guards_target_resolution_and_preserves_optional_compatibility(config):
    app = create_app(config, autoconnect=False)
    sessions = app.state.sessions
    await sessions.connect("demo")
    workspace = sessions.workspace_id()
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url=LOCAL) as client:
        current = sessions.engine.snapshot()
        stale = await client.put("/api/brightness", json={"value": 23}, headers={"X-EE-Workspace": "old-device"})
        assert stale.status_code == 409 and stale.json()["detail"]["code"] == "workspace_changed"
        assert sessions.engine.snapshot() == current
        stale_logout = await client.post("/api/session/logout", headers={"X-EE-Workspace": "old-device"})
        assert stale_logout.status_code == 409 and sessions.engine is not None
        accepted = await client.put("/api/brightness", json={"value": 23}, headers={"X-EE-Workspace": workspace})
        assert accepted.status_code == 200
        assert (await client.put("/api/brightness", json={"value": 31})).status_code == 200
    await sessions.close()


@pytest.mark.anyio
async def test_waiting_scene_response_does_not_adopt_another_clients_revision(scene_client, scene_engine, monkeypatch):
    scene_engine.switch.sfp_ports = (9, 10)
    before = (await scene_client.get("/api/scene")).json()
    first_scene = deepcopy(before["scene"])
    first_scene["sfp"]["9"].update({"mode": "color", "color": "#123456"})
    entered, complete = asyncio.Event(), asyncio.Event()
    async def confirm(port):
        entered.set()
        await complete.wait()
        scene_engine._sfp_acknowledged[port] = scene_engine._sfp_revisions[port]
    monkeypatch.setattr(scene_engine, "wait_for_sfp", confirm)
    first = asyncio.create_task(scene_client.put("/api/scene", json={"scene": first_scene,
        "expected_revision": before["revision"], "preserve_unchanged": True, "confirm_output": True}))
    await entered.wait()
    accepted_first = (await scene_client.get("/api/scene")).json()
    second_scene = deepcopy(accepted_first["scene"])
    second_scene["overrides"] = {"2": {"color": "#abcdef", "brightness": 47}}
    second = await scene_client.put("/api/scene", json={"scene": second_scene,
        "expected_revision": accepted_first["revision"], "preserve_unchanged": True})
    assert second.status_code == 200
    complete.set()
    result = await first
    assert result.status_code == 200 and result.json() == accepted_first
    assert result.json()["revision"] != second.json()["revision"]
    undo = await scene_client.put("/api/scene", json={"scene": before["scene"],
                                                    "expected_revision": result.json()["revision"]})
    assert undo.status_code == 409 and scene_engine.snapshot() == second.json()["scene"]


@pytest.mark.anyio
async def test_sfp_confirmation_cannot_adopt_a_newer_second_port_intent(scene_client, scene_engine, monkeypatch):
    scene_engine.switch.sfp_ports = (9, 10)
    before = (await scene_client.get("/api/scene")).json()
    scene = deepcopy(before["scene"])
    for port in ("9", "10"):
        scene["sfp"][port].update({"mode": "color", "color": "#123456"})
    waits = []
    async def replaced(port):
        waits.append(port)
        scene_engine._sfp_acknowledged[port] = scene_engine._sfp_revisions[port]
        scene_engine.set_sfp(10, {"color": "#abcdef"})
    monkeypatch.setattr(scene_engine, "wait_for_sfp", replaced)
    result = await scene_client.put("/api/scene", json={"scene": scene, "expected_revision": before["revision"],
                                                       "preserve_unchanged": True, "confirm_output": True})
    assert result.status_code == 409 and result.json()["detail"]["code"] == "output_unconfirmed"
    assert waits == [9] and scene_engine.sfp[10].color.to_hex() == "#abcdef"


async def save_profile(client, name, scene):
    current = (await client.get("/api/scene")).json()
    saved = await client.post("/api/presets", json={"name": name, "scene": scene,
                                                   "expected_revision": current["revision"]})
    assert saved.status_code == 201, saved.text


@pytest.mark.anyio
async def test_profile_apply_reports_sfp_output_that_stays_pending(scene_client, scene_engine, monkeypatch):
    scene_engine.switch.sfp_ports = (9, 10)
    scene = deepcopy(scene_engine.snapshot())
    scene["sfp"]["9"].update({"mode": "color", "color": "#123456"})
    await save_profile(scene_client, "Pending", scene)
    async def pending(port):
        pass
    monkeypatch.setattr(scene_engine, "wait_for_sfp", pending)
    result = await scene_client.post("/api/presets/Pending/apply")
    assert result.status_code == 409 and result.json()["detail"]["code"] == "output_unconfirmed"
    assert scene_engine.sfp[9].color.to_hex() == "#123456"


@pytest.mark.anyio
async def test_profile_apply_reports_an_unconfirmed_offline_copy(scene_app, scene_client, scene_engine, monkeypatch):
    scene = {**deepcopy(scene_engine.snapshot()), "brightness": 41}
    await save_profile(scene_client, "Offline", scene)
    sessions, saved = scene_app.state.sessions, []
    async def unconfirmed(engine):
        raise OSError("fixture offline copy failure")
    monkeypatch.setattr(sessions, "save_current_scene", saved.append)
    monkeypatch.setattr(sessions, "sync_offline_lighting", unconfirmed)
    monkeypatch.setattr(scene_engine.switch.transport, "desktop_output", object(), raising=False)
    result = await scene_client.post("/api/presets/Offline/apply")
    assert result.status_code == 503 and result.json()["detail"]["code"] == "offline_scene_unconfirmed"
    assert saved == [scene_engine] and scene_engine.brightness == 41
