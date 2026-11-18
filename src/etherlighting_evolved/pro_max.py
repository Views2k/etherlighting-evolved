"""Pro Max packet encoding and bounded, transport-independent register observation.

These objects describe transfers; they do not open a device or grant output qualification.
Controller observations must come from the integration's qualified ownership mechanism.
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field
from enum import Enum
from types import MappingProxyType


@dataclass(frozen=True, slots=True)
class ProMaxLayout:
    key: str
    sysid: str
    board_id: int
    copper_count: int
    optical_count: int

    @property
    def port_count(self) -> int:
        return self.copper_count + self.optical_count

    def record(self, port: int) -> int:
        if PRO_MAX_LAYOUTS.get(self.key) is not self:
            raise ValueError("Unrecognized Pro Max layout")
        if type(port) is not int or not 1 <= port <= self.port_count:
            raise ValueError("Port is outside the Pro Max connector layout")
        return port - 1


PRO_MAX_LAYOUTS = MappingProxyType({layout.key: layout for layout in (
    ProMaxLayout("USPM16", "ed71", 3, 16, 2),
    ProMaxLayout("USPM16P", "ed70", 3, 16, 2),
    ProMaxLayout("USPM24", "ed5c", 2, 24, 2),
    ProMaxLayout("USPM24P", "ed5b", 2, 24, 2),
    ProMaxLayout("USPM48", "ed5e", 1, 48, 4),
    ProMaxLayout("USPM48P", "ed5d", 1, 48, 4),
)})


def resolve_layout(key: str, sysid: str, board_id: int) -> ProMaxLayout:
    layout = PRO_MAX_LAYOUTS.get(key) if isinstance(key, str) else None
    if (layout is None or sysid != layout.sysid or type(board_id) is not int
            or board_id != layout.board_id):
        raise ValueError("Pro Max board identity does not match its connector layout")
    return layout


def crc8(payload: bytes) -> int:
    """CRC-8/SMBUS over the payload, excluding the command byte."""
    if type(payload) is not bytes:
        raise ValueError("A packet payload must be immutable bytes")
    crc = 0
    for byte in payload:
        crc ^= byte
        for _ in range(8):
            crc = ((crc << 1) ^ (0x07 if crc & 0x80 else 0)) & 0xff
    return crc


def _rgbw(value: tuple[int, int, int, int]) -> bytes:
    if (type(value) is not tuple or len(value) != 4
            or any(type(component) is not int or not 0 <= component <= 65535 for component in value)):
        raise ValueError("RGBW must contain four unsigned 16-bit components")
    return b"".join(component.to_bytes(2, "big") for component in value)


def steady_packet(layout: ProMaxLayout, port: int, rgbw: tuple[int, int, int, int], *, apply: bool = True) -> bytes:
    if type(apply) is not bool:
        raise ValueError("Apply must be a boolean")
    payload = bytes((layout.record(port), 8)) + _rgbw(rgbw) + bytes((int(apply),))
    return b"\x20" + payload + bytes((crc8(payload),))


def _mask(layout: ProMaxLayout, ports: tuple[int, ...]) -> bytes:
    if type(ports) is not tuple or not ports or len(ports) > layout.port_count:
        raise ValueError("Choose a nonempty tuple of Pro Max connectors")
    mask = 0
    for port in ports:
        bit = 1 << layout.record(port)
        if mask & bit:
            raise ValueError("A connector appears more than once")
        mask |= bit
    return mask.to_bytes(7, "big")


def scoped_commit_packet(layout: ProMaxLayout, ports: tuple[int, ...]) -> bytes:
    payload = bytes(7) + _mask(layout, ports)
    return b"\x25" + payload + bytes((crc8(payload),))


def fade_packet(layout: ProMaxLayout, ports: tuple[int, ...], *, seconds: int, mode: int = 0) -> bytes:
    """Enroll already-staged targets from steady state in one scoped native fade."""
    if type(seconds) is not int or seconds not in (1, 2, 3):
        raise ValueError("Native fade duration must be 1, 2, or 3 seconds")
    if type(mode) is not int or mode not in (0, 1):
        raise ValueError("Scoped native mode must be 0 or 1")
    payload = bytes((2 + mode, 0x10 | (8 + seconds))) + bytes(5) + _mask(layout, ports)
    return b"\x00" + payload + bytes((crc8(payload),))


def decode_snapshot(layout: ProMaxLayout, port: int, reply: bytes) -> tuple[int, int, int, int]:
    record = layout.record(port)
    if type(reply) is not bytes or len(reply) != 9 or reply[0] != record:
        raise ValueError("Snapshot registers do not identify the requested connector")
    return tuple(int.from_bytes(reply[index:index + 2], "big") for index in (1, 3, 5, 7))


class ProMaxState(Enum):
    READY = "ready"
    VERIFYING = "verifying"
    SNAPSHOT_MATCHED = "snapshot_matched"
    UNCERTAIN = "uncertain"
    REVOKED = "revoked"


@dataclass(frozen=True, slots=True)
class ControllerObservation:
    """An integration's controller revision, sampled after the specified transfer."""

    revision: bytes = field(repr=False)
    after_transfer: int
    sampled_at: float


@dataclass(frozen=True, slots=True)
class ProMaxTransfer:
    sequence: int
    purpose: str
    request: bytes
    read_length: int = 0


def _time(value: float) -> bool:
    return type(value) in (int, float) and math.isfinite(value) and value >= 0


