import asyncio
import json

import pytest
from fastapi.testclient import TestClient
from test_poe import SAMPLE

from etherlighting_evolved import timing
from etherlighting_evolved.api import create_app
from etherlighting_evolved.color import Color
from etherlighting_evolved.engine import Engine
from etherlighting_evolved.links import PortLink, TrafficMeter
from etherlighting_evolved.poe import parse_poe_sample, summarize_poe
from etherlighting_evolved.scene_history import HistoryStoreError, SceneHistory
from etherlighting_evolved.switch import EtherlightSwitch
from etherlighting_evolved.transport import SimulatedTransport, TransportError


async def identified_engine():
    switch = EtherlightSwitch(SimulatedTransport(latency=0))
    await switch.connect()
    await switch.probe()
    await switch.initialize_output()
    engine = Engine(switch, rules=[], transition=0, gamma=1,
                    effect_params={"color": "#ff0000"})
    engine.connected = True
    return engine


@pytest.mark.anyio
async def test_identify_expiry_restores_current_scene_not_old_snapshot(monkeypatch):
    engine = await identified_engine()
    clock = [100.0]
    monkeypatch.setattr(timing, "monotonic", lambda: clock[0])
    before = engine.snapshot()
    revision = engine.revision
    active = engine.start_identify(1)
    try:
        assert engine.snapshot() == before
        assert engine.revision == revision
        assert engine.compose()[0][0] == Color(255, 255, 255)
        engine.set_override(1, "#00ff00", 25)
        assert engine.compose()[0][0] == Color(255, 255, 255)
        clock[0] = 111
        assert engine.compose()[1][0].preview().to_hex() == "#004000"
        engine.stop_identify(active["id"])
        assert engine.identify_state() is None
        assert engine.overrides[1].color == Color(0, 255, 0)
    finally:
        engine.stop_identify()
        await engine.switch.close()


@pytest.mark.anyio
async def test_identify_timer_is_host_owned_and_new_request_is_protected():
    engine = await identified_engine()
    first = engine.start_identify(1)
    try:
        timer = engine._identify_timer
        assert 9 < timer.when() - asyncio.get_running_loop().time() <= 10
        with pytest.raises(ValueError, match="Another port"):
            engine.start_identify(2)
        timer._callback(*timer._args)
        assert engine.identify_state() is None
        second = engine.start_identify(2)
        with pytest.raises(ValueError, match="identification changed"):
            engine.stop_identify(first["id"])
        assert engine.identify_state()["id"] == second["id"]
        engine.release()
        assert engine.identify_state() is None
        assert engine._identify_timer is None
        with pytest.raises(ValueError, match="Resume"):
            engine.start_identify(3)
    finally:
        engine.stop_identify()
        await engine.switch.close()


@pytest.mark.anyio
async def test_identify_disconnect_drops_temporary_intent():
    engine = await identified_engine()
    engine.start_identify(1)
    engine._lost(TransportError("test disconnect"))
    assert engine.identify_state() is None
    assert engine._identify_timer is None
    assert engine.telemetry_state()["generation"] is None
    assert engine.telemetry_state()["stale"]
    await engine.switch.close()


@pytest.mark.anyio
async def test_identify_sfp_temporarily_owns_only_target_and_returns_native():
    engine = await identified_engine()
    try:
        before = engine.snapshot()
        active = engine.start_identify(9)
        assert set(engine.sfp_indicators.outputs()) == {9}
        assert engine.sfp_indicators.outputs()[9].preview() == Color(255, 255, 255)
        engine.set_sfp(9, {"mode": "color", "color": "#00ff00", "brightness": 100})
        engine.stop_identify(active["id"])
        assert engine.sfp_indicators.outputs()[9].preview() == Color(0, 255, 0)
        assert engine.snapshot()["sfp"]["10"] == before["sfp"]["10"]
        engine.set_sfp(9, {"mode": "unmanaged"})
        active = engine.start_identify(9)
        engine.stop_identify(active["id"])
        assert 9 in engine.sfp_indicators.release_ports
        await engine.wait_for_sfp(9)
        assert 9 not in engine.sfp_indicators.release_ports
        assert 9 not in engine.sfp_indicators.outputs()
    finally:
        engine.stop_identify()
        await engine.switch.close()


@pytest.mark.anyio
async def test_identifying_copper_does_not_identify_following_optical_ports():
    engine = await identified_engine()
    try:
        engine.set_sfp_sync({"mode": "ethernet", "source_port": 1})
        engine.compose()
        engine.start_identify(1)
        engine.compose()
        assert engine.sfp_indicators.outputs()[9].preview() == Color(255, 0, 0)
        engine.set_override(1, "#00ff00", 100)
        engine.compose()
        assert engine.sfp_indicators.outputs()[9].preview() == Color(0, 255, 0)
    finally:
        engine.stop_identify()
        await engine.switch.close()


def test_poe_budget_complete_partial_stale_and_missing():
    sample = parse_poe_sample(SAMPLE)
    current = summarize_poe(sample, range(1, 5), age=1, connected=True)
    assert current["status"] == "ready"
    assert current["budget_w"] == 155
    assert current["draw_w"] == 74.63
    assert current["headroom_w"] == 80.37
    assert current["consumers"][0] == {"port": 4, "power_w": 71.2}
    assert summarize_poe(sample, range(1, 9), age=1, connected=True)["status"] == "partial"
    assert summarize_poe(sample, range(1, 5), age=10, connected=True)["headroom_w"] is None
    assert summarize_poe(sample, range(1, 5), age=0, connected=False)["status"] == "stale"
    no_budget = parse_poe_sample(SAMPLE.replace("Total Power Limit(mW): 155000", ""))
    assert summarize_poe(no_budget, range(1, 5), age=0, connected=True)["status"] == "budget_unavailable"
    malformed = parse_poe_sample(SAMPLE.replace("71.20", "bad"))
    assert summarize_poe(malformed, range(1, 5), age=0, connected=True)["status"] == "partial"


