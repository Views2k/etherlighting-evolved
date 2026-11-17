import asyncio

import pytest
from fastapi.testclient import TestClient

from etherlighting_evolved.api import create_app
from etherlighting_evolved.color import Color
from etherlighting_evolved.engine import Engine, SfpLight, to_output
from etherlighting_evolved.session import SessionManager
from etherlighting_evolved.switch import BOARD_LIGHTING_INFO, EtherlightSwitch, PortOutput, sfp_lighting_ports
from etherlighting_evolved.transport import SimulatedTransport, TransportError


@pytest.mark.parametrize("board, count, expected", [
    ("board.shortname=USPXG8P", 8, (9, 10)),
    ("board.name=USW-Pro-XG-8-PoE\nboard.sysid=0xed76", 8, (9, 10)),
    ("", 8, ()),
    ("board.shortname=USPXG8P", 10, ()),
    ("board.shortname=USPXG8P\nboard.name=Other", 8, ()),
    ("board.shortname=Other\nboard.name=USW-Pro-XG-8-PoE", 8, ()),
    ("board.shortname=USPXG8P\nboard.sysid=0x1234", 8, ()),
    ("board.shortname=USPXG8P\nboard.shortname=USPXG8P", 8, ()),
    ("hostname=USPXG8P", 8, ()),
])
def test_model_scope(board, count, expected):
    assert sfp_lighting_ports(board, count) == expected


async def fixture_engine():
    sim = SimulatedTransport(latency=0)
    switch = EtherlightSwitch(sim, keepalive=60)
    await switch.connect()
    info = await switch.probe()
    await switch.initialize_output()
    engine = Engine(switch, transition=0)
    engine.switch_info, engine.connected = info, True
    engine.links = await switch.read_links()
    return sim, engine


@pytest.mark.anyio
async def test_sfp_offline_manual_color_is_independent_of_effects_and_rules():
    sim, engine = await fixture_engine()
    sim.set_link(9, False)
    sim.set_anomaly(9, 2)
    engine.links = await engine.switch.read_links()
    assert engine.sfp_indicators.outputs() == {}
    engine.set_sfp(9, {"mode": "color", "color": "#2244cc", "brightness": 65})
    engine.set_effect("comet", clear_overrides=True)
    _, frame = engine.compose(t=1)
    expected = to_output(Color(34, 68, 204), 65)
    assert engine.sfp_indicators.outputs() == {9: expected}
    await engine.switch.write(frame, sfp=engine.sfp_indicators.outputs())
    assert sim.leds[9] == (expected.color.to_hex()[1:], 100)
    assert 10 not in sim.leds
    status = engine.sfp_indicators.state()[0]
    assert status["link"] is False and status["anomaly"] is True
    assert engine.num_ports == 8 and engine.zone().ports == tuple(range(1, 9))


@pytest.mark.anyio
async def test_sfp_off_release_reenable_and_master_brightness():
    sim, engine = await fixture_engine()
    frame = [PortOutput.off()] * 8
    engine.set_sfp(10, {"mode": "color", "color": "blue", "brightness": 50})
    engine.set_brightness(40)
    assert engine.sfp_indicators.outputs()[10] == to_output(Color(0, 0, 255), 20)
    await engine.switch.write(frame, sfp=engine.sfp_indicators.outputs())
    engine.set_sfp(10, {"mode": "off"})
    assert engine.sfp_indicators.outputs()[10] == PortOutput.off()
    await engine.switch.write(frame, sfp=engine.sfp_indicators.outputs())
    assert sim.leds[10] == ("000000", 100)
    engine.set_sfp(10, {"mode": "unmanaged"})
    await engine.wait_for_sfp(10)
    before = sim.command_count
    assert await engine.switch.write(frame, sfp=engine.sfp_indicators.outputs()) == 0
    assert sim.command_count == before
    engine.set_sfp(10, {"mode": "off"})
    assert await engine.switch.write(frame, sfp=engine.sfp_indicators.outputs()) == 1
    assert engine.switch.sfp_status(10)["owned"] is True
    assert engine.sfp[10].color == Color(0, 0, 255) and engine.sfp[10].brightness == 50


