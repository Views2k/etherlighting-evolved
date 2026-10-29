"""Talking to the Etherlighting MCU on the switch.

Only one LED command is used: `ubus call etherlight.mcu set '{"port_rgb":"<port> <rrggbb> <level>"}'`.
The switch's own Etherlighting service (etherlightd) re-applies its colors every couple of seconds, so every
port is re-sent at least once per `keepalive` interval; in between, only ports whose color changed are sent.

Deliberately never used, because they have been reported to corrupt the LED controller's state or break LED
output: `led_stop`, `reset`, `mode_color`, `led_mode`, `port_pwm`.
"""

from __future__ import annotations

import time
from dataclasses import dataclass, field

from .color import BLACK, WHITE, Color
from .links import PortLink, parse_port_show
from .poe import PoePort, parse_poe_show
from .transport import Transport


@dataclass(frozen=True, slots=True)
class PortOutput:
    """What is sent to one port: a color and a hardware brightness level 0-100."""

    color: Color
    level: int

    @classmethod
    def off(cls) -> PortOutput:
        return cls(BLACK, 0)

    def preview(self) -> Color:
        """Approximation of what the LED looks like, for UIs."""
        return self.color.scaled(self.level / 100)


def port_rgb_command(port: int, color: Color, level: int) -> str:
    if not 1 <= int(port) <= 64:
        raise ValueError(f"port {port} out of range")
    level = max(0, min(100, int(level)))
    payload = f'{{"port_rgb":"{int(port)} {color.to_hex()[1:]} {level}"}}'
    return f"ubus call etherlight.mcu set '{payload}' >/dev/null 2>&1"


@dataclass
class SwitchStats:
    writes: int = 0
    commands: int = 0
    last_write_ms: float = 0.0
    avg_write_ms: float = 0.0
    errors: int = 0
    extra: dict = field(default_factory=dict)


class EtherlightSwitch:
    def __init__(self, transport: Transport, *, num_ports: int = 8, keepalive: float = 1.5) -> None:
        self.transport = transport
        self.num_ports = num_ports
        self.keepalive = keepalive
        self.stats = SwitchStats()
        self._last: dict[int, PortOutput] = {}
        self._last_full = 0.0
        self._has_poe: bool | None = None  # learned from the first `swctrl poe show`

    @property
    def connected(self) -> bool:
        return self.transport.connected

    async def connect(self) -> None:
        if not self.transport.connected:
            await self.transport.connect()
        self.forget()

    async def close(self) -> None:
        await self.transport.close()

    async def probe(self) -> dict:
        """Identify the switch and confirm it exposes the per-port RGB command."""
        out = await self.transport.run("hostname; ubus -v list etherlight.mcu 2>/dev/null", timeout=10)
        lines = out.splitlines()
        return {
            "hostname": lines[0].strip() if lines else "",
            "etherlight_mcu": "etherlight.mcu" in out,
            "port_rgb": '"port_rgb"' in out,
        }

    def forget(self) -> None:
        """Drop the memory of what was last sent, so the next write re-sends every port."""
        self._last.clear()
        self._last_full = 0.0

    async def write(self, outputs: list[PortOutput], *, force: bool = False) -> int:
        """Send a frame. Returns how many port commands went out."""
        if len(outputs) != self.num_ports:
            raise ValueError(f"expected {self.num_ports} port outputs, got {len(outputs)}")
        now = time.monotonic()
        full = force or (now - self._last_full) >= self.keepalive
        commands = [
            port_rgb_command(port, out.color, out.level)
            for port, out in enumerate(outputs, start=1)
            if full or self._last.get(port) != out
        ]
        if commands:
            started = time.perf_counter()
            await self.transport.run("\n".join(commands), channel="leds")
            ms = (time.perf_counter() - started) * 1000
            s = self.stats
            s.writes += 1
            s.commands += len(commands)
            s.last_write_ms = ms
            s.avg_write_ms = ms if s.writes == 1 else s.avg_write_ms * 0.9 + ms * 0.1
        for port, out in enumerate(outputs, start=1):
            self._last[port] = out
        if full:
            self._last_full = now
        return len(commands)

    async def set_port(self, port: int, color: Color, level: int = 100) -> None:
        """One-off write of a single port (no keepalive: etherlightd takes it back within seconds)."""
        await self.transport.run(port_rgb_command(port, color, level), channel="leds")

    async def read_links(self) -> dict[int, PortLink]:
        out = await self.transport.run("swctrl port show", channel="links", timeout=10)
        return parse_port_show(out)

    async def read_poe(self) -> dict[int, PoePort]:
        """PoE status per port. Empty on switches (or firmware) without PoE or with an unfamiliar format."""
        if self._has_poe is False:
            return {}
        out = await self.transport.run("swctrl poe show 2>/dev/null", channel="links", timeout=10)
        ports = parse_poe_show(out)
        if self._has_poe is None:
            self._has_poe = bool(ports)
        return ports

    async def benchmark(self, count: int = 20, port: int = 1, color: Color = WHITE) -> dict:
        """Measure round-trip time of single and batched port commands (writes `color` to `port`)."""
        cmd = port_rgb_command(port, color, 100)
        started = time.perf_counter()
        for _ in range(count):
            await self.transport.run(cmd, channel="leds")
        single = (time.perf_counter() - started) / count * 1000
        started = time.perf_counter()
        await self.transport.run("\n".join([cmd] * count), channel="leds")
        batched = (time.perf_counter() - started) / count * 1000
        frame_ms = batched * self.num_ports
        return {
            "single_command_ms": round(single, 2),
            "batched_per_command_ms": round(batched, 2),
            "full_frame_ms": round(frame_ms, 2),
            "max_full_frame_fps": round(1000 / frame_ms, 1) if frame_ms else None,
        }
