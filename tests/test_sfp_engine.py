import asyncio

import pytest
from test_native_engine import NativeSwitch

from etherlighting_evolved.color import Color
from etherlighting_evolved.engine import Engine
from etherlighting_evolved.links import PortLink
from etherlighting_evolved.transport import TransportError


def engine_for_sfp(**kwargs):
    engine = Engine(NativeSwitch(), rules=[], **kwargs)
    engine.connected = True
    return engine


def start_renderer(engine):
    task = asyncio.create_task(engine._render_loop())
    engine._tasks = [task]
    return task


def test_sfp_color_brightness_composes_once_and_keeps_copper_precision_separate():
    engine = engine_for_sfp(gamma=1, brightness=30)
    engine.set_sfp(9, {"mode": "color", "color": "blue", "brightness": 40, "color_brightness": 50})
    outputs = engine.sfp_indicators.outputs()
    assert outputs[9].color == Color(0, 0, 15)
    assert outputs[9].precise_rgb is None and outputs[9].breath is None
    assert 10 not in outputs and engine.num_ports == 8
    engine.set_brightness(0)
    assert engine.sfp_indicators.outputs()[9].color == Color(0, 0, 0)


def test_requested_sfp_setting_is_pending_and_never_changes_real_link_facts():
    engine = engine_for_sfp()
    engine.links[9] = PortLink(9, True, False, 0, False, anomaly=1)
    engine.set_sfp(9, {"mode": "color", "effect": "two_color", "color": "red", "secondary_color": "blue"})
    state = engine.sfp_indicators.state()[0]
    assert state["control"] == {"state": "pending", "owned": False}
    assert state["revision"] > state["acknowledged_revision"]
    assert state["link"] is False and state["anomaly"] is True


@pytest.mark.anyio
async def test_sfp_api_wait_needs_matching_rendered_output_ack():
    engine = engine_for_sfp()
    engine.switch.write_gate.clear()
    engine.set_sfp(9, {"mode": "color", "color": "red"})
    start_renderer(engine)
    try:
        await asyncio.wait_for(engine.switch.write_started.wait(), 1)
        waiting = asyncio.create_task(engine.wait_for_sfp(9))
        await asyncio.sleep(0)
        assert not waiting.done() and engine.sfp_indicators.state()[0]["control"]["state"] == "pending"
        engine.switch.write_gate.set()
        await asyncio.wait_for(waiting, 1)
        state = engine.sfp_indicators.state()[0]
        assert state["control"] == {"state": "active", "owned": True}
        assert state["revision"] == state["acknowledged_revision"]
    finally:
        engine.switch.write_gate.set()
        await engine.stop()


@pytest.mark.anyio
async def test_a_halted_frame_cannot_acknowledge_an_unsent_sfp_change():
    engine = engine_for_sfp()
    engine.switch.ack_sfp = False
    engine.set_sfp(9, {"mode": "color", "color": "red"})
    start_renderer(engine)
    try:
        await asyncio.wait_for(engine.switch.write_started.wait(), 1)
        state = engine.sfp_indicators.state()[0]
        assert state["control"]["state"] == "pending"
        assert state["revision"] != state["acknowledged_revision"]
    finally:
        await engine.stop()


@pytest.mark.anyio
async def test_rapid_color_edits_cannot_acknowledge_the_newer_color_with_an_old_frame():
    engine = engine_for_sfp()
    engine.switch.write_gate.clear()
    engine.set_sfp(9, {"mode": "color", "color": "red"})
    old_wait = asyncio.create_task(engine.wait_for_sfp(9))
    start_renderer(engine)
    try:
        await asyncio.wait_for(engine.switch.write_started.wait(), 1)
        engine.set_sfp(9, {"color": "blue"})
        with pytest.raises(ValueError, match="superseded"):
            await asyncio.wait_for(old_wait, 1)
        newest = engine.sfp_indicators.revisions[9]
        engine.switch.write_gate.set()
        await asyncio.wait_for(engine.wait_for_sfp(9), 1)
        assert engine.sfp_indicators.acknowledged[9] == newest
        assert engine.switch.sfp_applied[9].color == Color(0, 0, 255)
    finally:
        engine.switch.write_gate.set()
        await engine.stop()


