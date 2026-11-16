"""How many LED commands per second the engine may send, and the animation clock that this rate paces.

Output starts at a conservative rate. The persistent color channel can increase its rate when recent CPU,
acknowledgment latency, and demand support it; shell output stays at thirty commands per second or less.
"""

from __future__ import annotations

import math
from collections.abc import Callable

from . import timing
from .switch import EtherlightSwitch


class CommandBudget:
    MAX_COMMANDS = 200.0
    INITIAL_COMMANDS = 30.0
    BUSY_IDLE = 0.50
    CALM_IDLE = 0.75
    TELEMETRY_AGE = 3.0

    def __init__(self, switch: EtherlightSwitch, *, num_ports: int, fps: float, max_commands: float,
                 on_reduced: Callable[[], None]) -> None:
        """`on_reduced` runs whenever the budget drops, so the frame being written can stop at the old rate."""
        self.switch = switch
        self.num_ports = num_ports
        self.fps = fps
        self.max_commands = max_commands
        self._on_reduced = on_reduced
        self._value = min(max_commands, self.INITIAL_COMMANDS)
        self.cpu_updated_at: float | None = None
        self.healthy_cpu_samples = 0
        self._changed_at = timing.monotonic()
        self._clock_updated_at = timing.monotonic()
        self._animation_time = self._clock_updated_at
        self.last_write_seconds = 0.0
        self.sent: list[tuple[float, int]] = []  # (time, commands) for the last few seconds

    @property
    def value(self) -> float:
        return min(self._value, self.ceiling())

    @property
    def animation_time(self) -> float:
        return self._animation_time

    def ceiling(self) -> float:
        transport_limit = self.MAX_COMMANDS if self.switch.transport.direct_rgb else self.INITIAL_COMMANDS
        active_ports = self.num_ports or len(self.switch.sfp_ports) or 1
        return min(self.max_commands, transport_limit, active_ports * self.fps)

    def animation_clock(self) -> float:
        """Keep motion sampled consistently when the command budget or CPU load slows full frames."""
        tick = timing.monotonic()
        full_frame_rate = min(self.fps, self.value / max(1, self.num_ports))
        if self.last_write_seconds > 0:
            full_frame_rate = min(full_frame_rate, 1 / self.last_write_seconds)
        scale = min(1.0, full_frame_rate / 3.75)
        self._animation_time += max(0.0, tick - self._clock_updated_at) * scale
        self._clock_updated_at = tick
        return self._animation_time

    def set(self, value: float) -> None:
        value = max(min(5.0, self.ceiling()), min(value, self.ceiling()))
        if value == self._value:
            return
        before = self._value
        self.animation_clock()
        self._value = value
        self._changed_at = timing.monotonic()
        self.healthy_cpu_samples = 0
        if value < before:
            self._on_reduced()

    def refresh(self) -> None:
        """Expire ramp evidence and reduce pressure without waiting for another CPU poll."""
        now = timing.monotonic()
        ack = self.switch.ack_latency(now)
        fresh_cpu = self.cpu_updated_at is not None and 0 <= now - self.cpu_updated_at <= self.TELEMETRY_AGE
        fresh_ack = ack["last_at"] is not None and 0 <= now - ack["last_at"] <= self.TELEMETRY_AGE
        if not fresh_cpu or (self.switch.transport.direct_rgb and not fresh_ack):
            self.healthy_cpu_samples = 0
            self.set(min(self.value, self.INITIAL_COMMANDS))
        else:
            self.set(self.value)
        if (self.switch.transport.direct_rgb and fresh_ack and ack["p95"] is not None
                and ack["p95"] > 1 / self.value):
            self.set(self.value / 2)

    def current(self) -> float:
        """Refresh and return the budget; the switch asks for it between the commands of one frame."""
        self.refresh()
        return self.value

    def adjust(self, idle: float | None) -> None:
        """Increase direct output only with fresh headroom, fast acknowledgments, and actual demand."""
        now = timing.monotonic()
        if idle is None or not math.isfinite(idle) or not 0 <= idle <= 1:
            self.cpu_updated_at = None
            self.refresh()
            return
        if self.cpu_updated_at is None or not 0 <= now - self.cpu_updated_at <= self.TELEMETRY_AGE:
            self.healthy_cpu_samples = 0
            self.set(min(self.value, self.INITIAL_COMMANDS))
        self.cpu_updated_at = now
        self.refresh()
        if idle < self.BUSY_IDLE:
            self.healthy_cpu_samples = 0
            self.set(self.value / 2)
            return
        if idle < self.CALM_IDLE:
            self.healthy_cpu_samples = 0
            return
        self.healthy_cpu_samples += 1
        if self.healthy_cpu_samples < 3 or now - self._changed_at < 2:
            return
        proposed = min(self.value + 15, self.ceiling())
        if self.switch.transport.direct_rgb:
            ack = self.switch.ack_latency(now)
            demand = sum(n for at, n in self.sent if 0 <= now - at <= self.TELEMETRY_AGE) / self.TELEMETRY_AGE
            if (ack["count"] < min(16, max(3, int(self.value * 2))) or ack["p95"] is None
                    or ack["p95"] > 0.5 / proposed or demand < self.value * 0.75):
                self.healthy_cpu_samples = 0
                return
        self.set(proposed)

    def record_sent(self, commands: int) -> None:
        finished = timing.monotonic()
        self.sent = [(at, n) for at, n in self.sent if finished - at <= 5]
        self.sent.append((finished, commands))
        self.refresh()

    def sent_rate(self, window: float = 5.0) -> float:
        cutoff = timing.monotonic() - window
        self.sent = [(at, n) for at, n in self.sent if at >= cutoff]
        return sum(n for _, n in self.sent) / window

    def reset(self) -> None:
        """Discard the CPU evidence of a lost connection and return to the initial rate."""
        self.cpu_updated_at = None
        self.healthy_cpu_samples = 0
        self.set(min(self.INITIAL_COMMANDS, self.ceiling()))
