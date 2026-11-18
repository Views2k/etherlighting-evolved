"""Packet bounds and transaction behavior against an independent simulated MCU."""

from dataclasses import replace

import pytest

from etherlighting_evolved.hardware import PROFILES
from etherlighting_evolved.pro_max import (
    PRO_MAX_LAYOUTS,
    ControllerObservation,
    ProMaxState,
    ProMaxTransaction,
    crc8,
    decode_snapshot,
    fade_packet,
    resolve_layout,
    scoped_commit_packet,
    steady_packet,
)

LAYOUTS = tuple(PRO_MAX_LAYOUTS.values())
RGBW = (0x1234, 0xabcd, 0, 0xffff)


def reference_crc(data):
    polynomial = int.from_bytes(data + b"\0", "big")
    while polynomial.bit_length() >= 9:
        polynomial ^= 0x107 << (polynomial.bit_length() - 9)
    return polynomial


def snapshot(record, rgbw):
    return bytes((record,)) + b"".join(value.to_bytes(2, "big") for value in rgbw)


class SimulatedMCU:
    def __init__(self, layout):
        self.layout = layout
        self.current = [(0, 0, 0, 0)] * layout.port_count
        self.behavior = [1] * layout.port_count
        self.staged = {}
        self.selected = 0
        self.ack = bytes((0, 0))
        self.snapshot = snapshot(0, self.current[0])
        self.writes = []
        self.queue = []
        self.drop_commands = False
        self.drain_immediately = True
        self.steal_selection = False

    def run(self, transfer):
        request = transfer.request
        if request[0] == 0x20:
            assert transfer.read_length == 0 and len(request) == 13
            assert reference_crc(request[1:-1]) == request[-1]
            record = request[1]
            assert record < self.layout.port_count and request[2] == 8 and request[11] == 1
            color = tuple(int.from_bytes(request[index:index + 2], "big") for index in (3, 5, 7, 9))
            self.ack = bytes((record, 0))
            self.writes.append(record)
            if not self.drop_commands:
                self.queue.append((record, color))
            if self.drain_immediately:
                self.drain()
            return b""
        if request == b"\x52":
            assert transfer.read_length == 2
            return self.ack
        if request[0] == 0x10 and len(request) == 2:
            assert transfer.read_length == 0
            self.selected = request[1]
            self.snapshot = snapshot(self.selected, self.current[self.selected])
            return b""
        assert request == b"\x10" and transfer.read_length == 9
        if self.steal_selection:
            return snapshot((self.selected + 1) % self.layout.port_count, RGBW)
        return self.snapshot

    def drain(self):
        for record, color in self.queue:
            self.current[record] = color
            self.behavior[record] = 8
        self.queue.clear()

    def stage_selected_color(self, rgbw):
        self.staged[self.selected] = rgbw
        self.snapshot = snapshot(self.selected, rgbw)


class Harness:
    def __init__(self, layout=LAYOUTS[0], port=1, **kwargs):
        self.now = 0.0
        self.revision = b"controller-revision"
        self.completed = 0
        self.mcu = SimulatedMCU(layout)
        self.transaction = ProMaxTransaction(layout, port, RGBW, observation=self.observation(), now=0, **kwargs)
        self.transfers = []

    def observation(self, after_transfer=None):
        sequence = self.completed if after_transfer is None else after_transfer
        return ControllerObservation(self.revision, sequence, self.now)

    def next(self):
        self.now = max(self.now, self.transaction.next_at)
        transfer = self.transaction.next_transfer(self.observation(), now=self.now)
        if transfer is not None:
            self.transfers.append(transfer)
        return transfer

    def complete(self, transfer, reply=None, written=None, observation=None):
        if reply is None:
            reply = self.mcu.run(transfer)
        self.transaction.complete(
            transfer, written=len(transfer.request) if written is None else written, reply=reply,
            observation=self.observation(transfer.sequence) if observation is None else observation, now=self.now,
        )
        self.completed = transfer.sequence

    def step(self):
        transfer = self.next()
        assert transfer is not None
        self.complete(transfer)
        return transfer

    def finish(self):
        for _ in range(30):
            if self.transaction.terminal:
                return
            self.step()
        pytest.fail("Transaction exceeded the simulator step bound")


