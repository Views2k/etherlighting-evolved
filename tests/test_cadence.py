import asyncio
from types import SimpleNamespace

import pytest

from etherlighting_evolved import engine as engine_module
from etherlighting_evolved.config import EngineConfig, load_config
from etherlighting_evolved.engine import Engine
from etherlighting_evolved.switch import EtherlightSwitch
from etherlighting_evolved.transport import SimulatedTransport, TransportError


class DirectTransport(SimulatedTransport):
    @property
    def direct_rgb(self):
        return True


@pytest.fixture
def cadence(monkeypatch):
    clock = [100.0]
    monkeypatch.setattr(engine_module, "timing",
                        SimpleNamespace(monotonic=lambda: clock[0], wait=engine_module.timing.wait))
    switch = EtherlightSwitch(DirectTransport(latency=0))
    telemetry = {"count": 64, "p95": 0.001, "last_at": clock[0]}
    monkeypatch.setattr(switch, "ack_latency", lambda now=None: dict(telemetry))
    engine = Engine(switch, rules=[], transition=0)
    return engine, clock, telemetry


def healthy_sample(engine, clock, telemetry, *, idle=0.9, demand=1.0):
    clock[0] += 1
    telemetry["last_at"] = clock[0]
    engine._sent = [(clock[0], engine.command_budget * 3 * demand)]
    engine._adjust_throttle(idle)


def test_ceiling_defaults_and_old_explicit_config_remain_distinct(tmp_path):
    assert EngineConfig().max_commands == 200
    path = tmp_path / "old.toml"
    content = "[engine]\nmax_commands = 30\n"
    path.write_text(content, encoding="utf-8")
    assert load_config(path).engine.max_commands == 30
    assert path.read_text(encoding="utf-8") == content


def test_direct_starts_at_thirty_and_ramps_only_after_three_headroom_samples(cadence):
    engine, clock, telemetry = cadence
    assert engine.max_commands == 200 and engine.command_budget == 30
    for expected in [30, 30, 45, 45, 45, 60]:
        healthy_sample(engine, clock, telemetry)
        assert engine.command_budget == expected


@pytest.mark.parametrize("ceiling", [5, 20, 30, 85, 200])
def test_ramp_respects_requested_ceiling(cadence, ceiling):
    engine, clock, telemetry = cadence
    engine.set_output(max_commands=ceiling)
    for _ in range(45):
        healthy_sample(engine, clock, telemetry)
        assert engine.command_budget <= ceiling
    assert engine.command_budget == ceiling


@pytest.mark.parametrize("ports,fps,expected", [(1, 25, 25), (8, 10, 80), (8, 25, 200), (64, 25, 200)])
def test_target_depends_on_frame_rate_and_ports_without_exceeding_two_hundred(cadence, ports, fps, expected):
    engine, clock, telemetry = cadence
    engine.num_ports = ports
    engine.set_fps(fps)
    for _ in range(45):
        healthy_sample(engine, clock, telemetry)
    assert engine.command_budget == expected


@pytest.mark.parametrize("fault", ["cpu", "latency", "count", "demand"])
def test_one_missing_ramp_gate_keeps_the_initial_rate(cadence, fault):
    engine, clock, telemetry = cadence
    if fault == "latency":
        telemetry["p95"] = 0.012  # fast enough for30, without50% headroom at45
    if fault == "count":
        telemetry["count"] = 15
    for _ in range(9):
        healthy_sample(engine, clock, telemetry, idle=0.7 if fault == "cpu" else 0.9,
                       demand=0.7 if fault == "demand" else 1)
    assert engine.command_budget == 30


@pytest.mark.parametrize("missing", ["cpu", "ack", "both"])
def test_expired_feedback_returns_to_thirty_and_discards_ramp_streak(cadence, missing):
    engine, clock, telemetry = cadence
    for _ in range(12):
        healthy_sample(engine, clock, telemetry)
    assert engine.command_budget == 90
    clock[0] += 3.1
    if missing == "cpu":
        telemetry["last_at"] = clock[0]
    if missing == "ack":
        engine._cpu_updated_at = clock[0]
    engine._refresh_budget()
    assert engine.command_budget == 30 and engine._healthy_cpu_samples == 0
    for expected in [30, 30, 45]:
        healthy_sample(engine, clock, telemetry)
        assert engine.command_budget == expected


def test_new_cpu_sample_after_a_gap_does_not_reuse_an_old_streak(cadence):
    engine, clock, telemetry = cadence
    healthy_sample(engine, clock, telemetry)
    healthy_sample(engine, clock, telemetry)
    clock[0] += 4
    healthy_sample(engine, clock, telemetry)
    assert engine.command_budget == 30 and engine._healthy_cpu_samples == 1


@pytest.mark.parametrize("value", [None, -0.01, 1.01, float("nan"), float("inf")])
def test_missing_or_invalid_cpu_is_not_headroom(cadence, value):
    engine, clock, telemetry = cadence
    for _ in range(9):
        healthy_sample(engine, clock, telemetry)
    engine._adjust_throttle(value)
    assert engine.command_budget == 30 and engine._healthy_cpu_samples == 0