@pytest.mark.anyio
async def test_status_release_waits_for_native_status_ack_and_stops_target_frames():
    engine = engine_for_sfp()
    engine.set_sfp(9, {"mode": "color", "color": "red"})
    start_renderer(engine)
    try:
        await asyncio.wait_for(engine.wait_for_sfp(9), 1)
        engine.switch.release_sfp_gate.clear()
        engine.set_sfp(9, {"mode": "unmanaged"})
        waiting = asyncio.create_task(engine.wait_for_sfp(9))
        await asyncio.wait_for(engine.switch.release_sfp_started.wait(), 1)
        assert not waiting.done() and 9 not in engine.sfp_indicators.outputs()
        assert engine.sfp_indicators.state()[0]["control"]["state"] == "pending"
        engine.switch.release_sfp_gate.set()
        await asyncio.wait_for(waiting, 1)
        assert engine.sfp_indicators.state()[0]["control"] == {"state": "idle", "owned": False}
        assert "release-sfp-10" not in engine.switch.calls
    finally:
        engine.switch.release_sfp_gate.set()
        await engine.stop()


@pytest.mark.anyio
async def test_default_unmanaged_release_writes_nothing():
    engine = engine_for_sfp()
    engine.set_sfp(9, {"mode": "unmanaged"})
    await engine.wait_for_sfp(9)
    assert not engine.switch.calls and not engine.sfp_indicators.outputs()


@pytest.mark.anyio
async def test_global_release_quiesces_owned_sfp_and_retains_pending_scene_intent():
    engine = engine_for_sfp()
    engine.set_sfp(9, {"mode": "off"})
    start_renderer(engine)
    try:
        await asyncio.wait_for(engine.wait_for_sfp(9), 1)
        active_revision = engine.sfp_indicators.state()[0]["revision"]
        engine.release()
        await engine.wait_for_output_idle()
        assert not engine.switch.sfp_applied
        assert engine.sfp[9].mode == "off"
        assert engine.sfp_indicators.state()[0]["control"] == {"state": "pending", "owned": False}
        assert engine.sfp_indicators.state()[0]["revision"] > active_revision
        assert "close" not in engine.switch.calls
        engine.resume()
        await asyncio.wait_for(engine.wait_for_sfp(9), 1)
        assert engine.switch.sfp_applied[9].color == Color(0, 0, 0)
    finally:
        await engine.stop()


@pytest.mark.anyio
async def test_sfp_cleanup_failure_keeps_released_and_blocks_resume():
    engine = engine_for_sfp()
    engine.set_sfp(9, {"mode": "color"})
    engine.switch.sfp_applied.update(engine.sfp_indicators.outputs())
    engine.switch.fail_sfp_release = True
    engine.set_sfp(9, {"mode": "unmanaged"})
    with pytest.raises(TransportError, match="SFP release"):
        await engine.wait_for_sfp(9)
    assert engine.mode == "released" and engine._output_halt.is_set()
    assert engine.sfp_indicators.state()[0]["control"]["state"] == "uncertain"
    with pytest.raises(TransportError, match="Reconnect"):
        engine.resume()


def test_saved_sfp_effects_round_trip_as_pending_intent_without_acknowledgment():
    engine = engine_for_sfp()
    engine.set_sfp(9, {"mode": "color", "effect": "pulse", "period": 7, "color_brightness": 42})
    saved = engine.snapshot()
    restored = engine_for_sfp()
    restored.apply_snapshot(saved)
    assert restored.snapshot() == saved
    assert restored.sfp_indicators.state()[0]["control"] == {"state": "pending", "owned": False}
    assert restored.sfp_indicators.state()[1]["control"] == {"state": "idle", "owned": False}


def test_connection_loss_advances_sfp_revision_before_invalidating_its_ack():
    engine = engine_for_sfp()
    engine.set_sfp(9, {"mode": "color", "color": "red"})
    engine.switch.sfp_applied.update(engine.sfp_indicators.outputs())
    engine.sfp_indicators.acknowledged[9] = engine.sfp_indicators.revisions[9]
    before = engine.sfp_indicators.state()[0]
    engine._lost(TransportError("fixture disconnect"))
    after = engine.sfp_indicators.state()[0]
    assert after["revision"] > before["revision"]
    assert after["acknowledged_revision"] < after["revision"]
    assert after["control"]["state"] == "pending"
