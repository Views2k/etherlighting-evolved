import asyncio
from dataclasses import replace

import pytest

from etherlighting_evolved.bandwidth import UplinkBandwidth
from etherlighting_evolved.engine import Engine
from etherlighting_evolved.hardware import PRO_XG8
from etherlighting_evolved.links import PortLink, parse_port_show
from etherlighting_evolved.switch import EtherlightSwitch
from etherlighting_evolved.transport import PortSampleUnavailable, SimulatedTransport, TransportError


def uplink(port=5, rx=0, tx=0, **changes):
    return replace(PortLink(port, True, True, 10000, True, rx_bytes=rx, tx_bytes=tx, uplink=True), **changes)


def ready_meter():
    meter = UplinkBandwidth(range(1, 11))
    meter.update({5: uplink()}, now=0)
    meter.update({5: uplink(rx=625_000, tx=312_500)}, now=5)
    meter.publish(now=5)
    return meter


def test_directional_rate_uses_actual_elapsed_time_and_not_the_nominal_refresh():
    meter = UplinkBandwidth(range(1, 11))
    meter.update({5: uplink(rx=100, tx=200)}, now=1)
    meter.update({5: uplink(rx=750_100, tx=375_200)}, now=7)
    meter.publish(now=7.25)
    sample = meter.snapshot(now=7.25)
    assert sample == {"status": "ready", "ports": [5], "upload_bps": 500_000.0,
                      "download_bps": 1_000_000.0, "history": [[0, 500_000, 1_000_000]], "sample_period_ms": 6000.0,
                      "sample_age_ms": 250.0, "sequence": 1, "refresh_interval_ms": 5000}


def test_uplink_history_keeps_the_test_burst_after_traffic_returns_to_idle():
    meter = UplinkBandwidth([5])
    rx = tx = 0
    meter.update({5: uplink()}, now=0)
    for t in range(5, 70, 5):
        rx += 625_000 * (900 if t == 10 else 1)
        tx += 625_000 * (300 if t == 15 else 1)
        meter.update({5: uplink(rx=rx, tx=tx)}, now=t)
        meter.publish(now=t)
    sample = meter.snapshot(now=65)
    assert sample["download_bps"] == sample["upload_bps"] == 1_000_000
    assert sample["history"][:2] == [[-55_000, 1_000_000, 900_000_000], [-50_000, 300_000_000, 1_000_000]]
    assert sample["history"][-1] == [0, 1_000_000, 1_000_000]
    assert len(sample["history"]) == 12
    meter.update({5: uplink()}, now=66)
    assert meter.snapshot(now=66)["history"] == []


def test_counter_collection_waits_for_publication_and_preserves_exact_large_counter_deltas():
    meter = UplinkBandwidth([5])
    start = 2 ** 62
    meter.update({5: uplink(rx=start, tx=start)}, now=0)
    meter.update({5: uplink(rx=start + 20, tx=start + 10)}, now=4.9)
    assert meter.snapshot(now=4.9)["status"] == "waiting"
    assert meter.snapshot(now=4.9)["upload_bps"] is None
    meter.update({5: uplink(rx=start + 50, tx=start + 25)}, now=5)
    assert meter.snapshot(now=5)["sequence"] == 0
    meter.publish(now=5)
    assert meter.snapshot(now=5)["download_bps"] == 80
    assert meter.snapshot(now=5)["upload_bps"] == 40
    meter.update({5: uplink(rx=start + 150, tx=start + 125)}, now=6)
    assert meter.snapshot(now=6)["sequence"] == 1
    assert meter.snapshot(now=6)["upload_bps"] == 40


def test_only_unique_active_physical_uplinks_count_including_sfp():
    meter = UplinkBandwidth(range(1, 11))
    first = {3: uplink(3, uplink=False), 5: uplink(5, lag="LAG1"),
             10: uplink(10, lag="LAG1"), 20: uplink(20, lag="LAG1"),
             7: uplink(7, link_up=False)}
    meter.update(first, now=0)
    later = {port: replace(link, rx_bytes=125_000, tx_bytes=62_500) for port, link in first.items()}
    meter.update(later, now=5)
    meter.publish(now=5)
    sample = meter.snapshot(now=5)
    assert sample["ports"] == [5, 10]
    assert sample["download_bps"] == 400_000
    assert sample["upload_bps"] == 200_000


def test_uplink_membership_change_never_reuses_the_previous_ports_rate():
    meter = ready_meter()
    meter.update({5: uplink(uplink=False), 10: uplink(10, rx=900_000)}, now=6)
    sample = meter.snapshot(now=6)
    assert sample["ports"] == [10] and sample["status"] == "waiting"
    assert sample["download_bps"] is None
    meter.update({10: uplink(10, rx=1_025_000)}, now=11)
    meter.publish(now=11)
    assert meter.snapshot(now=11)["download_bps"] == 200_000