class ProMaxTransaction:
    """Plan one steady write and compare snapshot registers without retransmitting it.

    A driver must verify transfer byte counts and obtain fresh controller observations.
    This planner performs no I/O and cannot admit hardware or acquire controller ownership.
    SNAPSHOT_MATCHED means captured registers match the target. It does not prove
    command execution, steady behavior, controller ownership, or physical LED output.
    Other writers can change cached RGBW bytes without changing the selector echo.
    """

    def __init__(self, layout: ProMaxLayout, port: int, rgbw: tuple[int, int, int, int], *,
                 observation: ControllerObservation, now: float, timeout: float = 0.5,
                 snapshot_attempts: int = 3) -> None:
        self.packet = steady_packet(layout, port, rgbw)
        if not _time(now) or not _time(timeout) or not 0 < timeout <= 5:
            raise ValueError("A transaction needs a finite deadline of at most five seconds")
        if type(snapshot_attempts) is not int or not 1 <= snapshot_attempts <= 10:
            raise ValueError("Snapshot attempts must be between one and ten")
        if (not isinstance(observation, ControllerObservation)
                or type(observation.revision) is not bytes or not 1 <= len(observation.revision) <= 64
                or type(observation.after_transfer) is not int or observation.after_transfer != 0
                or not _time(observation.sampled_at) or observation.sampled_at != now):
            raise ValueError("A transaction needs a current initial controller observation")
        self.layout, self.port, self.target = layout, port, rgbw
        self.state = ProMaxState.READY
        self.reason = ""
        self._revision = observation.revision
        self._last_time = now
        self._deadline = now + timeout
        self._earliest = now
        self._last_completed_at = now
        self._issued_at = now
        self._completed = 0
        self._pending: ProMaxTransfer | None = None
        self._purpose = "write"
        self._remaining = snapshot_attempts

    @property
    def next_at(self) -> float:
        return self._earliest

    @property
    def terminal(self) -> bool:
        return self.state in (ProMaxState.SNAPSHOT_MATCHED, ProMaxState.UNCERTAIN, ProMaxState.REVOKED)

    def _stop(self, state: ProMaxState, reason: str) -> None:
        self.state, self.reason, self._pending = state, reason, None

    def _check(self, observation: ControllerObservation, now: float, after_transfer: int) -> bool:
        if self.terminal:
            return False
        if not _time(now) or now < self._last_time:
            self._stop(ProMaxState.UNCERTAIN, "invalid_clock")
            return False
        self._last_time = now
        if now >= self._deadline:
            self._stop(ProMaxState.UNCERTAIN, "deadline_exceeded")
            return False
        if (not isinstance(observation, ControllerObservation)
                or type(observation.revision) is not bytes or observation.revision != self._revision):
            self._stop(ProMaxState.REVOKED, "controller_changed")
            return False
        if (type(observation.after_transfer) is not int or observation.after_transfer != after_transfer
                or not _time(observation.sampled_at)
                or not self._last_completed_at <= observation.sampled_at <= now
                or now - observation.sampled_at > 0.1):
            self._stop(ProMaxState.UNCERTAIN, "stale_controller_observation")
            return False
        return True

    def next_transfer(self, observation: ControllerObservation, *, now: float) -> ProMaxTransfer | None:
        if not self._check(observation, now, self._completed):
            return None
        if self._pending is not None:
            raise RuntimeError("Complete the pending transfer before requesting another")
        if now < self._earliest:
            return None
        record = self.layout.record(self.port)
        request, length = {
            "write": (self.packet, 0),
            "acceptance": (b"\x52", 2),
            "select": (bytes((0x10, record)), 0),
            "snapshot": (b"\x10", 9),
        }[self._purpose]
        self._pending = ProMaxTransfer(self._completed + 1, self._purpose, request, length)
        self._issued_at = now
        self.state = ProMaxState.VERIFYING
        return self._pending

    def complete(self, transfer: ProMaxTransfer, *, written: int, reply: bytes,
                 observation: ControllerObservation, now: float) -> None:
        if self.terminal:
            return
        if not isinstance(transfer, ProMaxTransfer) or transfer is not self._pending:
            self._stop(ProMaxState.UNCERTAIN, "unexpected_transfer")
            return
        if not self._check(observation, now, transfer.sequence):
            return
        if observation.sampled_at < self._issued_at:
            self._stop(ProMaxState.UNCERTAIN, "stale_controller_observation")
            return
        if (type(written) is not int or written != len(transfer.request)
                or type(reply) is not bytes or len(reply) != transfer.read_length):
            self._stop(ProMaxState.UNCERTAIN, "incomplete_transfer")
            return
        self._pending = None
        self._completed = transfer.sequence
        self._last_completed_at = observation.sampled_at
        if transfer.purpose == "write":
            self._purpose, self._earliest = "acceptance", now + 0.001
        elif transfer.purpose == "acceptance":
            if reply != bytes((self.layout.record(self.port), 0)):
                self._stop(ProMaxState.UNCERTAIN, "acceptance_mismatch")
                return
            self._purpose = "select"
        elif transfer.purpose == "select":
            self._purpose = "snapshot"
        else:
            self._remaining -= 1
            try:
                matched = decode_snapshot(self.layout, self.port, reply) == self.target
            except ValueError:
                matched = False
            if matched:
                self._stop(ProMaxState.SNAPSHOT_MATCHED, "")
            elif not self._remaining:
                self._stop(ProMaxState.UNCERTAIN, "snapshot_not_matched")
            else:
                self._purpose, self._earliest = "select", now + 0.02

    def fail(self) -> None:
        if not self.terminal:
            self._stop(ProMaxState.UNCERTAIN, "transfer_failed")

    def revoke(self) -> None:
        if not self.terminal:
            self._stop(ProMaxState.REVOKED, "controller_changed")
