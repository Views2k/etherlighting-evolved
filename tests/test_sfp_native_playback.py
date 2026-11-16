"""Native SFP scheduling and cleanup with a verified in-memory transport."""

import asyncio
from dataclasses import replace

import pytest
from test_native_switch import Clock, frame, ready
from test_sfp_precision_transport import SfpPrecisionTransport

from etherlighting_evolved import timing
from etherlighting_evolved.color import Color
from etherlighting_evolved.engine import Engine, to_output
from etherlighting_evolved.sfp import SfpLight
from etherlighting_evolved.switch import PortOutput, sfp_preparation_lead
from etherlighting_evolved.transport import TransportError


@pytest.fixture
def clock(monkeypatch):
    value = Clock()
    monkeypatch.setattr(timing, "monotonic", lambda: value.now)
    monkeypatch.setattr(timing, "wait", value.wait)
    return value


async def setup(*, effect="two_color", period=6, together=True, **options):
    switch, transport = await ready(SfpPrecisionTransport())
    engine = Engine(switch, rules=[], transition=0, **options)
    engine.connected = True
    engine.set_sfp(9, {"mode": "color", "effect": effect, "period": period,
                       "color": "#80c0ff", "secondary_color": "#ef78c7"})
    if together:
        engine.set_sfp_sync({"mode": "together"})
    return engine, switch, transport


async def write(engine, switch, **options):
    outputs = engine.sfp_outputs()
    await switch.write([PortOutput.off()] * 8, sfp=outputs,
                       yield_at=engine._sfp_playback.next_deadline(
                           switch, engine.budget.value), **options)
    return outputs


@pytest.mark.parametrize("effect,period,duration", [
    ("pulse", 2, 1), ("pulse", 4, 2), ("pulse", 6, 3),
    ("two_color", 2, 1), ("two_color", 4, 2), ("two_color", 6, 3),
    ("color_cycle", 6, 1), ("color_cycle", 12, 2), ("color_cycle", 18, 3),
    ("pulse", 8, None), ("two_color", 6.1, None), ("color_cycle", 10, None),
])
def test_supported_duration_and_custom_period_preservation(effect, period, duration):
    light = SfpLight().updated({"mode": "color", "effect": effect, "period": period})
    assert light.native_duration == duration
    assert SfpLight().updated(light.to_dict()).period == period


@pytest.mark.anyio
async def test_native_capability_and_playback_metadata_are_independent_of_current_mode(clock):
    engine, switch, _ = await setup()
    assert (await switch.probe())["sfp_native_gradient"] is True
    assert engine.sfp_state()[0]["playback"] == {
        "kind": "native", "periods": [2, 4, 6], "segment_seconds": 3}
    engine.set_sfp(9, {"period": 7.5})
    saved = engine.snapshot()
    assert engine.sfp_state()[0]["playback"]["kind"] == "custom"
    engine.apply_snapshot(saved)
    assert engine.sfp[9].period == 7.5
    engine.set_sfp(9, {"mode": "off"})
    assert engine.sfp_state()[0]["playback"]["kind"] == "steady"
    assert switch.sfp_native_gradient
    switch.transport.lighting_generation = object()
    assert not switch.sfp_native_gradient


@pytest.mark.anyio
async def test_pair_enrollment_barrier_and_keepalive_do_not_restart_segments(clock):
    engine, switch, transport = await setup()
    baseline = engine.sfp_outputs()
    assert all(output.gradient is None for output in baseline.values())
    assert engine.sfp_outputs() == baseline  # Sampling cannot advance unacknowledged targets.
    await write(engine, switch)
    transport.events.clear()
    segments = await write(engine, switch)
    assert transport.events == [("behavior", "gradient_3s"),
                                ("sfp_pwm", 9, segments[9].precise_rgb),
                                ("sfp_pwm", 10, segments[10].precise_rgb),
                                ("behavior", "steady")]
    first, second = (switch.sfp_transition(port) for port in (9, 10))
    assert second.started_at - first.started_at == pytest.approx(1 / 30)
    transport.events.clear()
    for age in (0.1, 1.7, 2.8):
        clock.now = second.started_at + age
        assert await write(engine, switch, force=True) == segments
        assert not any(event[0] in ("behavior", "sfp_pwm") for event in transport.events)
        transport.events.clear()
    clock.now = first.finishes_at - sfp_preparation_lead(engine.budget.value) + .001
    assert engine.sfp_outputs() != segments  # The next target may be prepared, but cannot be sent early.
    attempts = []

    async def record(event):
        if event[0] == "sfp_pwm":
            attempts.append((event[1], clock.now))

    transport.hook = record
    following = await write(engine, switch)
    assert attempts[0][1] >= first.finishes_at
    assert attempts[1][1] >= second.finishes_at
    assert following[9].transition_id > segments[9].transition_id
    assert following[9] == following[10]
    assert switch.sfp_transition(10).started_at - switch.sfp_transition(9).started_at == pytest.approx(1 / 30)