@pytest.mark.parametrize("direction", ["rx_bytes", "tx_bytes"])
def test_reset_of_either_counter_invalidates_the_complete_interval(direction):
    meter = ready_meter()
    link = replace(uplink(rx=700_000, tx=400_000), **{direction: 1})
    meter.update({5: link}, now=6)
    assert meter.snapshot(now=6)["status"] == "waiting"
    assert meter.snapshot(now=6)["upload_bps"] is None
    meter.update({5: replace(link, rx_bytes=link.rx_bytes + 125, tx_bytes=link.tx_bytes + 250)}, now=11)
    meter.publish(now=11)
    assert meter.snapshot(now=11)["upload_bps"] == 400


def test_no_uplink_is_distinct_from_missing_or_malformed_counters():
    meter = ready_meter()
    meter.update({3: uplink(3, uplink=False)}, now=6)
    assert meter.snapshot(now=6)["status"] == "no_uplink"
    assert meter.snapshot(now=6)["ports"] == []
    meter.update({}, now=7)
    assert meter.snapshot(now=7)["status"] == "unavailable"
    invalid = parse_port_show("U 5 U/U 10000F 10 20 J forwarding unknown disabled 0x0")
    assert not invalid[5].counters_valid
    meter.update(invalid, now=8)
    assert meter.snapshot(now=8)["status"] == "unavailable"
    assert meter.snapshot(now=8)["ports"] == [5]
    assert meter.snapshot(now=8)["download_bps"] is None


def test_reported_zero_traffic_is_a_measured_value():
    meter = UplinkBandwidth([5])
    meter.update({5: uplink()}, now=0)
    meter.update({5: uplink()}, now=5)
    meter.publish(now=5)
    assert meter.snapshot(now=5)["status"] == "ready"
    assert meter.snapshot(now=5)["upload_bps"] == 0
    assert meter.snapshot(now=5)["download_bps"] == 0


def test_old_samples_expire_and_long_poll_gaps_require_a_new_baseline():
    meter = ready_meter()
    sample = meter.snapshot(now=15.01)
    assert sample["status"] == "stale"
    assert sample["upload_bps"] is None and sample["sample_age_ms"] == 10010
    meter.update({5: uplink(rx=8_000_000, tx=8_000_000)}, now=16)
    assert meter.snapshot(now=16)["status"] == "waiting"
    meter.update({5: uplink(rx=8_000_000, tx=8_000_000)}, now=21)
    meter.publish(now=21)
    assert meter.snapshot(now=21)["status"] == "ready"
    assert meter.snapshot(now=21)["download_bps"] == 0


def test_an_unfinished_sample_does_not_wait_forever():
    meter = UplinkBandwidth([5])
    meter.update({5: uplink()}, now=0)
    assert meter.snapshot(now=10.01)["status"] == "unavailable"


@pytest.mark.parametrize("next_time", [4, 5])
def test_clock_rewind_or_repeat_starts_a_new_interval(next_time):
    meter = ready_meter()
    meter.update({5: uplink(rx=625_000, tx=312_500)}, now=next_time)
    assert meter.snapshot(now=next_time)["status"] == "waiting"


def test_connection_generation_change_rejects_cross_connection_deltas():
    meter = UplinkBandwidth([5])
    first, second = object(), object()
    meter.update({5: uplink()}, now=0, generation=first)
    meter.update({5: uplink(rx=625_000)}, now=5, generation=first)
    meter.publish(now=5)
    meter.update({5: uplink(rx=50_000_000)}, now=6, generation=second)
    assert meter.snapshot(now=6)["status"] == "waiting"
    meter.update({5: uplink(rx=50_000_000)}, now=11, generation=second)
    meter.publish(now=11)
    assert meter.snapshot(now=11)["download_bps"] == 0
    assert meter.snapshot(now=11)["sequence"] == 2


def test_disconnect_hides_rates_and_reset_removes_the_old_membership():
    meter = ready_meter()
    sample = meter.snapshot(now=6, connected=False)
    assert sample["status"] == "unavailable" and sample["ports"] == []
    assert sample["upload_bps"] is None
    meter.reset()
    assert meter.snapshot(now=6)["status"] == "unavailable"
    assert meter.snapshot(now=6)["sample_age_ms"] is None


def test_publication_without_new_counters_never_creates_a_zero_or_duplicate_sample():
    meter = ready_meter()
    assert not meter.publish(now=10)
    assert meter.snapshot(now=10)["sequence"] == 1
    assert meter.snapshot(now=10)["upload_bps"] == 500_000
    assert not meter.publish(now=20)
    assert meter.snapshot(now=20)["status"] == "stale"
    assert meter.snapshot(now=20)["upload_bps"] is None
    assert meter.snapshot(now=20)["sequence"] == 1


