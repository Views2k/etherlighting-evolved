import asyncio
import time

import pytest
from fastapi.testclient import TestClient
from test_sfp_engine import engine_for_sfp, start_renderer

from etherlighting_evolved.api import create_app
from etherlighting_evolved.color import Color
from etherlighting_evolved.links import PortLink
from etherlighting_evolved.rules import Rule
from etherlighting_evolved.transport import TransportError


def anomaly(engine, port, active):
    engine.links[port] = PortLink(port, True, False, 0, False, anomaly=int(active))
    engine._dirty.set()


def alert(ports=None, **look):
    return {"name": "SFP anomaly", "ports": ports,
            "conditions": [{"field": "anomaly", "op": "is", "value": True}],
            "look": {"color": "red", "style": "solid", **look}}


async def settled(engine, port):
    engine.sfp_indicators.sync_rules()
    await asyncio.wait_for(engine.wait_for_sfp(port), 1)


def test_rule_scope_requires_verified_sfp_and_general_rules_remain_copper_only():
    with pytest.raises(ValueError):
        Rule.parse(alert([9]), num_ports=8)
    assert Rule.parse(alert([9, 10]), num_ports=8, extra_ports=(9, 10)).ports == (9, 10)
    engine = engine_for_sfp()
    anomaly(engine, 9, True)
    engine.add_rule(alert())
    assert not engine.sfp_indicators.outputs()
    engine.switch.sfp_ports = ()
    with pytest.raises(ValueError):
        engine.add_rule(alert([9]))


@pytest.mark.anyio
async def test_anomaly_acquires_only_targeted_sfp_then_restores_link_status():
    engine = engine_for_sfp(gamma=1)
    engine.add_rule(alert([9]))
    start_renderer(engine)
    try:
        anomaly(engine, 9, True)
        await settled(engine, 9)
        assert engine.switch.sfp_applied[9].color == Color(255, 0, 0)
        assert 10 not in engine.switch.sfp_applied
        state = engine.sfp_indicators.state()[0]
        assert state["mode"] == "unmanaged" and state["rule"]["name"] == "SFP anomaly"
        assert state["preview"] == "#ff0000" and state["facts"]["anomaly"] is True
        assert state["control"]["state"] == "active"
        anomaly(engine, 9, False)
        await settled(engine, 9)
        assert 9 not in engine.switch.sfp_applied
        assert engine.sfp_indicators.state()[0]["rule"] is None
        assert engine.sfp_indicators.state()[0]["control"]["state"] == "idle"
    finally:
        await engine.stop()


@pytest.mark.anyio
async def test_rule_clear_returns_to_saved_color_and_explicit_off_stays_off():
    engine = engine_for_sfp(gamma=1, brightness=50)
    engine.set_sfp(9, {"mode": "color", "color": "blue", "brightness": 50})
    rule = engine.add_rule(alert([9], brightness=80))
    anomaly(engine, 9, True)
    start_renderer(engine)
    try:
        await settled(engine, 9)
        assert engine.switch.sfp_applied[9].color == Color(102, 0, 0)
        engine.update_rule(rule.id, {"enabled": False})
        await settled(engine, 9)
        assert engine.switch.sfp_applied[9].color == Color(0, 0, 64)
        engine.update_rule(rule.id, {"enabled": True})
        engine.set_sfp(9, {"mode": "off"})
        await settled(engine, 9)
        assert engine.switch.sfp_applied[9].color == Color(0, 0, 0)
        assert engine.sfp_indicators.state()[0]["rule"] is None
    finally:
        await engine.stop()


@pytest.mark.anyio
async def test_disabling_deleting_and_reordering_rules_restores_current_priority():
    engine = engine_for_sfp(gamma=1)
    anomaly(engine, 9, True)
    red = engine.add_rule(alert([9]))
    blue = engine.add_rule(alert([9], color="blue"))
    start_renderer(engine)
    try:
        await settled(engine, 9)
        assert engine.switch.sfp_applied[9].color == Color(255, 0, 0)
        engine.reorder_rules([blue.id, red.id])
        await settled(engine, 9)
        assert engine.switch.sfp_applied[9].color == Color(0, 0, 255)
        engine.delete_rule(blue.id)
        engine.delete_rule(red.id)
        await settled(engine, 9)
        assert not engine.switch.sfp_applied
    finally:
        await engine.stop()


def test_unknown_facts_do_not_trigger_alert_and_keep_effect_preserves_link_status():
    engine = engine_for_sfp()
    engine.add_rule(alert([9]))
    assert engine.sfp_indicators.outputs() == {}
    anomaly(engine, 9, True)
    engine.set_rules([alert([9], style="effect", brightness=50)])
    assert engine.sfp_indicators.outputs() == {}


def test_scene_round_trip_preserves_targets_without_persisting_active_rule_ownership():
    engine = engine_for_sfp()
    rule = engine.add_rule(alert([9, 10]))
    anomaly(engine, 9, True)
    snapshot = engine.snapshot()
    restored = engine_for_sfp()
    restored.apply_snapshot(snapshot)
    assert restored.snapshot() == snapshot
    assert restored.rules[0].id == rule.id
    assert restored.sfp_indicators.outputs() == {}
    assert all(item["rule"] is None for item in restored.sfp_indicators.state())


@pytest.mark.anyio
async def test_failed_alert_release_keeps_output_blocked_until_reconnection():
    engine = engine_for_sfp()
    engine.add_rule(alert([9]))
    anomaly(engine, 9, True)
    engine.sfp_indicators.sync_rules()
    engine.switch.sfp_applied.update(engine.sfp_indicators.outputs())
    engine.switch.fail_sfp_release = True
    anomaly(engine, 9, False)
    engine.sfp_indicators.sync_rules()
    with pytest.raises(TransportError):
        await engine.wait_for_sfp(9)
    assert engine.mode == "released" and engine._native_blocked
    with pytest.raises(TransportError):
        engine.resume()


def test_rule_api_anomaly_output_and_restart_persistence(config):
    config.engine.link_poll = 0.5
    with TestClient(create_app(config, autoconnect=False), base_url="http://127.0.0.1:8787") as client:
        assert client.post("/api/session/login", json={"host": "demo"}).status_code == 200
        added = client.post("/api/rules", json=alert([9]))
        assert added.status_code == 201
        rule_id = added.json()["id"]
        assert client.put("/api/simulator/anomaly/9", json={"code": 1}).status_code == 200
        deadline = time.monotonic() + 2
        while True:
            state = client.get("/api/state").json()["sfp"][0]
            if state["rule"] and state["control"]["state"] == "active":
                break
            assert time.monotonic() < deadline, state["control"]
            time.sleep(.02)
        assert state["rule"]["id"] == rule_id and state["preview"] == "#ff0000"
        assert client.post("/api/presets", json={"name": "SFP alerts"}).status_code == 201
        assert client.get("/api/presets").json()["SFP alerts"]["rules"][-1]["ports"] == [9]
        assert client.post("/api/session/logout").status_code == 200
    with TestClient(create_app(config, autoconnect=False), base_url="http://127.0.0.1:8787") as client:
        assert client.post("/api/session/login", json={"host": "demo"}).status_code == 200
        restored = client.get("/api/rules").json()
        assert next(rule for rule in restored if rule["id"] == rule_id)["ports"] == [9]