@pytest.mark.parametrize("layout", LAYOUTS, ids=lambda item: item.key)
def test_connector_identity_and_all_per_connector_packets(layout):
    assert resolve_layout(layout.key, layout.sysid, layout.board_id) is layout
    assert not PROFILES[layout.key].output_verified
    for port in range(1, layout.port_count + 1):
        payload = bytes((port - 1, 8)) + bytes.fromhex("1234abcd0000ffff") + b"\1"
        assert steady_packet(layout, port, RGBW) == b"\x20" + payload + bytes((reference_crc(payload),))
        assert decode_snapshot(layout, port, snapshot(port - 1, RGBW)) == RGBW
        driver = Harness(layout, port)
        driver.finish()
        assert driver.transaction.state is ProMaxState.SNAPSHOT_MATCHED
        assert driver.mcu.writes == [port - 1]
        assert driver.mcu.current[port - 1] == RGBW
        assert all(color == (0, 0, 0, 0) for index, color in enumerate(driver.mcu.current) if index != port - 1)


@pytest.mark.parametrize("layout", LAYOUTS, ids=lambda item: item.key)
def test_mask_never_includes_extra_records_and_preserves_high_bits(layout):
    ports = tuple(range(1, layout.port_count + 1))
    packet = scoped_commit_packet(layout, ports)
    assert packet[0] == 0x25 and len(packet) == 16 and packet[1:8] == bytes(7)
    assert int.from_bytes(packet[8:15], "big") == (1 << layout.port_count) - 1
    assert packet[-1] == reference_crc(packet[1:-1])
    packet = scoped_commit_packet(layout, (layout.port_count,))
    assert int.from_bytes(packet[8:15], "big") == 1 << (layout.port_count - 1)


@pytest.mark.parametrize("port", [False, True, -1, 0, 19, 22, 64, 256, 1.0, "1", None])
def test_invalid_connector_never_reaches_encoder(port):
    layout = LAYOUTS[0]
    with pytest.raises(ValueError):
        steady_packet(layout, port, RGBW)
    with pytest.raises(ValueError):
        scoped_commit_packet(layout, (port,))


@pytest.mark.parametrize("bad", [(), (1,), (1, 2, 3), (0, 0, 0, 0, 0), [0, 0, 0, 0],
                                 (True, 0, 0, 0), (-1, 0, 0, 0), (65536, 0, 0, 0), (0.0, 0, 0, 0)])
def test_invalid_color_rejected(bad):
    with pytest.raises(ValueError):
        steady_packet(LAYOUTS[0], 1, bad)


def test_crc_matches_standard_vector_and_independent_polynomial_division():
    assert crc8(b"123456789") == 0xf4
    for value in range(256):
        data = bytes((value, 8, 0xff, value, 0, 0, 0xff, 0xff, 0, 1, 1))
        assert crc8(data) == reference_crc(data)


def test_foreign_or_modified_layout_and_identity_rejected():
    with pytest.raises(ValueError):
        steady_packet(replace(LAYOUTS[0], copper_count=64), 60, RGBW)
    for identity in [("USPM16", "ed70", 3), ("USPM16", "ed71", 2), ("USPM16", "ed71", 3.0),
                     ("USPXG8P", "ed76", 3), ("USPM48", "ed5e", True)]:
        with pytest.raises(ValueError):
            resolve_layout(*identity)


def test_staging_is_explicit_and_does_not_include_a_commit():
    packet = steady_packet(LAYOUTS[0], 18, RGBW, apply=False)
    assert packet[0] == 0x20 and packet[1] == 17 and packet[11] == 0 and len(packet) == 13
    assert packet[-1] == reference_crc(packet[1:-1])
    for value in (0, 1, None):
        with pytest.raises(ValueError):
            steady_packet(LAYOUTS[0], 1, RGBW, apply=value)


@pytest.mark.parametrize("ports", [(), [], (1, 1), (1, 19)])
def test_empty_duplicate_and_unmapped_masks_rejected(ports):
    with pytest.raises(ValueError):
        scoped_commit_packet(LAYOUTS[0], ports)


@pytest.mark.parametrize("mode", (0, 1))
@pytest.mark.parametrize("seconds", (1, 2, 3))
def test_fade_enrollment_uses_only_scoped_mode_prefixes(mode, seconds):
    packet = fade_packet(LAYOUTS[-1], (1, 52), seconds=seconds, mode=mode)
    assert packet[0:3] == bytes((0, mode + 2, 0x10 | (seconds + 8)))
    assert packet[3:8] == bytes(5)
    assert int.from_bytes(packet[8:15], "big") == (1 << 51) | 1
    assert packet[-1] == reference_crc(packet[1:-1])