def test_publication_uses_capture_time_not_refresh_time_when_reads_are_sparse():
    meter = UplinkBandwidth([5])
    meter.update({5: uplink()}, now=1)
    assert not meter.publish(now=5)
    meter.update({5: uplink(rx=750_000, tx=375_000)}, now=7)
    assert meter.publish(now=10)
    sample = meter.snapshot(now=10)
    assert sample["sample_period_ms"] == 6000
    assert sample["sample_age_ms"] == 3000
    assert sample["download_bps"] == 1_000_000
    assert sample["upload_bps"] == 500_000
    assert not meter.publish(now=15)
    assert meter.snapshot(now=15)["sequence"] == 1


def test_stale_unpublished_counters_cannot_become_a_fresh_measurement():
    meter = UplinkBandwidth([5])
    meter.update({5: uplink()}, now=0)
    meter.update({5: uplink(rx=100)}, now=1)
    assert not meter.publish(now=12)
    sample = meter.snapshot(now=12)
    assert sample["status"] == "unavailable"
    assert sample["upload_bps"] is None and sample["sequence"] == 0


@pytest.mark.parametrize("change", ["counter_reset", "generation", "membership", "invalid", "disconnect"])
def test_discontinuity_before_publication_discards_cached_counters(change):
    meter = UplinkBandwidth([5, 10])
    generation = object()
    meter.update({5: uplink(rx=1000)}, now=0, generation=generation)
    meter.update({5: uplink(rx=2000)}, now=1, generation=generation)
    if change == "counter_reset":
        meter.update({5: uplink(rx=0)}, now=2, generation=generation)
    elif change == "generation":
        meter.update({5: uplink(rx=3000)}, now=2, generation=object())
    elif change == "membership":
        meter.update({10: uplink(10, rx=3000)}, now=2, generation=generation)
    elif change == "invalid":
        meter.update({5: uplink(rx=3000, counters_valid=False)}, now=2, generation=generation)
    else:
        meter.reset()
    assert not meter.publish(now=5)
    sample = meter.snapshot(now=5)
    assert sample["upload_bps"] is None and sample["sequence"] == 0


def test_engine_publication_stays_on_five_second_deadlines_despite_poll_and_wake_jitter(monkeypatch):
    engine = Engine(EtherlightSwitch(SimulatedTransport(latency=0)))
    engine.connected = True
    engine._subscribers = {object()}
    clock = [0.0]
    polls = iter([0.01, 1.011, 2.009, 3.01, 4.018, 5.025, 6.012, 7.016,
                  8.014, 9.012, 10.024, 11.015, 12.01, 13.011, 14.009])
    pending = [next(polls)]
    publications, waits = [], []

    async def sleep(delay):
        waits.append(delay)
        if len(waits) > 3:
            raise asyncio.CancelledError
        clock[0] += delay + 0.002
        while pending[0] is not None and pending[0] <= clock[0]:
            at = pending[0]
            engine._bandwidth.update({5: uplink(rx=round(at * 125_000), tx=round(at * 62_500))}, now=at)
            pending[0] = next(polls, None)

    monkeypatch.setattr("etherlighting_evolved.engine.timing.monotonic", lambda: clock[0])
    monkeypatch.setattr("etherlighting_evolved.engine.asyncio.sleep", sleep)
    monkeypatch.setattr(engine, "_publish", lambda frame: publications.append((clock[0], frame["uplink_bandwidth"])))
    # Every fixture await completes immediately; no event-loop clock or real sleep is involved.
    with pytest.raises(asyncio.CancelledError):
        engine._bandwidth_loop().send(None)
    assert [at for at, _ in publications] == pytest.approx([5.002, 10.002, 15.002])
    assert [sample["sequence"] for _, sample in publications] == [1, 2, 3]
    assert [sample["sample_period_ms"] for _, sample in publications] == pytest.approx([4008, 4994, 4997])
    assert all(sample["upload_bps"] == pytest.approx(500_000, abs=2) for _, sample in publications)
    assert all(sample["download_bps"] == pytest.approx(1_000_000, abs=2) for _, sample in publications)
    assert len(waits) == 4


def test_delayed_publication_skips_missed_deadlines_without_catchup_bursts(monkeypatch):
    engine = Engine(EtherlightSwitch(SimulatedTransport(latency=0)))
    clock, waits = [0.0], []

    async def sleep(delay):
        waits.append(delay)
        if len(waits) > 1:
            raise asyncio.CancelledError
        clock[0] = 17.0

    monkeypatch.setattr("etherlighting_evolved.engine.timing.monotonic", lambda: clock[0])
    monkeypatch.setattr("etherlighting_evolved.engine.asyncio.sleep", sleep)
    with pytest.raises(asyncio.CancelledError):
        engine._bandwidth_loop().send(None)
    assert waits == [5, 3]