def test_busy_feedback_immediately_halves_rate_and_interrupts_pending_frame(cadence):
    engine, clock, telemetry = cadence
    for _ in range(12):
        healthy_sample(engine, clock, telemetry)
    pending_frame_stop = engine._output_halt
    engine._adjust_throttle(0.42)
    assert engine.command_budget == 45
    assert pending_frame_stop.is_set() and not engine._output_halt.is_set()
    for _ in range(5):
        engine._adjust_throttle(0.1)
    assert engine.command_budget == 5


def test_slow_acknowledgment_backs_off_without_waiting_for_a_cpu_poll(cadence):
    engine, clock, telemetry = cadence
    for _ in range(9):
        healthy_sample(engine, clock, telemetry)
    assert engine.command_budget == 75
    telemetry["p95"] = 0.02
    engine._refresh_budget()
    assert engine.command_budget == 37.5


def test_lost_connection_discards_headroom_and_timing_samples(cadence, monkeypatch):
    engine, clock, telemetry = cadence
    for _ in range(9):
        healthy_sample(engine, clock, telemetry)
    resets = []
    monkeypatch.setattr(engine.switch, "reset_timing", lambda: resets.append(True))
    engine._lost(TransportError("fixture connection closed"))
    assert engine.command_budget == 30 and engine._cpu_updated_at is None
    assert engine._healthy_cpu_samples == 0 and resets == [True]


def test_switching_to_shell_immediately_limits_effective_budget(cadence):
    engine, clock, telemetry = cadence
    for _ in range(12):
        healthy_sample(engine, clock, telemetry)
    engine.switch.transport = SimulatedTransport(latency=0)
    assert engine.command_budget == 30
    for _ in range(9):
        healthy_sample(engine, clock, telemetry)
    assert engine.command_budget == 30


def test_budget_change_does_not_reset_or_jump_animation_phase(cadence):
    engine, clock, telemetry = cadence
    engine.set_effect("rainbow")
    healthy_sample(engine, clock, telemetry)
    healthy_sample(engine, clock, telemetry)
    clock[0] += 1
    before = engine.compose()[0]
    telemetry["last_at"] = clock[0]
    engine._sent = [(clock[0], 90)]
    engine._adjust_throttle(0.9)
    assert engine.command_budget == 45 and engine.compose()[0] == before
    engine._adjust_throttle(0.1)
    assert engine.compose()[0] == before


def test_state_exposes_transport_and_fresh_latency_without_device_identifiers(cadence, monkeypatch):
    engine, clock, _ = cadence
    monkeypatch.setattr(engine.switch, "ack_latency", EtherlightSwitch.ack_latency.__get__(engine.switch))
    engine.switch._ack_samples.append((clock[0], 0.002))
    engine._cpu_updated_at = clock[0] - 0.5
    stats = engine.state()["stats"]
    assert stats["direct_rgb"] is True
    assert stats["ack_p95_ms"] == 2.0 and stats["cpu_sample_age_ms"] == 500.0
    clock[0] += 3.1
    assert engine.state()["stats"]["ack_p95_ms"] is None
    engine._cpu_updated_at = None
    assert engine.state()["stats"]["cpu_sample_age_ms"] is None


@pytest.mark.anyio
async def test_render_passes_live_budget_callback_that_stops_a_frame_at_cpu_expiry(cadence, monkeypatch):
    engine, clock, _ = cadence
    engine.connected = True
    engine._command_budget = 45
    engine._cpu_updated_at = clock[0] - 2.99
    done = asyncio.Event()

    async def write(outputs, *, max_commands, stop, budget_getter, yield_at):
        assert max_commands == 45 and not stop.is_set()
        assert yield_at is None
        clock[0] += 0.02
        assert budget_getter() == 30 and stop.is_set()
        done.set()
        return 0

    monkeypatch.setattr(engine.switch, "write", write)
    task = asyncio.create_task(engine._render_loop())
    try:
        await asyncio.wait_for(done.wait(), 1)
    finally:
        task.cancel()
        with pytest.raises(asyncio.CancelledError):
            await task


@pytest.mark.anyio
async def test_render_frame_period_includes_write_time_instead_of_adding_it(monkeypatch):
    switch = EtherlightSwitch(SimulatedTransport(latency=0))
    engine = Engine(switch, fps=25, rules=[], transition=0)
    engine.connected = True
    starts = []
    done = asyncio.Event()

    async def write(outputs, **kwargs):
        starts.append(engine_module.timing.monotonic())
        await asyncio.sleep(0.02)
        switch.stats.attempted_commands += 8
        if len(starts) >= 12:
            done.set()
        return 8

    monkeypatch.setattr(switch, "write", write)
    task = asyncio.create_task(engine._render_loop())
    try:
        await asyncio.wait_for(done.wait(), 2)
    finally:
        task.cancel()
        with pytest.raises(asyncio.CancelledError):
            await task
    periods = [b - a for a, b in zip(starts, starts[1:], strict=False)]
    assert min(periods) >= 0.035
    assert sum(periods) / len(periods) < 0.05  #20ms output is inside, not added to, the40ms frame period