@pytest.mark.parametrize("seconds,mode", [(0, 0), (4, 0), (True, 0), (1, True), (1, 2), (1.0, 0)])
def test_fade_rejects_unreviewed_modes_and_duration(seconds, mode):
    with pytest.raises(ValueError):
        fade_packet(LAYOUTS[0], (1,), seconds=seconds, mode=mode)


def test_crc_acceptance_does_not_claim_command_execution():
    driver = Harness()
    driver.mcu.drop_commands = True
    driver.step()
    driver.step()
    assert driver.transaction.state is ProMaxState.VERIFYING
    driver.finish()
    assert driver.transaction.state is ProMaxState.UNCERTAIN
    assert driver.transaction.reason == "snapshot_not_matched"
    assert driver.mcu.writes == [0]
    assert [item.purpose for item in driver.transfers].count("snapshot") == 3


def test_already_matching_snapshot_does_not_prove_dropped_command_executed():
    driver = Harness()
    driver.mcu.current[0] = RGBW
    driver.mcu.drop_commands = True
    driver.finish()
    assert driver.transaction.state is ProMaxState.SNAPSHOT_MATCHED
    assert driver.mcu.behavior[0] == 1
    assert driver.mcu.current[0] == RGBW
    assert not driver.mcu.queue


def test_same_selector_staging_can_match_cache_without_changing_current_color():
    driver = Harness()
    driver.mcu.drop_commands = True
    for _ in range(3):
        driver.step()
    driver.mcu.stage_selected_color(RGBW)
    driver.step()
    assert driver.transaction.state is ProMaxState.SNAPSHOT_MATCHED
    assert driver.mcu.selected == 0
    assert driver.mcu.staged[0] == RGBW
    assert driver.mcu.current[0] == (0, 0, 0, 0)
    assert driver.mcu.behavior[0] == 1


def test_queued_command_can_complete_between_bounded_snapshots():
    driver = Harness()
    driver.mcu.drain_immediately = False
    for _ in range(4):
        driver.step()
    assert driver.transaction.state is ProMaxState.VERIFYING
    driver.mcu.drain()
    driver.finish()
    assert driver.transaction.state is ProMaxState.SNAPSHOT_MATCHED
    assert driver.mcu.writes == [0]


def test_snapshot_is_reselected_instead_of_reading_old_cached_values():
    driver = Harness()
    driver.mcu.steal_selection = True
    for _ in range(4):
        driver.step()
    assert driver.transaction.state is ProMaxState.VERIFYING
    driver.mcu.steal_selection = False
    driver.finish()
    assert driver.transaction.state is ProMaxState.SNAPSHOT_MATCHED
    assert [item.purpose for item in driver.transfers] == [
        "write", "acceptance", "select", "snapshot", "select", "snapshot",
    ]


@pytest.mark.parametrize("stage", range(4))
def test_controller_change_after_any_transfer_revokes_without_restore(stage):
    driver = Harness()
    for _ in range(stage):
        driver.step()
    transfer = driver.next()
    driver.revision = b"new-controller-choice"
    driver.complete(transfer)
    assert driver.transaction.state is ProMaxState.REVOKED
    assert driver.next() is None
    assert len(driver.mcu.writes) == 1


def test_changed_controller_before_first_transfer_yields_no_output():
    driver = Harness()
    driver.revision = b"changed"
    assert driver.next() is None and not driver.mcu.writes
    assert driver.transaction.state is ProMaxState.REVOKED


@pytest.mark.parametrize("stage", range(4))
def test_old_observation_cannot_complete_a_new_transfer(stage):
    driver = Harness()
    for _ in range(stage):
        driver.step()
    old = driver.observation()
    transfer = driver.next()
    driver.complete(transfer, observation=old)
    assert driver.transaction.state is ProMaxState.UNCERTAIN
    assert driver.transaction.reason == "stale_controller_observation"


def test_timestamp_before_dispatch_is_stale_even_with_the_new_sequence():
    driver = Harness()
    driver.now = 0.05
    transfer = driver.next()
    old = ControllerObservation(driver.revision, transfer.sequence, 0)
    driver.complete(transfer, observation=old)
    assert driver.transaction.reason == "stale_controller_observation"


def test_unrefreshed_observation_cannot_start_a_transfer():
    driver = Harness()
    old = driver.observation()
    assert driver.transaction.next_transfer(old, now=0.101) is None
    assert driver.transaction.reason == "stale_controller_observation"


def test_observation_from_the_future_is_rejected():
    driver = Harness()
    future = ControllerObservation(driver.revision, 0, 0.1)
    assert driver.transaction.next_transfer(future, now=0) is None
    assert driver.transaction.reason == "stale_controller_observation"