def test_engine_state_and_frames_include_the_same_readonly_measurement(monkeypatch):
    engine = Engine(EtherlightSwitch(SimulatedTransport(latency=0)))
    engine.connected = True
    engine._bandwidth.update({5: uplink()}, now=0)
    engine._bandwidth.update({5: uplink(rx=625_000)}, now=5)
    engine._bandwidth.publish(now=5)
    monkeypatch.setattr("etherlighting_evolved.engine.timing.monotonic", lambda: 5)
    assert engine.state()["uplink_bandwidth"] == engine.frame_message()["uplink_bandwidth"]
    assert engine.state()["uplink_bandwidth"]["download_bps"] == 1_000_000
    engine._lost(TransportError("fixture loss"))
    assert engine.state()["uplink_bandwidth"]["status"] == "unavailable"
    assert engine.state()["uplink_bandwidth"]["ports"] == []


def test_physical_optical_uplink_is_measured_without_lighting_capability():
    switch = EtherlightSwitch(SimulatedTransport(latency=0))
    engine = Engine(switch)
    switch.hardware_profile = PRO_XG8
    engine._validate_switch_topology()
    assert not switch.sfp_ports
    engine._bandwidth.update({10: uplink(10)}, now=0)
    engine._bandwidth.update({10: uplink(10, rx=625_000)}, now=5)
    engine._bandwidth.publish(now=5)
    sample = engine._bandwidth.snapshot(now=5)
    assert sample["ports"] == [10]
    assert sample["download_bps"] == 1_000_000


@pytest.mark.anyio
async def test_missing_link_sample_ages_on_original_generation_and_resumes(monkeypatch):
    engine = Engine(EtherlightSwitch(SimulatedTransport(latency=0)))
    engine.connected = True
    updates = []
    clock = [0.0]

    async def read_links():
        if len(updates) == 1:
            raise PortSampleUnavailable("fixture timeout")
        return {5: uplink(rx=int(clock[0] * 125_000))}

    async def pause(delay):
        assert delay == engine.link_poll
        clock[0] += 3.0
        updates.append(engine.telemetry_state())
        if len(updates) == 3:
            raise asyncio.CancelledError

    async def read_poe():
        return {}

    async def read_cpu():
        return None

    monkeypatch.setattr(engine.switch, "read_links", read_links)
    monkeypatch.setattr(engine.switch, "read_poe", read_poe)
    monkeypatch.setattr(engine.switch, "read_cpu", read_cpu)
    monkeypatch.setattr("etherlighting_evolved.engine.timing.monotonic", lambda: clock[0])
    monkeypatch.setattr("etherlighting_evolved.engine.asyncio.sleep", pause)
    with pytest.raises(asyncio.CancelledError):
        await engine._status_loop()
    assert [sample["sequence"] for sample in updates] == [1, 1, 2]
    assert [sample["stale"] for sample in updates] == [False, True, False]
    assert len({sample["generation"] for sample in updates}) == 1
    assert engine.connected and engine.mode == "running" and engine.error is None


@pytest.mark.anyio
async def test_status_poll_reuses_existing_reads_without_extra_commands(monkeypatch):
    engine = Engine(EtherlightSwitch(SimulatedTransport(latency=0)))
    engine.connected = True
    calls = []
    clock = [0.0]

    async def read_links():
        calls.append("links")
        if calls.count("links") > 2:
            raise asyncio.CancelledError
        return {5: uplink(rx=int(clock[0] * 125_000), tx=int(clock[0] * 62_500))}

    async def read_poe():
        calls.append("poe")
        return {}

    async def read_cpu():
        calls.append("cpu")
        return None

    async def sleep(delay):
        assert delay == engine.link_poll
        clock[0] += 5

    monkeypatch.setattr(engine.switch, "read_links", read_links)
    monkeypatch.setattr(engine.switch, "read_poe", read_poe)
    monkeypatch.setattr(engine.switch, "read_cpu", read_cpu)
    monkeypatch.setattr("etherlighting_evolved.engine.timing.monotonic", lambda: clock[0])
    monkeypatch.setattr("etherlighting_evolved.engine.asyncio.sleep", sleep)
    with pytest.raises(asyncio.CancelledError):
        await engine._status_loop()
    assert calls == ["links", "poe", "cpu", "links", "poe", "cpu", "links"]
    assert engine._bandwidth.snapshot(now=5)["status"] == "waiting"
    engine._bandwidth.publish(now=5)
    assert engine._bandwidth.snapshot(now=5)["upload_bps"] == 500_000
