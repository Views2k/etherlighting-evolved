import asyncio
import json
from contextlib import nullcontext
from types import SimpleNamespace

import pytest
from fastapi.testclient import TestClient

from etherlighting_evolved import engine as engine_module
from etherlighting_evolved.api import create_app
from etherlighting_evolved.color import BLACK
from etherlighting_evolved.session import LoginError
from etherlighting_evolved.transport import TransportError


@pytest.mark.parametrize("backoff_after,cleanup_failure", [
    ("before", False), ("blank", False), ("mode", False), ("target", False), ("mode", True),
])
def test_sfp_resume_survives_budget_backoff_after_acknowledged_acquisition_step(
        config, monkeypatch, tmp_path, backoff_after, cleanup_failure):
    app = create_app(config, autoconnect=False)
    trace = []
    shutdown = (pytest.raises(LoginError, match="original verified lighting connection")
                if cleanup_failure else nullcontext())
    with shutdown as shutdown_error, TestClient(app, base_url="http://127.0.0.1:8787") as client:
        assert client.post("/api/session/login", json={"host": "demo"}).status_code == 200
        assert client.post("/api/release").status_code == 200
        engine = app.state.sessions.engine
        switch = engine.switch
        original_rgb = switch._rgb_write
        original_mode = switch.transport.write_sfp_mode
        original_send = switch._send_sfp_locked
        original_timing = engine_module.timing
        injected = False
        stopped_frame = None
        cleanup_calls = 0
        frame_gate = asyncio.Event()

        def capture(event):
            trace.append({
                "event": event, "budget": engine.budget.value, "mode": engine.mode,
                "connected": engine.connected, "native_blocked": engine._native_blocked,
                "native_uncertain": switch.native_uncertain,
                "sfp_status": switch.sfp_status(9), "sfp_pending": 9 in switch._sfp_pending,
                "sfp_uncertain": 9 in switch._sfp_uncertain,
                "owned_generation_matches": switch._sfp_owned.get(9) is switch._sfp_generation,
                "revision": engine.sfp_indicators.revisions[9],
                "acknowledged_revision": engine.sfp_indicators.acknowledged[9],
                "requested_mode": switch.transport.sfp_modes.get(9),
                "last_led": switch.transport.leds.get(9),
                "stopped_frame": stopped_frame.is_set() if stopped_frame is not None else None,
                "current_frame_stopped": engine._output_halt.is_set(),
            })

        def after_ack(phase):
            nonlocal injected, stopped_frame
            capture(phase + " acknowledged")
            if phase == backoff_after and not injected:
                injected = True
                stopped_frame = engine._output_halt
                engine.budget.adjust(0.0)
                capture("CPU backoff")

        async def rgb(port, color, level):
            await original_rgb(port, color, level)
            if port == 9:
                after_ack("blank" if color == BLACK else "target")

        async def mode(port, direct):
            nonlocal cleanup_calls
            if port == 9 and not direct:
                cleanup_calls += 1
                if cleanup_failure:
                    capture("cleanup failure")
                    raise TransportError("fixture cleanup was not acknowledged")
            await original_mode(port, direct)
            if port == 9 and direct:
                after_ack("mode")

        async def send(*args, **kwargs):
            if backoff_after == "before" and not injected and engine.sfp[9].mode == "color":
                after_ack("before")
            return await original_send(*args, **kwargs)

        async def wait(delay, wake=None):
            if injected and switch.sfp_status(9)["state"] == "uncertain" and not frame_gate.is_set():
                capture("frame boundary delayed")
                await frame_gate.wait()
            return await original_timing.wait(delay, wake)

        monkeypatch.setattr(switch, "_rgb_write", rgb)
        monkeypatch.setattr(switch.transport, "write_sfp_mode", mode)
        monkeypatch.setattr(switch, "_send_sfp_locked", send)
        monkeypatch.setattr(engine_module, "timing", SimpleNamespace(monotonic=original_timing.monotonic, wait=wait))
        assert client.post("/api/resume").status_code == 200
        try:
            # Issue one acquisition. A duplicate PUT can cancel a frame mid-write and
            # legitimately add cleanup independent of the backoff being tested.
            response = client.put("/api/sfp/9", json={"mode": "color", "color": "blue"})
            capture("API response")
            evidence = {"backoff_after": backoff_after, "cleanup_failure": cleanup_failure,
                        "cleanup_calls": cleanup_calls, "status": response.status_code,
                        "response": response.json(), "trace": trace}
            (tmp_path / "repro.json").write_text(json.dumps(evidence, indent=2), encoding="utf-8")
            assert injected
            if cleanup_failure:
                assert cleanup_calls == 1, json.dumps(evidence, indent=2)
                assert response.status_code == 409, json.dumps(evidence, indent=2)
                assert switch.sfp_status(9)["state"] == "uncertain"
                assert engine.sfp_indicators.acknowledged[9] != engine.sfp_indicators.revisions[9]
            else:
                assert response.status_code == 200, json.dumps(evidence, indent=2)
                state = response.json()["sfp"][0]
                assert state["control"]["state"] == "active", json.dumps(evidence, indent=2)
                assert state["revision"] == state["acknowledged_revision"]
                assert cleanup_calls == (1 if backoff_after in ("blank", "mode") else 0)
        finally:
            client.portal.call(frame_gate.set)
            monkeypatch.setattr(switch.transport, "write_sfp_mode", original_mode)
    if cleanup_failure:
        assert shutdown_error.value.code == "output_unconfirmed"


