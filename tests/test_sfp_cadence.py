import asyncio

import pytest
from test_native_switch import Clock, MemoryTransport, ready

from etherlighting_evolved import timing
from etherlighting_evolved.color import Color
from etherlighting_evolved.engine import Engine, to_output
from etherlighting_evolved.switch import PortOutput


class SfpTransport(MemoryTransport):
    async def write_sfp_precise(self, port, channels):
        await self.emit(("sfp_pwm", port, channels))


@pytest.fixture
def clock(monkeypatch):
    value = Clock()
    monkeypatch.setattr(timing, "monotonic", lambda: value.now)
    monkeypatch.setattr(timing, "wait", value.wait)
    return value


@pytest.mark.anyio
async def test_two_animated_sfp_ports_do_not_wait_for_eight_unchanged_refreshes(clock):
    switch, transport = await ready(SfpTransport())
    engine = Engine(switch, transition=0, rules=[])
    for port, color in ((9, "blue"), (10, "red")):
        engine.set_sfp(port, {"mode": "color", "effect": "color_cycle", "color": color, "period": 6.1})
    writes = []

    async def record(event):
        if event[0] in ("pwm", "rgb", "mode", "sfp_pwm"):
            writes.append((clock.now, event))

    transport.hook = record
    copper = [to_output(Color(0, 0, 255), 100, precise=True)] * 8
    start = clock.now
    while clock.now - start < 12:
        frame_start = clock.now
        await switch.write(copper, sfp=engine.sfp_indicators.outputs(), max_commands=30)
        clock.now = max(clock.now, frame_start + 1 / 25)
    assert all(b[0] - a[0] >= 1 / 30 - 1e-8 for a, b in zip(writes, writes[1:], strict=False))
    for port in (9, 10):
        samples = [at for at, event in writes if event[:2] == ("sfp_pwm", port) and at > start + 1]
        assert len(samples) > 100
        assert max(b-a for a, b in zip(samples, samples[1:], strict=False)) < 0.135
    for port in range(1, 9):
        samples = [at for at, event in writes if event[:2] == ("pwm", port)]
        assert len(samples) >= 6  # Every copper port is still refreshed.
        assert max(b-a for a, b in zip(samples, samples[1:], strict=False)) < 2.4


@pytest.mark.anyio
async def test_interrupted_refresh_is_not_cached_and_remains_due(clock):
    switch, transport = await ready(SfpTransport())
    copper = [PortOutput.off()] * 8
    sfp = {9: PortOutput(Color(0, 0, 20), 100, (0, 0, 5000))}
    await switch.write(copper, sfp=sfp)
    previous = dict(switch._last_sent)
    clock.now += 2
    halted = asyncio.Event()
    clock.on_wait = halted.set
    # Put the next update inside the pacing wait, then halt it.
    switch._last_attempt_at = clock.now
    assert await switch.write(copper, sfp=sfp, stop=halted) == 0
    assert switch._last_sent == previous
    assert await switch.write(copper, sfp=sfp) == 1
    updated = [port for port in previous if previous[port] != switch._last_sent[port]]
    assert len(updated) == 1


@pytest.mark.anyio
async def test_force_and_sfp_release_keep_existing_copper_refresh_behavior(clock):
    switch, _ = await ready(SfpTransport())
    copper = [PortOutput.off()] * 8
    sfp = {9: PortOutput(Color(0, 0, 20), 100, (0, 0, 5000))}
    await switch.write(copper, sfp=sfp)
    assert await switch.write(copper, sfp=sfp, force=True) == 9
    await switch.release_sfp(9)
    clock.now += 2
    assert await switch.write(copper) == 8