def test_traffic_sample_validity_distinguishes_first_reset_missing_and_zero():
    meter = TrafficMeter()
    link = PortLink(1, True, True, 10000, True, rx_bytes=100)
    meter.update({1: link}, now=0)
    assert meter.valid == set()
    assert meter.update({1: link}, now=1)[1] == 0
    assert meter.valid == {1}
    reset = PortLink(1, True, True, 10000, True, rx_bytes=0)
    meter.update({1: reset}, now=2)
    assert meter.valid == set()
    meter.update({}, now=3)
    meter.update({1: reset}, now=4)
    assert meter.valid == set()
    missing = PortLink(1, True, True, 10000, True, counters_valid=False)
    meter.update({1: missing}, now=5)
    assert meter.valid == set()


def test_history_deduplicates_scopes_restarts_and_bounds(tmp_path):
    store = SceneHistory(tmp_path / "history.json")
    engine = Engine(EtherlightSwitch(SimulatedTransport()), rules=[])
    engine.attach_history(store, "device-one")
    initial = store.list("device-one")[0]
    engine.set_brightness(25)
    engine.set_brightness(25)
    assert len(store.list("device-one")) == 2
    assert store.list("device-one")[1] == initial
    engine.attach_history(SceneHistory(store.path), "device-one")
    assert len(store.list("device-one")) == 2
    engine.attach_history(store, "device-two")
    engine.set_brightness(50)
    assert len(store.list("device-one")) == 2
    assert store.get("device-two", initial["id"]) is None
    for value in range(80):
        engine.set_brightness(value)
    assert len(store.list("device-two")) == 50
    assert store.list("device-two")[0]["scene"]["brightness"] == 79


def test_corrupt_history_is_preserved_and_reported_without_rejecting_scene(tmp_path):
    path = tmp_path / "history.json"
    path.write_text('{"invalid":true}')
    engine = Engine(EtherlightSwitch(SimulatedTransport()), rules=[])
    engine.attach_history(SceneHistory(path), "device")
    engine.set_brightness(21)
    assert engine.brightness == 21
    assert engine.history_error
    assert json.loads(path.read_text()) == {"invalid": True}
    with pytest.raises(HistoryStoreError):
        engine.history[0].list("device")


def test_history_restoration_uses_scene_guard_and_no_identify_saved(config):
    app = create_app(config, demo=True)
    with TestClient(app, base_url="http://127.0.0.1") as client:
        initial = client.get("/api/history").json()["entries"][0]
        before = client.get("/api/scene").json()
        scene = {**before["scene"], "brightness": 47}
        changed = client.put("/api/scene", json={"scene": scene, "expected_revision": before["revision"]})
        assert changed.status_code == 200
        response = client.post(f"/api/history/{initial['id']}/restore",
                               json={"expected_revision": before["revision"]})
        assert response.status_code == 409
        assert client.get("/api/state").json()["brightness"] == 47
        response = client.post(f"/api/history/{initial['id']}/restore",
                               json={"expected_revision": changed.json()["revision"]})
        assert response.status_code == 200
        assert response.json()["scene"] == before["scene"]
        count = len(client.get("/api/history").json()["entries"])
        identify = client.post("/api/identify", json={"port": 1})
        assert identify.status_code == 200
        assert client.post("/api/identify", json={"port": 2}).status_code == 400
        assert client.request("DELETE", "/api/identify", json={"id": identify.json()["id"]}).status_code == 200
        assert len(client.get("/api/history").json()["entries"]) == count
        assert client.post("/api/identify", json={"port": True}).status_code == 422


@pytest.mark.anyio
async def test_identify_sfp_actual_writer_acknowledges_and_releases_native_control():
    engine = await identified_engine()
    await engine.start()
    try:
        active = engine.start_identify(9)
        await asyncio.wait_for(engine.wait_for_sfp(9), 3)
        assert engine.switch.sfp_status(9)["owned"]
        assert engine.sfp_indicators.acknowledged[9] == engine.sfp_indicators.revisions[9]
        assert not engine.switch.sfp_status(10)["owned"]
        engine.stop_identify(active["id"])
        await asyncio.wait_for(engine.wait_for_sfp(9), 3)
        assert not engine.switch.sfp_status(9)["owned"]
        assert engine.sfp[9].mode == "unmanaged"
    finally:
        await engine.stop()


@pytest.mark.anyio
async def test_identify_rejects_output_qualification_and_uncertain_state(monkeypatch):
    engine = await identified_engine()
    try:
        engine.switch.native_uncertain = True
        with pytest.raises(ValueError, match="Resume"):
            engine.start_identify(1)
        engine.switch.native_uncertain = False
        def unqualified():
            raise ValueError("qualification missing")
        monkeypatch.setattr(engine.switch, "require_output_profile", unqualified)
        with pytest.raises(ValueError, match="qualification"):
            engine.start_identify(1)
        assert engine.identify_state() is None
        assert engine._identify_timer is None
    finally:
        await engine.switch.close()