@pytest.mark.parametrize("release_after", ["blank", "mode"])
def test_release_during_sfp_acquisition_confirms_handoff_before_resuming(config, monkeypatch, release_after):
    app = create_app(config, autoconnect=False)
    with TestClient(app, base_url="http://127.0.0.1:8787") as client:
        assert client.post("/api/session/login", json={"host": "demo"}).status_code == 200
        assert client.post("/api/release").status_code == 200
        assert client.put("/api/sfp/9", json={"mode": "color", "color": "blue"}).status_code == 200
        engine = app.state.sessions.engine
        switch = engine.switch
        original_rgb = switch._rgb_write
        original_mode = switch.transport.write_sfp_mode
        released = False
        targets_after_release = []

        def release(phase):
            nonlocal released
            if phase == release_after and not released:
                released = True
                engine.release()

        async def rgb(port, color, level):
            if port == 9 and color != BLACK and released:
                targets_after_release.append(color)
            await original_rgb(port, color, level)
            if port == 9 and color == BLACK:
                release("blank")

        async def mode(port, direct):
            await original_mode(port, direct)
            if port == 9 and direct:
                release("mode")

        monkeypatch.setattr(switch, "_rgb_write", rgb)
        monkeypatch.setattr(switch.transport, "write_sfp_mode", mode)
        assert client.post("/api/resume").status_code == 200
        response = client.put("/api/sfp/9", json={"mode": "color", "color": "blue"})
        assert response.status_code == 200, response.text
        assert released
        response = client.post("/api/release")
        assert response.status_code == 200, response.text
        assert engine.mode == "released"
        assert not targets_after_release
        assert switch.transport.sfp_modes[9] is False
        assert switch.sfp_status(9)["state"] == "idle"
        assert 9 not in switch._sfp_owned
        assert not engine._output_uncertain()
        assert engine.sfp_indicators.acknowledged[9] != engine.sfp_indicators.revisions[9]
        monkeypatch.setattr(switch, "_rgb_write", original_rgb)
        monkeypatch.setattr(switch.transport, "write_sfp_mode", original_mode)
        assert client.post("/api/resume").status_code == 200
        response = client.put("/api/sfp/9", json={"mode": "color", "color": "blue"})
        assert response.status_code == 200, response.text
        state = response.json()["sfp"][0]
        assert state["control"]["state"] == "active"
        assert state["revision"] == state["acknowledged_revision"]