@pytest.mark.anyio
async def test_native_display_is_encoded_rgb16_interpolation_with_one_gamma(clock):
    engine, switch, _ = await setup(gamma=2.2, brightness=80)
    engine.set_sfp(9, {"brightness": 50, "color_brightness": 80, "secondary_brightness": 40})
    await write(engine, switch)
    await write(engine, switch)
    record = switch.sfp_transition(9)
    expected_start = to_output(Color.parse("#80c0ff").scaled(.8), 40, gamma=2.2, precise=True)
    expected_end = to_output(Color.parse("#ef78c7").scaled(.4), 40, gamma=2.2, precise=True)
    assert record.start_rgb == expected_start.precise_rgb
    assert record.output.precise_rgb == expected_end.precise_rgb
    clock.now = record.started_at + 1.5
    expected = [(a + b) / 2 for a, b in zip(record.start_rgb, record.output.precise_rgb, strict=True)]
    assert switch.sfp_rgb16(9) == pytest.approx(expected)
    state = engine.sfp_state()[0]
    assert state["display_rgb"] == pytest.approx([255 * (value / 65280) ** (1 / 2.2) for value in expected])
    assert state["native_transition"]["elapsed"] == 1.5


@pytest.mark.anyio
async def test_read_during_pending_ack_retains_last_confirmed_native_color(clock):
    engine, switch, transport = await setup()
    await write(engine, switch)
    await write(engine, switch)
    clock.now = max(switch.sfp_transition(port).finishes_at for port in (9, 10))
    before = switch.sfp_rgb16(9)
    entered, proceed = asyncio.Event(), asyncio.Event()

    async def block(event):
        if event[:2] == ("sfp_pwm", 9):
            entered.set()
            await proceed.wait()

    transport.hook = block
    task = asyncio.create_task(write(engine, switch))
    await asyncio.wait_for(entered.wait(), 1)
    try:
        assert switch.sfp_status(9)["state"] == "pending"
        assert switch.sfp_rgb16(9) == before
        assert any(engine.sfp_state()[0]["display_rgb"])
    finally:
        proceed.set()
        await task


@pytest.mark.anyio
@pytest.mark.parametrize("changes", [{"mode": "off"}, {"effect": "steady", "color": "red"}])
async def test_off_and_manual_targets_stop_native_transition_without_waiting(clock, changes):
    engine, switch, transport = await setup()
    await write(engine, switch)
    await write(engine, switch)
    ends = switch.sfp_transition(10).finishes_at
    engine.set_sfp(9, changes)
    transport.events.clear()
    await write(engine, switch)
    assert clock.now < ends
    assert all(switch.sfp_transition(port) is None for port in (9, 10))
    assert all(event[0] != "behavior" for event in transport.events)
    assert [event[1] for event in transport.events if event[0] == "rgb"] == [9, 10]


@pytest.mark.anyio
async def test_explicit_rule_and_same_id_edit_override_native_then_restore_current_settings(clock):
    engine, switch, transport = await setup(gamma=1)
    await write(engine, switch)
    await write(engine, switch)
    rule = engine.add_rule({"name": "Alert", "ports": [10], "conditions": [],
                            "look": {"color": "red", "brightness": 80}})
    outputs = await write(engine, switch)
    assert outputs[10].gradient is None and outputs[10].color == Color(204, 0, 0)
    assert switch.sfp_transition(10) is None
    engine.update_rule(rule.id, {"look": {"color": "green", "brightness": 20}})
    outputs = await write(engine, switch)
    assert outputs[10].color == Color(0, 51, 0)
    engine.delete_rule(rule.id)
    await write(engine, switch)
    outputs = await write(engine, switch)
    assert outputs[9] == outputs[10] and outputs[10].gradient == "gradient_3s"
    assert not transport.failure