@pytest.mark.parametrize("written,reply", [(0, b""), (-1, b""), (True, b""), (13, b"extra")])
def test_short_or_ambiguous_write_is_not_retried(written, reply):
    driver = Harness()
    transfer = driver.next()
    driver.complete(transfer, written=written, reply=reply)
    assert driver.transaction.state is ProMaxState.UNCERTAIN
    assert driver.next() is None and len(driver.transfers) == 1


@pytest.mark.parametrize("reply", [b"", b"\x00", b"\x00\x00\x00", b"\x00\x01", b"\x01\x00"])
def test_missing_failed_or_other_record_ack_is_not_a_success(reply):
    driver = Harness()
    driver.step()
    driver.complete(driver.next(), reply=reply)
    assert driver.transaction.state is ProMaxState.UNCERTAIN
    assert driver.next() is None and driver.mcu.writes == [0]


@pytest.mark.parametrize("reply", [bytes(8), bytes(10), bytearray(9)])
def test_incomplete_or_mutable_snapshot_is_not_matched(reply):
    driver = Harness()
    for _ in range(3):
        driver.step()
    driver.complete(driver.next(), reply=reply)
    assert driver.transaction.state is ProMaxState.UNCERTAIN
    assert driver.transaction.reason == "incomplete_transfer"


@pytest.mark.parametrize("stage", range(4))
def test_deadline_expiry_and_clock_rollback_stop_transfer_progress(stage):
    for invalid_now, reason in ((0.5, "deadline_exceeded"), (-1, "invalid_clock"), (float("nan"), "invalid_clock")):
        driver = Harness()
        for _ in range(stage):
            driver.step()
        transfer = driver.next()
        driver.now = invalid_now
        driver.complete(transfer)
        assert driver.transaction.state is ProMaxState.UNCERTAIN
        assert driver.transaction.reason == reason


def test_receipt_from_another_transaction_or_replayed_receipt_is_rejected():
    first, second = Harness(), Harness()
    foreign = first.next()
    second.next()
    second.complete(foreign, reply=b"")
    assert second.transaction.reason == "unexpected_transfer"
    first.complete(foreign)
    first.complete(foreign, reply=b"")
    assert first.transaction.reason == "unexpected_transfer"


def test_duplicate_dispatch_does_not_resend_pending_write():
    driver = Harness()
    driver.next()
    with pytest.raises(RuntimeError):
        driver.next()
    assert len(driver.transfers) == 1


def test_pending_transfer_and_readback_are_paced_without_busy_retries():
    driver = Harness()
    driver.step()
    assert driver.transaction.next_transfer(driver.observation(), now=driver.now) is None
    driver.mcu.steal_selection = True
    for _ in range(3):
        driver.step()
    assert driver.transaction.next_transfer(driver.observation(), now=driver.now) is None
    assert len(driver.transfers) == 4


def test_a_positive_clock_rollback_is_rejected():
    driver = Harness()
    driver.now = 0.1
    transfer = driver.next()
    driver.now = 0.05
    driver.complete(transfer)
    assert driver.transaction.reason == "invalid_clock"


@pytest.mark.parametrize("observation", [None, ControllerObservation(b"", 0, 0),
                                        ControllerObservation(b"x", True, 0), ControllerObservation(b"x", 1, 0),
                                        ControllerObservation(b"x", 0, -1), ControllerObservation(b"x", 0, 0.1)])
def test_missing_or_stale_initial_controller_observation_rejected(observation):
    with pytest.raises(ValueError):
        ProMaxTransaction(LAYOUTS[0], 1, RGBW, observation=observation, now=0)


def test_failure_and_revocation_are_terminal_and_do_not_generate_cleanup_writes():
    for stop, state in (("fail", ProMaxState.UNCERTAIN), ("revoke", ProMaxState.REVOKED)):
        driver = Harness()
        transfer = driver.next()
        getattr(driver.transaction, stop)()
        driver.complete(transfer, reply=b"")
        assert driver.transaction.state is state
        assert driver.next() is None and not driver.mcu.writes


@pytest.mark.parametrize("timeout,attempts", [(0, 3), (6, 3), (float("inf"), 3), (True, 3), (0.5, 0), (0.5, 11),
                                           (0.5, True)])
def test_invalid_transaction_bounds_rejected(timeout, attempts):
    with pytest.raises(ValueError):
        Harness(timeout=timeout, snapshot_attempts=attempts)
