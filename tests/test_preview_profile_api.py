from concurrent.futures import ThreadPoolExecutor
from copy import deepcopy
from types import SimpleNamespace

import httpx
import pytest

from etherlighting_evolved.api import create_app
from etherlighting_evolved.engine import Engine
from etherlighting_evolved.presets import PresetStore
from etherlighting_evolved.routes.context import scene_envelope
from etherlighting_evolved.switch import EtherlightSwitch
from etherlighting_evolved.transport import SimulatedTransport


@pytest.fixture
def preview_app(config):
    app = create_app(config, autoconnect=False)
    transport = SimulatedTransport(latency=0)
    switch = EtherlightSwitch(transport)
    switch.sfp_ports = (9, 10)
    engine = Engine(switch, rules=[])
    app.state.sessions.engine = engine
    return app, engine, transport


def active_state(engine, transport):
    return {
        "scene": deepcopy(engine.snapshot()),
        "revision": engine.revision,
        "zones": [id(zone) for zone in engine.zones],
        "dirty": engine._dirty.is_set(),
        "halt": (id(engine._output_halt), engine._output_halt.is_set()),
        "sfp_changed": engine._sfp_changed.is_set(),
        "sfp_revisions": dict(engine._sfp_revisions),
        "sfp_acknowledged": dict(engine._sfp_acknowledged),
        "sfp_release": set(engine._sfp_release_ports),
        "origin": engine._animation_origin,
        "animation_time": engine.budget.animation_time,
        "commands": transport.command_count,
        "leds": deepcopy(transport.leds),
    }


def preview_payload(engine, **changes):
    base = scene_envelope(engine.snapshot())
    staged = deepcopy(base["scene"])
    staged["brightness"] = 37
    staged["overrides"] = {"1": {"color": "#123456", "brightness": 42}}
    staged["sfp"]["9"].update({"mode": "color", "color": "#112233", "brightness": 71})
    return {"name": "Preview", "scene": staged, "expected_revision": base["revision"],
            "overwrite": False, **changes}


@pytest.mark.anyio
async def test_preview_save_does_not_apply_output_or_mutate_engine(preview_app, monkeypatch):
    app, engine, transport = preview_app
    before = active_state(engine, transport)
    payload = preview_payload(engine)

    def forbidden(*args, **kwargs):
        pytest.fail("Saving a preview must not apply or signal engine output")

    monkeypatch.setattr(engine, "apply_snapshot", forbidden)
    monkeypatch.setattr(engine, "_changed", forbidden)
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://localhost") as client:
        response = await client.post("/api/presets", json=payload)
    assert response.status_code == 201, response.text
    saved = response.json()["Preview"]
    assert saved["brightness"] == 37
    assert saved["overrides"]["1"] == {"color": "#123456", "brightness": 42}
    assert saved["sfp"]["9"]["brightness"] == 71
    assert active_state(engine, transport) == before


@pytest.mark.anyio
@pytest.mark.parametrize("bad_part", ["topology", "overrides", "rules", "sfp", "zones"])
async def test_invalid_preview_is_rejected_before_profile_storage(preview_app, bad_part):
    app, engine, transport = preview_app
    before = active_state(engine, transport)
    payload = preview_payload(engine)
    invalid = {"topology": {"version": 1, "model": None, "copper_ports": [1], "sfp_ports": [9, 10]},
               "overrides": {"99": {"color": "#ffffff"}}, "rules": [None],
               "sfp": {"11": {"mode": "color"}}, "zones": [None]}
    payload["scene"][bad_part] = invalid[bad_part]
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://localhost") as client:
        response = await client.post("/api/presets", json=payload)
    assert response.status_code == 400, response.text
    assert app.state.presets.list() == {}
    assert active_state(engine, transport) == before


@pytest.mark.anyio
@pytest.mark.parametrize("revision,status,code", [
    (None, 400, "scene_revision_required"), ("stale", 409, "scene_changed")
])
async def test_preview_requires_unchanged_scene_revision(preview_app, revision, status, code):
    app, engine, transport = preview_app
    before = active_state(engine, transport)
    payload = preview_payload(engine, expected_revision=revision)
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://localhost") as client:
        response = await client.post("/api/presets", json=payload)
    assert response.status_code == status, response.text
    assert response.json()["detail"]["code"] == code
    assert app.state.presets.list() == {}
    assert active_state(engine, transport) == before


@pytest.mark.anyio
async def test_preview_name_collision_keeps_the_existing_profile(preview_app):
    app, engine, transport = preview_app
    original = engine.snapshot()
    app.state.presets.save("Preview", original)
    before = active_state(engine, transport)
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://localhost") as client:
        response = await client.post("/api/presets", json=preview_payload(engine))
    assert response.status_code == 409
    assert response.json()["detail"]["code"] == "already_exists"
    assert app.state.presets.get("Preview") == original
    assert active_state(engine, transport) == before


@pytest.mark.anyio
async def test_current_snapshot_save_requires_explicit_guarded_replacement(preview_app):
    app, engine, transport = preview_app
    app.state.presets.save("Current", {"brightness": 12})
    before = active_state(engine, transport)
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://localhost") as client:
        response = await client.post("/api/presets", json={"name": "Current"})
    assert response.status_code == 409
    assert app.state.presets.get("Current") == {"brightness": 12}
    assert active_state(engine, transport) == before


@pytest.mark.anyio
async def test_preview_retains_native_capability_validation(preview_app):
    app, engine, _ = preview_app
    payload = preview_payload(engine)
    payload["scene"]["zones"][0]["effect"] = {
        "id": "breathing", "params": {"playback": "synchronized", "pace": 2}
    }
    engine.connected = True
    engine.switch.transport = SimpleNamespace()
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://localhost") as client:
        response = await client.post("/api/presets", json=payload)
    assert response.status_code == 400, response.text
    assert "synchronized Breathing is unavailable" in response.text
    assert app.state.presets.list() == {}


def test_validated_snapshot_matches_apply_normalization_without_early_mutation(preview_app):
    _, engine, transport = preview_app
    before = active_state(engine, transport)
    staged = preview_payload(engine)["scene"]
    staged["brightness"] = 37.2
    prepared = engine.validate_snapshot(staged)
    assert active_state(engine, transport) == before
    engine.apply_snapshot(staged)
    assert engine.snapshot() == prepared


def test_nonoverwriting_profile_save_is_atomic_for_concurrent_requests(tmp_path):
    store = PresetStore(tmp_path / "profiles.json")

    def save(number):
        try:
            store.save(" Shared ", {"brightness": number}, overwrite=False)
            return number
        except FileExistsError:
            return None

    with ThreadPoolExecutor(max_workers=4) as executor:
        accepted = [result for result in executor.map(save, range(8)) if result is not None]
    assert len(accepted) == 1
    assert store.get("Shared") == {"brightness": accepted[0]}