@pytest.mark.anyio
async def test_follow_sampling_waits_for_segment_and_does_not_double_brightness(clock):
    engine, switch, _ = await setup(gamma=2.2, brightness=37)
    engine.set_effect("static", {"color": "#c08040", "brightness_color": 70})
    engine.set_sfp_sync({"mode": "ethernet", "source_port": 4})
    engine.compose(t=0)
    await write(engine, switch)
    first = await write(engine, switch)
    expected = to_output(*engine._shown[3], engine.gamma, precise=True)
    assert first[9].precise_rgb == expected.precise_rgb
    assert first[9].gradient == "gradient_1s"
    engine.set_override(4, "blue", brightness=81)
    engine.compose(t=0.1)
    assert engine.sfp_outputs() == first
    clock.now = max(switch.sfp_transition(port).finishes_at for port in (9, 10))
    second = await write(engine, switch)
    assert second[9].precise_rgb == to_output(Color.parse("blue"), 81, gamma=2.2, precise=True).precise_rgb
    assert second[9].transition_id > first[9].transition_id


@pytest.mark.anyio
async def test_release_clears_transitions_only_after_black_and_status_ack(clock):
    engine, switch, transport = await setup()
    await write(engine, switch)
    await write(engine, switch)
    await switch.write(frame(native_ports=(1, 8)), sfp=engine.sfp_outputs())
    active = dict(switch._native_active)
    switch.forget()
    assert set(switch._sfp_transitions) == {9, 10}
    transport.events.clear()
    await switch.quiesce_sfp()
    assert not switch._sfp_transitions and not switch._sfp_owned
    assert switch._native_active == active
    assert transport.events == [("rgb", 9, "000000", 100), ("mode", 9, False),
                                ("rgb", 10, "000000", 100), ("mode", 10, False)]


@pytest.mark.anyio
@pytest.mark.parametrize("fault", ["select", "first", "second", "cleanup", "generation", "cancel"])
async def test_failed_segment_never_replays_and_cleanup_is_guarded(clock, fault):
    engine, switch, transport = await setup()
    await write(engine, switch)
    outputs = engine.sfp_outputs()
    transport.events.clear()
    failure = {"select": ("behavior", "gradient_3s"),
               "first": ("sfp_pwm", 9, outputs[9].precise_rgb),
               "second": ("sfp_pwm", 10, outputs[10].precise_rgb),
               "cleanup": ("behavior", "steady")}.get(fault)
    transport.failure = failure

    async def interrupt(event):
        if event[:2] == ("sfp_pwm", 9):
            if fault == "generation":
                transport.lighting_generation = object()
            if fault == "cancel":
                raise asyncio.CancelledError

    transport.hook = interrupt
    expected = asyncio.CancelledError if fault == "cancel" else TransportError
    with pytest.raises(expected):
        await switch.write([PortOutput.off()] * 8, sfp=outputs)
    assert transport.events.count(("behavior", "gradient_3s")) == 1
    assert sum(event[:2] == ("sfp_pwm", 9) for event in transport.events) <= 1
    assert sum(event[:2] == ("sfp_pwm", 10) for event in transport.events) <= 1
    assert not switch.sfp_acknowledged(9, outputs[9])
    assert not switch.sfp_acknowledged(10, outputs[10])
    assert not switch._sfp_pending
    if fault in ("cleanup", "generation"):
        assert switch.native_uncertain
    elif fault != "generation":
        assert transport.events[-1] == ("behavior", "steady")


@pytest.mark.anyio
async def test_normal_output_halt_keeps_reserved_selector_cleanup(clock):
    engine, switch, transport = await setup()
    await write(engine, switch)
    halt = asyncio.Event()

    async def stop(event):
        if event[:2] == ("sfp_pwm", 9):
            halt.set()

    transport.hook = stop
    transport.events.clear()
    await write(engine, switch, stop=halt)
    assert [event[1] for event in transport.events if event[0] == "sfp_pwm"] == [9]
    assert transport.events[-1] == ("behavior", "steady")
    assert not switch.native_uncertain


@pytest.mark.anyio
@pytest.mark.parametrize("change", [
    {"gradient": "gradient_4s"}, {"breath": "breath_1s"}, {"transition_id": 0},
    {"transition_id": True}, {"level": 50}, {"precise_rgb": None},
])
async def test_invalid_segment_rejected_before_any_write(clock, change):
    engine, switch, transport = await setup()
    await write(engine, switch)
    output = engine.sfp_outputs()[9]
    transport.events.clear()
    with pytest.raises((ValueError, TransportError)):
        await switch.write([PortOutput.off()] * 8, sfp={9: replace(output, **change)})
    assert not transport.events


