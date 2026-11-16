import asyncio

import pytest
from test_native_switch import Clock, ready
from test_sfp_cadence import SfpTransport

from etherlighting_evolved import timing
from etherlighting_evolved.engine import Engine


@pytest.mark.anyio
@pytest.mark.parametrize("effect", ["pulse", "two_color", "color_cycle"])
async def test_sfp_sampling_uses_available_headroom_without_accelerating_copper(effect, monkeypatch):
    clock = Clock()
    monkeypatch.setattr(timing, "monotonic", lambda: clock.now)
    monkeypatch.setattr(timing, "wait", clock.wait)
    switch, transport = await ready(SfpTransport())
    engine = Engine(switch, rules=[], transition=0, fps=25)
    engine.connected = True
    engine.budget.set(200)
    # Hold already-earned headroom to isolate scheduling from the independently tested CPU governor.
    monkeypatch.setattr(engine.budget, "refresh", lambda: None)
    for port in (9, 10):
        engine.set_sfp(port, {"mode": "color", "effect": effect, "color": "blue", "period": 6.1})
    start = clock.now
    writes = []
    samples = []
    original = engine.compose

    def compose():
        samples.append(clock.now)
        return original()

    monkeypatch.setattr(engine, "compose", compose)

    async def record(event):
        writes.append((clock.now, event))
        if clock.now - start >= 5:
            raise asyncio.CancelledError

    transport.hook = record
    with pytest.raises(asyncio.CancelledError):
        await engine._render_loop()
    for port in (9, 10):
        times = [t for t, event in writes if event[:2] == ("sfp_pwm", port) and t > start + 1]
        gaps = [b-a for a, b in zip(times, times[1:], strict=False)]
        # Exclude the pulse's repeated near-zero code. At least 95% of updates meet the 20ms cadence.
        assert sorted(gaps)[int(len(gaps) * .95)] < .022
        assert len(times) > 175
    assert min(b-a for a, b in zip(samples, samples[1:], strict=False)) >= 1/25 - 1e-7
    assert min(b[0]-a[0] for a, b in zip(writes, writes[1:], strict=False)) >= 1/200 - 1e-7
    # Regular copper refreshes still reach all eight ports.
    assert all(sum(event[:2] == ("pwm", port) for _, event in writes) >= 3 for port in range(1, 9))


@pytest.mark.anyio
async def test_stopping_sfp_animation_restores_configured_frame_cadence(monkeypatch):
    clock = Clock()
    monkeypatch.setattr(timing, "monotonic", lambda: clock.now)
    switch, _ = await ready(SfpTransport())
    engine = Engine(switch, rules=[], fps=10)
    engine.set_sfp(9, {"mode": "color", "effect": "pulse"})
    assert engine._sfp_animating()
    for changes in ({"effect": "steady"}, {"effect": "pulse", "mode": "off"}, {"mode": "unmanaged"}):
        engine.set_sfp(9, changes)
        assert not engine._sfp_animating()
    engine.set_sfp(9, {"mode": "color", "effect": "pulse"})
    engine.mode = "released"
    assert not engine._sfp_animating()
