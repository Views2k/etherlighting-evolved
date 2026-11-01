import asyncio
import contextlib

import httpx
import pytest

from etherlighting_evolved.api import create_app
from etherlighting_evolved.engine import Engine
from etherlighting_evolved.switch import EtherlightSwitch
from etherlighting_evolved.transport import SimulatedTransport


@pytest.mark.anyio
async def test_unmanaged_api_waits_for_submitted_sfp_ack_and_never_resubmits_released_port(config, monkeypatch):
    sim = SimulatedTransport(latency=0)
    switch = EtherlightSwitch(sim, keepalive=60)
    await switch.connect()
    await switch.probe()
    await switch.initialize_output()
    engine = Engine(switch, transition=0, rules=[])
    engine.connected = True
    engine.set_sfp(9, {"mode": "color", "color": "blue"})
    engine.set_sfp(10, {"mode": "color", "color": "red"})
    app = create_app(config, autoconnect=False)
    app.state.sessions.engine = engine

    submitted = asyncio.Event()
    acknowledge = asyncio.Event()
    released = asyncio.Event()
    other_written = asyncio.Event()
    original_run, original_set, original_mode = sim.run, engine.set_sfp, sim.write_sfp_mode
    writes = []
    colors = []
    modes = []
    active = peak = 0

    async def controlled(command, **kwargs):
        nonlocal active, peak
        if '"port_rgb":' not in command:
            return await original_run(command, **kwargs)
        port = int(command.split('"port_rgb":"', 1)[1].split()[0])
        writes.append(port)
        colors.append((port, command.split('"port_rgb":"', 1)[1].split()[1]))
        active += 1
        peak = max(peak, active)
        try:
            if port == 9:
                submitted.set()
                await acknowledge.wait()
            result = await original_run(command, **kwargs)
            if port == 10:
                other_written.set()
            return result
        finally:
            active -= 1

    async def controlled_mode(port, direct):
        nonlocal active, peak
        active += 1
        peak = max(peak, active)
        modes.append((port, direct))
        try:
            await original_mode(port, direct)
        finally:
            active -= 1

    def mark_release(port, changes):
        original_set(port, changes)
        if port == 9 and changes.get("mode") == "unmanaged":
            released.set()

    monkeypatch.setattr(sim, "run", controlled)
    monkeypatch.setattr(sim, "write_sfp_mode", controlled_mode)
    monkeypatch.setattr(engine, "set_sfp", mark_release)
    render = asyncio.create_task(engine._render_loop())
    release = None
    try:
        await asyncio.wait_for(submitted.wait(), 2)
        async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app),
                                     base_url="http://127.0.0.1") as client:
            release = asyncio.create_task(client.put("/api/sfp/9", json={"mode": "unmanaged"}))
            await asyncio.wait_for(released.wait(), 2)
            assert not release.done()
            assert active == 1
            acknowledge.set()
            response = await asyncio.wait_for(release, 2)
            assert response.status_code == 200
            assert response.json()["sfp"][0]["mode"] == "unmanaged"
            assert response.json()["sfp"][0]["control"]["state"] == "idle"
            count_after_release = writes.count(9)
            assert count_after_release == 2  # acquisition black, then release black before status mode
            assert (9, False) in modes
            assert all(color == "000000" for port, color in colors if port == 9)
            await asyncio.wait_for(other_written.wait(), 2)
            assert writes.count(9) == count_after_release
            assert peak == 1
            assert set(writes) <= set(range(1, 11))
    finally:
        acknowledge.set()
        render.cancel()
        with contextlib.suppress(asyncio.CancelledError):
            await render
        if release is not None and not release.done():
            release.cancel()
            with contextlib.suppress(asyncio.CancelledError):
                await release
        await switch.close()