@pytest.mark.anyio
async def test_gradients_cannot_target_copper_or_survive_revoked_capability(clock):
    engine, switch, transport = await setup()
    await write(engine, switch)
    output = engine.sfp_outputs()[9]
    transport.events.clear()
    with pytest.raises(ValueError):
        await switch.write([output] * 8)
    transport.lighting_generation = object()
    with pytest.raises((ValueError, TransportError)):
        await switch.write([PortOutput.off()] * 8, sfp={9: output})
    assert not transport.events


@pytest.mark.anyio
async def test_render_loop_keeps_pair_barriers_under_changing_copper_load(clock, monkeypatch):
    engine, switch, transport = await setup(effect="color_cycle", period=6)
    engine.set_effect("rainbow", {"speed": 1})
    engine.budget.set(30)
    monkeypatch.setattr(engine.budget, "refresh", lambda: None)
    start = clock.now
    events, segments = [], []
    original = engine.compose

    def changing():
        # Exercise the real renderer with eight changed copper targets every frame.
        colors, outputs = original()
        value = int((clock.now - start) * 1000) % 60000 + 1000
        return colors, [PortOutput(Color(0, 0, round(value / 256)), 100, (0, 0, value))] * 8

    monkeypatch.setattr(engine, "compose", changing)

    async def record(event):
        events.append((clock.now, event))
        clock.now += 0.012
        if event[0] == "sfp_pwm":
            segments.append((clock.now, event[1]))
        if clock.now - start > 9:
            raise asyncio.CancelledError

    transport.hook = record
    with pytest.raises(asyncio.CancelledError):
        await engine._render_loop()
    for port in (9, 10):
        # Discard acquisition baseline; each following target is a complete one-second segment.
        times = [at for at, p in segments if p == port][1:]
        assert len(times) >= 5
        gaps = [b-a for a, b in zip(times, times[1:], strict=False)]
        assert min(gaps) >= 1
        assert max(gaps) < 1.013  # Selector preparation leaves only the simulated 12 ms ACK allowance.
    pairs = list(zip([at for at, p in segments if p == 9][1:],
                     [at for at, p in segments if p == 10][1:], strict=False))
    assert all(0 < second-first < 0.035 for first, second in pairs)
    times = [at for at, _ in events]
    assert min(b-a for a, b in zip(times, times[1:], strict=False)) >= 1 / 30 - 1e-7
    assert all(any(event[:2] == ("pwm", port) for _, event in events) for port in range(1, 9))


@pytest.mark.anyio
@pytest.mark.parametrize("interruption", ["halt", "off", "rule", "generation", "cancel"])
async def test_preselected_wait_honors_changes_and_restores_selector(clock, monkeypatch, interruption):
    engine, switch, transport = await setup()
    await write(engine, switch)
    await write(engine, switch)
    old = dict(switch._sfp_transitions)
    clock.now = old[9].finishes_at - sfp_preparation_lead(engine.budget.value) + .001
    selected = False
    original_wait = timing.wait
    original_stop = engine._output_halt

    async def record(event):
        nonlocal selected
        if event == ("behavior", "gradient_3s"):
            selected = True

    async def interrupt(delay, stop=None):
        nonlocal selected
        if selected and delay > 0:
            selected = False
            if interruption == "off":
                engine.set_sfp(9, {"mode": "off"})
            elif interruption == "rule":
                engine.add_rule({"name": "Alert", "ports": [9, 10], "conditions": [],
                                 "look": {"color": "red"}})
            elif interruption == "generation":
                transport.lighting_generation = object()
            elif interruption == "cancel":
                raise asyncio.CancelledError
            else:
                original_stop.set()
        await original_wait(delay, stop)

    monkeypatch.setattr(timing, "wait", interrupt)
    transport.hook = record
    transport.events.clear()
    if interruption in ("generation", "cancel"):
        with pytest.raises(TransportError if interruption == "generation" else asyncio.CancelledError):
            await write(engine, switch, stop=original_stop)
    else:
        await write(engine, switch, stop=original_stop)
    assert not any(event[0] == "sfp_pwm" for event in transport.events)
    assert switch._sfp_transitions == old
    assert not switch._sfp_pending
    if interruption == "generation":
        assert switch.native_uncertain
        assert transport.events == [("behavior", "gradient_3s")]
    else:
        assert transport.events == [("behavior", "gradient_3s"), ("behavior", "steady")]