@pytest.mark.anyio
async def test_snapshot_roundtrip_old_scene_and_atomic_validation():
    _, engine = await fixture_engine()
    engine.set_sfp(9, {"mode": "color", "color": "blue", "brightness": 35})
    saved = engine.snapshot()
    _, other = await fixture_engine()
    other.apply_snapshot(saved)
    assert other.snapshot() == saved
    invalid = dict(saved, brightness=1, sfp={"9": {"mode": "invalid"}})
    with pytest.raises(ValueError):
        other.apply_snapshot(invalid)
    assert other.snapshot() == saved
    old = dict(saved)
    old.pop("sfp")
    other.apply_snapshot(old)
    assert other.sfp_indicators.outputs() == {}


@pytest.mark.anyio
async def test_sfp_preferences_survive_session_close_and_restart(config):
    session = SessionManager(config)
    await session.connect("demo")
    session.engine.set_sfp(9, {"mode": "color", "color": "#123456", "brightness": 60})
    await session.close()
    restored = SessionManager(config)
    try:
        await restored.connect("demo")
        assert restored.engine.sfp[9] == SfpLight("color", Color(18, 52, 86), 60)
    finally:
        await restored.close()


@pytest.mark.anyio
async def test_bad_extra_port_rejected_before_copper_writes():
    sim, engine = await fixture_engine()
    before = sim.command_count
    for port in (0, 1, 8, 11, True, 9.0, "9"):
        with pytest.raises(ValueError):
            await engine.switch.write([PortOutput.off()] * 8, sfp={port: PortOutput.off()})
    assert sim.command_count == before


@pytest.mark.anyio
async def test_failed_model_read_keeps_normal_control_and_disables_sfp():
    class UnknownBoard(SimulatedTransport):
        async def run(self, command, **kwargs):
            if command == BOARD_LIGHTING_INFO:
                raise TransportError("fixture read failure")
            return await super().run(command, **kwargs)

    switch = EtherlightSwitch(UnknownBoard(latency=0))
    await switch.connect()
    assert (await switch.probe())["port_rgb"] is True
    assert switch.sfp_ports == ()
    with pytest.raises(ValueError):
        Engine(switch).set_sfp(9, {"mode": "color"})
    await switch.write([PortOutput.off()] * 8)


def test_sfp_api_validation_and_saved_preset(config):
    with TestClient(create_app(config, autoconnect=False), base_url="http://127.0.0.1:8787") as client:
        assert client.put("/api/sfp/9", json={"mode": "color"}).status_code == 409
        client.post("/api/session/login", json={"host": "demo"})
        assert [p["port"] for p in client.get("/api/state").json()["sfp"]] == [9, 10]
        for data in ({}, {"mode": "bad"}, {"brightness": True}, {"brightness": 101},
                     {"brightness": None}, {"color": "invalid"}, {"enabled": True}):
            assert client.put("/api/sfp/9", json=data).status_code == 400
        for port in (0, 8, 11):
            assert client.put(f"/api/sfp/{port}", json={"mode": "color"}).status_code == 400
        response = client.put("/api/sfp/9", json={"mode": "color", "color": "blue", "brightness": 70})
        assert response.status_code == 200
        assert response.json()["sfp"][0]["brightness"] == 70
        client.post("/api/presets", json={"name": "SFP scene"})
        assert client.get("/api/presets").json()["SFP scene"]["sfp"]["9"]["color"] == "#0000ff"


@pytest.mark.anyio
async def test_sfp_release_cancels_pending_frame_at_shared_writer_boundary(monkeypatch):
    sim, engine = await fixture_engine()
    engine.set_sfp(9, {"mode": "color"})
    reached = asyncio.Event()
    resume = asyncio.Event()
    original = sim.run

    async def controlled(command, **kwargs):
        result = await original(command, **kwargs)
        if '"port_rgb":"1 ' in command:
            reached.set()
            await resume.wait()
        return result

    monkeypatch.setattr(sim, "run", controlled)
    task = asyncio.create_task(engine._render_loop())
    try:
        await asyncio.wait_for(reached.wait(), 2)
        engine.set_sfp(9, {"mode": "unmanaged"})
        resume.set()
        await asyncio.wait_for(engine.wait_for_sfp(9), 2)
        assert sim.leds[9] == ("000000", 100) and sim.sfp_modes[9] is False
        assert engine.switch.sfp_status(9)["state"] == "idle"
        assert 10 not in sim.leds
    finally:
        task.cancel()
        with pytest.raises(asyncio.CancelledError):
            await task