@pytest.mark.anyio
async def test_pair_skew_recovers_after_one_slow_ack_without_early_retarget(clock):
    engine, switch, transport = await setup(effect="color_cycle")
    await write(engine, switch)
    await write(engine, switch)
    pairs, early = [], []
    _step = 0

    async def record(event):
        if event[0] != "sfp_pwm":
            return
        prior = switch.sfp_transition(event[1])
        early.append(clock.now < prior.finishes_at)
        clock.now += 0.19 if _step == 1 and event[1] == 10 else 0.012

    transport.hook = record
    for _step in range(5):
        records = [switch.sfp_transition(port) for port in (9, 10)]
        clock.now = min(record.finishes_at for record in records) - sfp_preparation_lead(engine.budget.value)
        # The skew guard can defer preparation until the pair's corrected scheduling window.
        for _ in range(30):
            prior_ids = [record.output.transition_id for record in records]
            await write(engine, switch)
            current = [switch.sfp_transition(port) for port in (9, 10)]
            if all(record.output.transition_id > previous for record, previous in zip(current, prior_ids, strict=True)):
                break
            clock.now += .02
        else:
            pytest.fail("pair did not reach its next boundary")
        pairs.append(current[1].started_at - current[0].started_at)
    assert not any(early)
    assert pairs[0] == pytest.approx(1 / 30)
    assert pairs[1] > .19
    assert pairs[2:] == pytest.approx([1 / 30] * 3)


@pytest.mark.anyio
async def test_constant_follow_finishes_before_retiring_to_steady(clock):
    engine, switch, transport = await setup(gamma=1)
    engine.set_effect("static", {"color": "blue"})
    engine.set_sfp_sync({"mode": "ethernet"})
    engine.compose(t=0)
    await write(engine, switch)
    segments = await write(engine, switch)
    ends = {port: switch.sfp_transition(port).finishes_at for port in (9, 10)}
    clock.now = min(ends.values()) - sfp_preparation_lead(engine.budget.value) + .001
    transport.events.clear()
    assert engine.sfp_outputs() == segments
    assert engine._sfp_playback.next_deadline(switch, engine.budget.value) == max(ends.values())
    await write(engine, switch)
    assert not any(event[0] in ("sfp_pwm", "behavior") for event in transport.events)
    assert set(switch._sfp_transitions) == {9, 10}
    clock.now = max(ends.values())
    settled = await write(engine, switch)
    assert all(output.gradient is None for output in settled.values())
    assert not switch._sfp_transitions
    assert engine._sfp_playback.next_deadline(switch, engine.budget.value) is None


@pytest.mark.anyio
async def test_slow_pair_ack_does_not_starve_copper_before_corrected_preparation(clock, monkeypatch):
    engine, switch, transport = await setup(effect="color_cycle")
    engine.budget.set(30)
    monkeypatch.setattr(engine.budget, "refresh", lambda: None)
    start = clock.now
    old_compose = engine.compose
    old_wait = timing.wait
    writes, windows = [], []
    targets = 0

    async def bounded_wait(delay, stop=None):
        if clock.now - start > 6 and not switch._sfp_pending:
            raise asyncio.CancelledError
        await old_wait(delay, stop)

    def changing():
        colors, _ = old_compose()
        value = int((clock.now - start) * 1000) % 60000 + 1000
        return colors, [PortOutput(Color(0, 0, round(value / 256)), 100, (0, 0, value))] * 8

    async def record(event):
        nonlocal targets
        writes.append((clock.now, event))
        if event[:2] == ("sfp_pwm", 10):
            targets += 1
        slow = event[:2] == ("sfp_pwm", 10) and targets == 3
        clock.now += .4 if slow else .012
        if slow:
            earliest = switch.sfp_transition(9).finishes_at - sfp_preparation_lead(30)
            corrected = clock.now + 1 - 1 / 30 - sfp_preparation_lead(30)
            windows.append((earliest, corrected))
    monkeypatch.setattr(engine, "compose", changing)
    monkeypatch.setattr(timing, "wait", bounded_wait)
    transport.hook = record
    with pytest.raises(asyncio.CancelledError):
        await engine._render_loop()
    assert len(windows) == 1
    assert engine.connected and engine.error is None
    assert targets >= 5
    earliest, corrected = windows[0]
    assert corrected - earliest > .3
    copper_between = [event for at, event in writes if earliest < at < corrected and event[0] == "pwm"]
    assert len(copper_between) >= 6
