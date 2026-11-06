"""Talking to the Etherlighting MCU on the switch.

Ordinary output uses per-port RGB. Qualified daemon profiles accept finer RGB components through
their per-port PWM parser; that path requires the matching running executable hash and advertised schema.
The switch's own Etherlighting service (etherlightd) reapplies its colors every couple of seconds, so every
port is re-sent at least once per `keepalive` interval; in between, only ports whose color changed are sent.

Deliberately never used, because they have been reported to corrupt the LED controller's state or break LED
output: `led_stop`, `reset`, `mode_color`, `led_mode`.
"""

from __future__ import annotations

import asyncio
import json
import math
import re
import time
from collections import deque
from collections.abc import Awaitable, Callable
from dataclasses import dataclass, field

from . import timing
from .color import BLACK, WHITE, Color
from .hardware import PRO_XG8, HardwareProfile, identify_profile
from .health import parse_health
from .links import PortLink, parse_port_show
from .ownership import OutputLease, OutputOwnershipError
from .poe import PoePort, PoeSample, parse_poe_sample
from .transport import NativeOutputUncertain, SimulatedTransport, SSHTransport, Transport, TransportError

WRITE_FAILED = "__EE_LED_WRITE_FAILED__"
MCU_SCHEMA_EXIT = "__EE_MCU_SCHEMA_EXIT__"
MCU_SCHEMA_COMMAND = (
    "ubus -v list etherlight.mcu 2>/dev/null\n"
    f"printf '\\n{MCU_SCHEMA_EXIT}%s\\n' \"$?\""
)
HOSTNAME_COMMAND = "hostname 2>/dev/null"
BOARD_LIGHTING_INFO = (
    "grep -E '^board\\.(name|shortname|sysid)=' /etc/board.info 2>/dev/null"
)
PRECISE_DAEMON_HASH = "67fb1ca03ef7d6325dc1eed8628e82c93462c50d52c15e5778a1483bae50a91c"
RUNNING_DAEMON_HASH = (
    'set -- $(pidof etherlightd); [ "$#" -eq 1 ] && sha256sum "/proc/$1/exe" 2>/dev/null'
)
PORT_FIRMWARE_HASH = "cb99c14250ed28c77a00d4df46bf699db6b87f5a1094d0b10cfc79f2d5832a8c"
PORT_FIRMWARE_HASH_COMMAND = "sha256sum /usr/share/firmware/port_led_fw.bin 2>/dev/null"
BREATH_BEHAVIORS = frozenset({"breath_1s", "breath_2s", "breath_3s"})
GRADIENT_BEHAVIORS = frozenset({"gradient_1s", "gradient_2s", "gradient_3s"})


def sfp_preparation_lead(command_budget: float) -> float:
    """Reserve selector pacing and one SFP render tick before a segment boundary."""
    return 2 / command_budget + 0.02


def sfp_lighting_ports(board_info: str, num_ports: int) -> tuple[int, ...]:
    """Expose the two separately controlled SFP LEDs only on the identified Pro XG 8 PoE."""
    return (9, 10) if is_pro_xg8(board_info, num_ports) else ()


def is_pro_xg8(board_info: str, num_ports: int) -> bool:
    fields = {}
    for line in board_info.splitlines():
        key, separator, value = line.partition("=")
        if separator and key in {"board.name", "board.shortname", "board.sysid"}:
            if key in fields:
                return False
            fields[key] = value.strip()
    known = (fields.get("board.shortname") == "USPXG8P"
             or fields.get("board.name") == "USW-Pro-XG-8-PoE")
    sysid = fields.get("board.sysid")
    conflicting = (fields.get("board.shortname") not in (None, "USPXG8P")
                   or fields.get("board.name") not in (None, "USW-Pro-XG-8-PoE"))
    return num_ports == 8 and known and not conflicting and sysid in (None, "0xed76", "ed76")

_UBUS_OBJECT = re.compile(r'''^\s*(['"])([^'"\r\n]+)\1(?:\s+@[0-9a-fA-F]+)?\s*$''', re.MULTILINE)
_UBUS_METHOD = re.compile(r'''\s*(['"])([^'"\r\n]+)\1\s*:\s*''')


def parse_mcu_probe(output: str) -> dict:
    """Read the named ubus object's method schema without assuming hostname output is present."""
    objects = list(_UBUS_OBJECT.finditer(output))
    first_header = objects[0].start() if objects else len(output)
    prefix = output[:first_header].strip().splitlines()
    name = prefix[0].strip() if len(prefix) == 1 else ""
    if not re.fullmatch(r"[A-Za-z0-9](?:[A-Za-z0-9._-]{0,251}[A-Za-z0-9])?", name):
        name = ""
    result = {"hostname": name, "etherlight_mcu": False, "port_rgb": False}
    for index, header in enumerate(objects):
        if header.group(2) != "etherlight.mcu":
            continue
        result["etherlight_mcu"] = True
        end = objects[index + 1].start() if index + 1 < len(objects) else len(output)
        methods = output[header.end():end]
        cursor = 0
        while method := _UBUS_METHOD.match(methods, cursor):
            try:
                arguments, cursor = json.JSONDecoder().raw_decode(methods, method.end())
            except json.JSONDecodeError:
                break
            if method.group(2) == "set" and isinstance(arguments, dict):
                kind = arguments.get("port_rgb")
                if isinstance(kind, str) and kind.casefold() == "string":
                    result["port_rgb"] = True
                    return result
    return result


def parse_mcu_schema_reply(output: str) -> dict:
    """Require a completed, successful schema read before inspecting its capabilities."""
    schema, separator, status = output.rpartition("\n" + MCU_SCHEMA_EXIT)
    if (not separator or MCU_SCHEMA_EXIT in schema
            or not re.fullmatch(r"(?:0|[1-9][0-9]{0,2})\r?\n", status)
            or int(status) != 0 or not schema.strip()):
        raise TransportError("The switch's lighting capabilities could not be read. Try connecting again.")
    return parse_mcu_probe(schema)


@dataclass(frozen=True, slots=True)
class PortOutput:
    """What is sent to one port: a color and a hardware brightness level 1-100."""

    color: Color
    level: int
    precise_rgb: tuple[int, int, int] | None = None
    breath: str | None = None
    gradient: str | None = None
    transition_id: int = 0

    @classmethod
    def off(cls) -> PortOutput:
        return cls(BLACK, 100)

    def preview(self) -> Color:
        """Approximation of what the LED looks like, for UIs."""
        return self.color.scaled(self.level / 100)


@dataclass(frozen=True, slots=True)
class SfpTransition:
    generation: object
    output: PortOutput
    start_rgb: tuple[float, float, float]
    started_at: float
    duration: float

    @property
    def finishes_at(self) -> float:
        return self.started_at + self.duration

    def rgb(self, now: float) -> tuple[float, float, float]:
        progress = max(0.0, min(1.0, (now - self.started_at) / self.duration))
        return tuple(a + (b - a) * progress for a, b in zip(self.start_rgb, self.output.precise_rgb, strict=True))


def looks_the_same(before: PortOutput | None, after: PortOutput) -> bool:
    """True when sending `after` instead of `before` wouldn't make a visible difference."""
    if before is None:
        return False
    before_off = before.color.is_off or before.level <= 0
    after_off = after.color.is_off or after.level <= 0
    if before_off != after_off:
        return False  # never skip turning a port on or off
    a, b = before.color, after.color
    close = abs(a.r - b.r) <= 2 and abs(a.g - b.g) <= 2 and abs(a.b - b.b) <= 2
    return close and abs(before.level - after.level) <= 1


def port_rgb_values(port: int, color: Color, level: int) -> tuple[int, str, int]:
    if isinstance(port, bool) or int(port) != float(port) or not 1 <= int(port) <= 64:
        raise ValueError(f"port {port} out of range")
    if isinstance(level, bool) or not math.isfinite(float(level)):
        raise ValueError("brightness must be a finite number")
    level = max(0, min(100, int(level)))
    # The native parser accepts levels 1-100. Black RGB carries an off request.
    if level == 0 or color.is_off:
        color, level = BLACK, 100
    return int(port), color.to_hex()[1:], level


def port_rgb_command(port: int, color: Color, level: int) -> str:
    port, rgb, level = port_rgb_values(port, color, level)
    payload = f'{{"port_rgb":"{port} {rgb} {level}"}}'
    return f"ubus call etherlight.mcu set '{payload}' 2>/dev/null"


def require_write_ack(reply: str) -> None:
    def unique_object(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError("duplicate response field")
            result[key] = value
        return result

    try:
        response = json.loads(reply, object_pairs_hook=unique_object)
    except (TypeError, ValueError):
        raise TransportError("the switch did not acknowledge the lighting update") from None
    if response != {"set": "ok"}:
        raise TransportError("the switch rejected an Etherlighting update")


@dataclass
class SwitchStats:
    writes: int = 0
    commands: int = 0
    attempted_commands: int = 0
    last_write_ms: float = 0.0
    avg_write_ms: float = 0.0
    errors: int = 0
    extra: dict = field(default_factory=dict)


class EtherlightSwitch:
    def __init__(self, transport: Transport, *, num_ports: int = 8, keepalive: float = 1.5,
                 auto_detect: bool = False) -> None:
        self.output_lease: OutputLease | None = None
        if isinstance(num_ports, bool) or not isinstance(num_ports, int) or not 0 <= num_ports <= 64:
            raise ValueError("number of ports must be an integer from 0 to 64")
        if not math.isfinite(keepalive) or keepalive < 0:
            raise ValueError("keepalive must be a finite non-negative interval")
        self.transport = transport
        self.num_ports = num_ports
        self.auto_detect = auto_detect
        self.hardware_profile: HardwareProfile | None = None
        self.sfp_ports: tuple[int, ...] = ()
        self._precise_generation: object | None = None
        self._native_generation: object | None = None
        self._sfp_generation: object | None = None
        self._steady_generation: object | None = None
        self._native_active: dict[int, tuple[object, PortOutput]] = {}
        self._sfp_transitions: dict[int, SfpTransition] = {}
        self._sfp_owned: dict[int, object] = {}
        self._sfp_pending: set[int] = set()
        self._sfp_uncertain: set[int] = set()
        self.native_uncertain = False
        self.keepalive = keepalive
        self.stats = SwitchStats()
        self._last: dict[int, PortOutput] = {}
        self._last_sent: dict[int, float] = {}
        self._last_full = 0.0
        self.poe_sample: PoeSample | None = None
        self._has_poe: bool | None = None  # learned from the first `swctrl poe show`
        self._cpu_ticks: tuple[int, int] | None = None
        self._send_lock = asyncio.Lock()
        self._last_attempt_at: float | None = None
        self._last_ack_seconds = 0.0
        self._ack_samples: deque[tuple[float, float]] = deque(maxlen=64)

    @property
    def connected(self) -> bool:
        return self.transport.connected

    @property
    def precise_rgb(self) -> bool:
        return (self._precise_generation is not None
                and self._precise_generation is self.transport.lighting_generation
                and self.transport.direct_rgb and self.transport.precise_rgb_supported)

    @property
    def native_breathing(self) -> bool:
        return (self.precise_rgb and self.transport.behavior_supported
                and self._native_generation is not None
                and self._native_generation is self.transport.lighting_generation)

    @property
    def scoped_sfp(self) -> bool:
        return (self._sfp_generation is not None
                and self._sfp_generation is self.transport.lighting_generation
                and self.transport.sfp_mode_supported and self.transport.behavior_supported)

    @property
    def sfp_precise(self) -> bool:
        return self.scoped_sfp and self.precise_rgb

    @property
    def sfp_native_gradient(self) -> bool:
        return (self.sfp_precise and self._native_generation is not None
                and self._native_generation is self.transport.lighting_generation)

    def require_output_profile(self) -> None:
        if not isinstance(self.transport, SimulatedTransport) and not (self.native_breathing or self.scoped_sfp):
            raise ValueError("This switch model or firmware needs compatibility verification before EE can control "
                             "its lighting. Use a verified switch and firmware profile to continue")

    def _validate_precision(self, port: int, channels: tuple[int, int, int]) -> None:
        from .ubus import validate_precise_rgb, validate_sfp_precise_rgb
        if port in self.sfp_ports:
            validate_sfp_precise_rgb(port, channels, profile=self.hardware_profile or PRO_XG8)
            if not self.sfp_precise:
                raise TransportError("precise SFP output has not been verified for this switch")
        else:
            validate_precise_rgb(port, channels, profile=self.hardware_profile or PRO_XG8)
            if not self.precise_rgb:
                raise TransportError("precise RGB output has not been verified for this switch")

    def revoke_precision(self) -> None:
        self.transport.revoke_lighting_profile()
        self._precise_generation = None
        self._native_generation = None
        self._sfp_generation = None
        self._steady_generation = None

    async def connect(self) -> None:
        self.revoke_precision()
        if not self.transport.connected:
            await self.transport.connect()
        self.forget()
        self.reset_timing()

    async def close(self) -> None:
        self.revoke_precision()
        await self.transport.close()
        self.reset_timing()
        self._last_attempt_at = None

    async def recover_output(self, outputs: list[PortOutput]) -> dict:
        """Recover this process's owned output after a dropped connection."""
        lease = self.output_lease
        if lease is None or lease.closed or not all(lease.owns(path) for path in lease.paths):
            raise OutputOwnershipError("The original lighting owner is unavailable. Keep output stopped.")
        lease.before_write(getattr(self.transport, "host_key_fingerprint", None))
        if len(outputs) != self.num_ports:
            raise ValueError("A complete copper frame is required for recovery")
        copper = dict(self._native_active)
        sfp = set(self._sfp_owned) | self._sfp_uncertain
        old_sfp_ports = self.sfp_ports
        old_profile = self.hardware_profile
        old_copper_count = self.num_ports
        lease.uncertain = True
        try:
            # No replacement session is opened until our earlier channels confirm closure.
            try:
                await self.close()
            except TransportError:
                recover = getattr(self.transport, 'recover_disconnected_helpers', None)
                if recover is None:
                    raise
                await recover()
                await self.close()
            await self.connect()
            info = await self.probe()
            lease.before_write(getattr(self.transport, "host_key_fingerprint", None))
            if not all(lease.owns(path) for path in lease.paths):
                raise OutputOwnershipError("The lighting owner changed during recovery.")
            self.require_output_profile()
            if (not info.get("port_rgb") or self.sfp_ports != old_sfp_ports
                    or self.hardware_profile is not old_profile or self.num_ports != old_copper_count
                    or not self.native_breathing or (sfp and not self.scoped_sfp)):
                raise OutputOwnershipError("The switch lighting capabilities changed. Keep output stopped.")
            generation = self.transport.lighting_generation
            if generation is None:
                raise OutputOwnershipError("The lighting connection could not be verified.")
            await self.initialize_output()
            self._native_active = {port: (generation, target) for port, (_, target) in copper.items()}
            self._sfp_owned = dict.fromkeys(sfp, generation)
            self._sfp_uncertain.update(sfp)
            await self.quiesce_native(outputs)
            await self.quiesce_sfp()
            if (self.transport.lighting_generation is not generation or self.native_uncertain
                    or self._native_active or self._sfp_owned or self._sfp_uncertain
                    or not all(lease.owns(path) for path in lease.paths)):
                raise OutputOwnershipError("Lighting recovery did not finish. Keep output stopped.")
            lease.uncertain = False
            return info
        except BaseException:
            lease.uncertain = True
            self.native_uncertain = True
            raise

    def reset_timing(self) -> None:
        """Discard latency evidence after connection loss without granting a pacing credit."""
        self._ack_samples.clear()

    def ack_latency(self, now: float | None = None) -> dict:
        now = timing.monotonic() if now is None else now
        recent = [(at, elapsed) for at, elapsed in self._ack_samples if 0 <= now - at <= 3.0]
        values = sorted(elapsed for _, elapsed in recent)
        return {
            "count": len(values),
            "p95": values[math.ceil(len(values) * 0.95) - 1] if values else None,
            "last_at": recent[-1][0] if recent else None,
        }

    async def probe(self) -> dict:
        """Identify the switch and confirm it exposes the per-port RGB command."""
        self.revoke_precision()
        self.sfp_ports = ()
        self.hardware_profile = None
        board = None
        if self.auto_detect:
            board = await self._read_board()
            self.hardware_profile = identify_profile(board)
            if self.hardware_profile is not None:
                self.num_ports = len(self.hardware_profile.copper_ports)
                if self.hardware_profile.protocol != "ubus-mcu":
                    return {"hostname": "", "etherlight_mcu": False, "port_rgb": False,
                            "sfp_ports": [], "hardware_profile": self.hardware_profile.public_dict()}
        out = await self.transport.run(MCU_SCHEMA_COMMAND, timeout=10)
        result = parse_mcu_schema_reply(out)
        if result["port_rgb"]:
            # Optional identity output cannot interleave with the capability schema.
            hostname = await self.transport.run(HOSTNAME_COMMAND, timeout=5)
            result["hostname"] = parse_mcu_probe(hostname)["hostname"]
            await self.transport.prepare_rgb()
            if board is None:
                board = await self._read_board()
            self.hardware_profile = identify_profile(board)
            profile = identify_profile(board, strict=True)
            if (profile is not None and profile.output_verified
                    and len(profile.copper_ports) == self.num_ports
                    and (self.transport.precise_rgb_supported or self.transport.sfp_mode_supported)):
                generation = self.transport.lighting_generation
                try:
                    digest = (await self.transport.run(RUNNING_DAEMON_HASH, timeout=5)).strip()
                except TransportError:
                    digest = ""
                if (generation is not None and generation is self.transport.lighting_generation
                        and re.fullmatch(profile.daemon_sha256 + r"\s+/proc/[1-9][0-9]*/exe", digest)):
                    if self.transport.behavior_supported:
                        try:
                            firmware = (await self.transport.run(PORT_FIRMWARE_HASH_COMMAND, timeout=5)).strip()
                        except TransportError:
                            firmware = ""
                        if (generation is self.transport.lighting_generation and re.fullmatch(
                                profile.mcu_sha256 + r"\s+/usr/share/firmware/port_led_fw\.bin", firmware)):
                            self.transport.configure_lighting_profile(profile)
                            if self.transport.sfp_mode_supported:
                                self.sfp_ports = profile.optical_ports
                                self._sfp_generation = generation
                            if self.transport.precise_rgb_supported and self.transport.direct_rgb:
                                self._native_generation = generation
                    if (generation is self.transport.lighting_generation
                            and self.transport.direct_rgb and self.transport.precise_rgb_supported):
                        self._precise_generation = generation
                if self.precise_rgb:
                    result["output_precision"] = "rgb16"
            result["native_breathing"] = self.native_breathing
            result["sfp_direct"] = self.scoped_sfp
            result["sfp_native_gradient"] = self.sfp_native_gradient
        if not self.scoped_sfp:
            self.sfp_ports = ()
        result["sfp_ports"] = list(self.sfp_ports)
        result["hardware_profile"] = self.hardware_profile.public_dict() if self.hardware_profile else None
        return result

    async def _read_board(self) -> str:
        try:
            return await self.transport.run(BOARD_LIGHTING_INFO, timeout=5)
        except TransportError:
            return ""

    async def _paced_call(self, operation: Callable[[], Awaitable[None]], *, max_commands: float = 30,
                          stop: asyncio.Event | None = None,
                          budget_getter: Callable[[], float] | None = None,
                          valid: Callable[[], bool] | None = None) -> bool:
        while True:
            current = max_commands if budget_getter is None else budget_getter()
            if isinstance(current, bool) or not math.isfinite(current) or current <= 0:
                raise ValueError("command limit must be a positive finite number")
            budget = min(200.0 if self.transport.direct_rgb else 30.0, current)
            if stop is not None and stop.is_set():
                return False
            deadline = (self._last_attempt_at + 1.0 / budget
                        if self._last_attempt_at is not None else timing.monotonic())
            delay = max(0.0, deadline - timing.monotonic())
            if delay <= 0:
                break
            await timing.wait(delay, stop)
        if valid is not None and not valid():
            return False
        if isinstance(self.transport, SSHTransport) and self.output_lease is None:
            raise OutputOwnershipError("Reserve lighting output before sending switch commands.")
        if self.output_lease is not None:
            self.output_lease.before_write(getattr(self.transport, "host_key_fingerprint", None))
        self._last_attempt_at = timing.monotonic()
        self.stats.attempted_commands += 1
        started = time.perf_counter()
        try:
            await operation()
        except BaseException:
            if self.output_lease is not None:
                self.output_lease.uncertain = True
            self.reset_timing()
            self.stats.errors += 1
            raise
        elapsed = time.perf_counter() - started
        self._last_ack_seconds = elapsed
        if self.transport.direct_rgb:
            self._ack_samples.append((timing.monotonic(), elapsed))
        self.stats.commands += 1
        return True

    async def initialize_output(self) -> None:
        """Establish the reviewed shared selector before any native ownership transition."""
        if not (self.native_breathing or self.scoped_sfp):
            return
        generation = self.transport.lighting_generation
        async with self._send_lock:
            if self._steady_generation is generation:
                return
            try:
                sent = await self._paced_call(lambda: self.transport.write_behavior("steady"),
                                              valid=lambda: generation is self.transport.lighting_generation
                                              and (self.native_breathing or self.scoped_sfp))
                if not sent:
                    raise TransportError("The lighting connection changed before initialization")
            except (TransportError, asyncio.CancelledError) as exc:
                self.native_uncertain = True
                raise NativeOutputUncertain("Lighting initialization was not confirmed. Reconnect to retry.") from exc
            self._steady_generation = generation
            self.native_uncertain = False

    async def _rgb_write(self, port: int, color: Color, level: int) -> None:
        if self.transport.direct_rgb:
            await self.transport.write_port_rgb(*port_rgb_values(port, color, level))
        else:
            command = port_rgb_command(port, color, level)
            reply = await self.transport.run(command + f" || printf '{WRITE_FAILED}\\n'", channel="leds")
            require_write_ack(reply)

    async def _send_port(self, port: int, color: Color, level: int, *, max_commands: float = 30,
                         stop: asyncio.Event | None = None,
                         budget_getter: Callable[[], float] | None = None,
                         precise_rgb: tuple[int, int, int] | None = None,
                         breath: str | None = None, expected_generation: object | None = None) -> bool:
        port_rgb_values(port, color, level)
        generation = self._precise_generation
        if precise_rgb is not None:
            self._validate_precision(port, precise_rgb)
            if level != 100:
                raise ValueError("precise RGB carries brightness in its components")
            if not self.precise_rgb or not self.transport.direct_rgb:
                raise TransportError("precise RGB output has not been verified for this switch")
        if breath is not None and (not 1 <= port <= self.num_ports or breath not in BREATH_BEHAVIORS
                                   or precise_rgb is None or not self.native_breathing):
            raise ValueError("native breathing requires a verified copper RGB target")
        if isinstance(max_commands, bool) or not math.isfinite(max_commands) or max_commands <= 0:
            raise ValueError("command limit must be a positive finite number")
        async with self._send_lock:
            if self.native_uncertain:
                raise NativeOutputUncertain("Lighting cleanup was not confirmed. Reconnect before resuming.")
            options = dict(max_commands=max_commands, stop=stop, budget_getter=budget_getter)
            options["valid"] = lambda: (
                (precise_rgb is None or (self.precise_rgb and generation is self._precise_generation))
                and (expected_generation is None or expected_generation is self.transport.lighting_generation))
            if port in self.sfp_ports:
                return await self._send_sfp_locked(port, color, level, options, precise_rgb=precise_rgb)
            if breath is None:
                async def operation() -> None:
                    if precise_rgb is not None:
                        await self.transport.write_precise_rgb(port, precise_rgb)
                    else:
                        await self._rgb_write(port, color, level)
                sent = await self._paced_call(operation, **options)
                if sent:
                    self._native_active.pop(port, None)
                return sent
            if self._steady_generation is not generation:
                raise NativeOutputUncertain("Lighting needs initialization before synchronized playback.")
            selected = False

            async def select() -> None:
                nonlocal selected
                selected = True
                await self.transport.write_behavior(breath)

            async def target() -> None:
                self._native_active[port] = (generation, PortOutput(color, level, precise_rgb, breath))
                await self.transport.write_precise_rgb(port, precise_rgb)

            try:
                if not await self._paced_call(select, **options):
                    return False
                return await self._paced_call(target, **options)
            finally:
                if selected:
                    try:
                        restored = await self._paced_call(
                            lambda: self.transport.write_behavior("steady"), max_commands=max_commands,
                            budget_getter=budget_getter,
                            valid=lambda: generation is self.transport.lighting_generation)
                        if not restored:
                            raise TransportError("The lighting connection changed during restoration")
                    except (TransportError, asyncio.CancelledError) as exc:
                        self.native_uncertain = True
                        raise NativeOutputUncertain(
                            "Lighting cleanup was not confirmed. Reconnect before resuming."
                        ) from exc

    async def quiesce_native(self, outputs: list[PortOutput]) -> None:
        if len(outputs) != self.num_ports:
            raise ValueError("a complete copper frame is required to stop native playback")
        if any(generation is not self.transport.lighting_generation
               for generation, _ in self._native_active.values()):
            self.native_uncertain = True
            raise NativeOutputUncertain("Native playback needs the original lighting connection for cleanup.")
        for port, (generation, _) in tuple(self._native_active.items()):
            out = outputs[port - 1]
            if not await self._send_port(port, out.color, out.level, precise_rgb=out.precise_rgb,
                                         expected_generation=generation):
                self.native_uncertain = True
                raise NativeOutputUncertain("Native playback could not be stopped on the original connection.")
            self._last[port] = PortOutput(out.color, out.level, out.precise_rgb)

    def sfp_status(self, port: int) -> dict:
        owned = self.scoped_sfp and self._sfp_owned.get(port) is self._sfp_generation
        uncertain = port in self._sfp_uncertain or (port in self._sfp_owned and not owned)
        state = "pending" if port in self._sfp_pending else "uncertain" if uncertain else "active" if owned else "idle"
        return {"state": state, "owned": bool(owned and not uncertain),
                "error": "SFP lighting was not confirmed. Reconnect to retry." if state == "uncertain" else None}

    def sfp_acknowledged(self, port: int, output: PortOutput) -> bool:
        return (self.sfp_status(port)["state"] == "active" and self._last.get(port) == output)

    def sfp_transition(self, port: int) -> SfpTransition | None:
        transition = self._sfp_transitions.get(port)
        if (transition is None or transition.generation is not self._sfp_generation
                or not self.sfp_native_gradient or not self._sfp_confirmed(port)):
            return None
        return transition

    def _sfp_confirmed(self, port: int) -> bool:
        return (self.scoped_sfp and not self.native_uncertain
                and self._sfp_owned.get(port) is self._sfp_generation
                and (port not in self._sfp_uncertain or port in self._sfp_pending))

    def sfp_rgb16(self, port: int) -> tuple[float, float, float]:
        transition = self.sfp_transition(port)
        if transition is not None:
            return transition.rgb(timing.monotonic())
        output = self._last.get(port)
        if output is None or not self._sfp_confirmed(port):
            return 0.0, 0.0, 0.0
        if output.precise_rgb is not None:
            return tuple(float(channel) for channel in output.precise_rgb)
        return tuple(channel * 256 * output.level / 100 for channel in output.color.precise_channels)

    def sfp_segment_deadlines(self, outputs: dict[int, PortOutput], command_budget: float) -> dict[int, float]:
        deadlines = {port: transition.finishes_at if (transition := self.sfp_transition(port)) else 0.0
                     for port in sorted(outputs)}
        if len(outputs) > 1 and len({out.transition_id for out in outputs.values()}) == 1:
            ordered = list(deadlines)
            # Realign the group after ACK jitter without restarting unfinished legs.
            for first, second in zip(reversed(ordered[:-1]), reversed(ordered[1:]), strict=True):
                deadlines[first] = max(deadlines[first], deadlines[second] - 1 / command_budget)
        return deadlines

    def _validate_sfp_gradient(self, port: int, output: PortOutput) -> None:
        if (type(port) is not int or port not in self.sfp_ports
                or output.gradient not in GRADIENT_BEHAVIORS or output.breath is not None
                or type(output.transition_id) is not int or output.transition_id < 1
                or output.precise_rgb is None or output.level != 100):
            raise ValueError("native SFP transitions require a scoped RGB16 target and segment identity")
        self._validate_precision(port, output.precise_rgb)
        if not self.sfp_native_gradient or self._steady_generation is not self._sfp_generation:
            raise TransportError("Native SFP transitions have not been initialized for this switch")

    async def _send_sfp_gradients(self, outputs: dict[int, PortOutput], *, max_commands: float,
                                  stop: asyncio.Event | None = None,
                                  budget_getter: Callable[[], float] | None = None) -> set[int]:
        if not outputs or len(outputs) > len(self.sfp_ports):
            raise ValueError("SFP transition targets must fit the qualified indicator layout")
        for port, output in outputs.items():
            self._validate_sfp_gradient(port, output)
        selectors = {output.gradient for output in outputs.values()}
        if len(selectors) != 1:
            raise ValueError("a scoped SFP transaction requires one transition duration")
        outputs = dict(sorted(outputs.items()))
        generation = self._sfp_generation
        def valid() -> bool:
            return (self.sfp_native_gradient and generation is self._sfp_generation
                    and generation is self.transport.lighting_generation)
        options = dict(max_commands=max_commands, stop=stop, budget_getter=budget_getter, valid=valid)
        accepted: set[int] = set()
        selected = False
        async with self._send_lock:
            if self.native_uncertain:
                raise NativeOutputUncertain("Lighting cleanup was not confirmed. Reconnect before resuming.")
            # Mode acquisition is completed under steady before selecting any gradient.
            for port in outputs:
                if self._sfp_owned.get(port) is not generation or port in self._sfp_uncertain:
                    if not await self._send_sfp_locked(port, BLACK, 100, options, precise_rgb=(0, 0, 0)):
                        return accepted
                    self._last[port] = PortOutput(BLACK, 100, (0, 0, 0))
                    self._last_sent[port] = timing.monotonic()
            current_budget = max_commands if budget_getter is None else budget_getter()
            deadlines = self.sfp_segment_deadlines(outputs, current_budget)
            window = timing.monotonic() + sfp_preparation_lead(current_budget)
            if (min(deadlines.values()) > window
                    or max(deadlines.values()) > window + max(1, len(outputs) - 1) / current_budget):
                return accepted
            self._sfp_pending.update(outputs)

            async def select() -> None:
                nonlocal selected
                selected = True
                await self.transport.write_behavior(next(iter(selectors)))

            try:
                if not await self._paced_call(select, **options):
                    return accepted
                for port, output in outputs.items():
                    if not valid() or (stop is not None and stop.is_set()):
                        break
                    await timing.wait(max(0.0, deadlines[port] - timing.monotonic()), stop)
                    if not valid() or (stop is not None and stop.is_set()):
                        break
                    async def target(port=port, output=output) -> None:
                        start_rgb = self.sfp_rgb16(port)
                        self._sfp_uncertain.add(port)
                        await self.transport.write_sfp_precise(port, output.precise_rgb)
                        self._sfp_transitions[port] = SfpTransition(
                            generation, output, start_rgb, timing.monotonic(), int(output.gradient[-2]))
                    if not await self._paced_call(target, **options):
                        break
                    accepted.add(port)
                return accepted
            except (TransportError, asyncio.CancelledError):
                self._sfp_uncertain.update(outputs)
                raise
            finally:
                try:
                    if selected:
                        try:
                            restored = await self._paced_call(
                                lambda: self.transport.write_behavior("steady"), max_commands=max_commands,
                                budget_getter=budget_getter, valid=valid)
                            if not restored:
                                raise TransportError("The lighting connection changed during SFP restoration")
                        except (TransportError, asyncio.CancelledError) as exc:
                            self.native_uncertain = True
                            self._sfp_uncertain.update(outputs)
                            raise NativeOutputUncertain(
                                "SFP transition cleanup was not confirmed. Reconnect before resuming.") from exc
                    self._sfp_uncertain.difference_update(accepted)
                finally:
                    self._sfp_pending.difference_update(outputs)

    async def _send_sfp_locked(self, port: int, color: Color, level: int, options: dict, *,
                               precise_rgb: tuple[int, int, int] | None = None) -> bool:
        if not self.scoped_sfp or self._steady_generation is not self._sfp_generation:
            raise TransportError("Scoped SFP lighting has not been initialized for this switch")
        generation = self._sfp_generation
        earlier_valid = options.get("valid", lambda: True)
        options = options | {"valid": lambda: earlier_valid() and self.scoped_sfp
                             and generation is self._sfp_generation
                             and (precise_rgb is None or self.sfp_precise)}
        acquisition_started = False

        async def interrupted() -> bool:
            if acquisition_started:
                # Finish partial ownership cleanup before retrying at the new command rate.
                await self._release_sfp_locked(port, max_commands=options.get("max_commands", 30),
                                               budget_getter=options.get("budget_getter"))
            return False

        self._sfp_pending.add(port)
        try:
            if port in self._sfp_owned and self._sfp_owned[port] is not generation:
                raise TransportError("SFP ownership belongs to an earlier lighting connection")
            if self._sfp_owned.get(port) is not generation or port in self._sfp_uncertain:
                async def blank() -> None:
                    self._sfp_uncertain.add(port)
                    self._sfp_owned[port] = generation
                    await self._rgb_write(port, BLACK, 100)

                if not await self._paced_call(blank, **options):
                    return False
                acquisition_started = True
                # Record the possibly-applied transition before waiting for its acknowledgment.
                async def acquire() -> None:
                    self._sfp_uncertain.add(port)
                    self._sfp_owned[port] = generation
                    await self.transport.write_sfp_mode(port, True)

                if not await self._paced_call(acquire, **options):
                    return await interrupted()
            async def output() -> None:
                if precise_rgb is None:
                    await self._rgb_write(port, color, level)
                else:
                    await self.transport.write_sfp_precise(port, precise_rgb)

            if not await self._paced_call(output, **options):
                return await interrupted()
            self._sfp_transitions.pop(port, None)
            self._sfp_uncertain.discard(port)
            return True
        except (TransportError, asyncio.CancelledError):
            self._sfp_uncertain.add(port)
            raise
        finally:
            self._sfp_pending.discard(port)

    async def release_sfp(self, port: int) -> None:
        async with self._send_lock:
            self._sfp_pending.add(port)
            try:
                await self._release_sfp_locked(port)
            finally:
                self._sfp_pending.discard(port)

    async def _release_sfp_locked(self, port: int, *, max_commands: float = 30,
                                  budget_getter: Callable[[], float] | None = None) -> None:
        if port not in self._sfp_owned and port not in self._sfp_uncertain:
            return
        generation = self._sfp_owned.get(port, self._sfp_generation)
        if (not self.scoped_sfp or generation is not self._sfp_generation
                or self.native_uncertain or self._steady_generation is not generation):
            self._sfp_uncertain.add(port)
            raise TransportError("SFP release needs the original verified lighting connection")

        def valid() -> bool:
            return self.scoped_sfp and generation is self._sfp_generation

        options = dict(max_commands=max_commands, budget_getter=budget_getter, valid=valid)
        try:
            if not await self._paced_call(lambda: self._rgb_write(port, BLACK, 100), **options):
                raise TransportError("SFP ownership changed before release")
            if not await self._paced_call(lambda: self.transport.write_sfp_mode(port, False), **options):
                raise TransportError("SFP ownership changed during release")
            self._sfp_owned.pop(port, None)
            self._sfp_uncertain.discard(port)
            self._sfp_transitions.pop(port, None)
            self._last.pop(port, None)
            self._last_sent.pop(port, None)
        except (TransportError, asyncio.CancelledError):
            self._sfp_uncertain.add(port)
            raise

    async def quiesce_sfp(self) -> None:
        for port in tuple(set(self._sfp_owned) | self._sfp_uncertain):
            await self.release_sfp(port)

    async def restore_defaults(self) -> None:
        """Return the LED controller to its shared defaults after a session whose shutdown was never confirmed.

        The behavior goes back to steady and every qualified optical port is darkened and handed back to the
        switch's own service, the same acknowledged sequence a normal release uses. The caller provides the
        output lease; a connection change or a missing acknowledgment raises and leaves the lease uncertain.
        """
        self.require_output_profile()
        generation = self.transport.lighting_generation

        def valid() -> bool:
            return generation is self.transport.lighting_generation and (self.native_breathing or self.scoped_sfp)

        async with self._send_lock:
            if not (self.native_breathing or self.scoped_sfp):
                return
            if not await self._paced_call(lambda: self.transport.write_behavior("steady"), valid=valid):
                raise TransportError("The lighting connection changed during recovery")
            self._steady_generation = generation
            if self.scoped_sfp:
                for port in self.sfp_ports:
                    for operation in (lambda port=port: self._rgb_write(port, BLACK, 100),
                                      lambda port=port: self.transport.write_sfp_mode(port, False)):
                        if not await self._paced_call(operation, valid=valid):
                            raise TransportError("The lighting connection changed during recovery")
            self._native_active.clear()
            self._sfp_owned.clear()
            self._sfp_uncertain.clear()
            self._sfp_transitions.clear()
            self.native_uncertain = False
            self.forget()

    def forget(self) -> None:
        """Drop the memory of what was last sent, so the next write re-sends every port."""
        self._last.clear()
        self._last_sent.clear()
        self._last_full = 0.0

    def forget_sfp(self, port: int) -> None:
        if port not in self.sfp_ports:
            raise ValueError("SFP lighting is unavailable for this port")
        self._last.pop(port, None)
        self._last_sent.pop(port, None)

    async def write(self, outputs: list[PortOutput], *, force: bool = False, max_commands: float = 30,
                    stop: asyncio.Event | None = None,
                    budget_getter: Callable[[], float] | None = None,
                    yield_at: float | None = None,
                    sfp: dict[int, PortOutput] | None = None) -> int:
        """Send a frame. Returns how many port commands went out.

        Compare with the last acknowledged output, so small fade steps accumulate until a change is sent.
        Both transports wait for each acknowledgment before sending another port.
        """
        if len(outputs) != self.num_ports:
            raise ValueError(f"expected {self.num_ports} port outputs, got {len(outputs)}")
        if isinstance(max_commands, bool) or not math.isfinite(max_commands) or max_commands <= 0:
            raise ValueError("command limit must be a positive finite number")
        sfp = {} if sfp is None else dict(sfp)
        if any(type(port) is not int or port not in self.sfp_ports for port in sfp):
            raise ValueError("SFP lighting is unavailable for this port")
        frame = dict(enumerate(outputs, start=1)) | sfp
        # A released SFP must be sent again if EE later takes control of the same color.
        for port in tuple(self._last):
            if port > self.num_ports and port not in sfp:
                self._last.pop(port)
        now = timing.monotonic()
        full = force or (now - self._last_full) >= self.keepalive
        # Validate the complete frame before issuing any command.
        for port, out in frame.items():
            port_rgb_values(port, out.color, out.level)
            if out.precise_rgb is not None:
                self._validate_precision(port, out.precise_rgb)
                if out.level != 100:
                    raise ValueError("precise RGB carries brightness in its components")
                if not self.precise_rgb or not self.transport.direct_rgb:
                    raise TransportError("precise RGB output has not been verified for this switch")
            if out.breath is not None:
                if (not 1 <= port <= self.num_ports or out.breath not in BREATH_BEHAVIORS or out.precise_rgb is None
                        or not self.native_breathing):
                    raise ValueError("native breathing requires a verified copper RGB target")
                if self._steady_generation is not self._precise_generation:
                    raise NativeOutputUncertain("Lighting needs initialization before synchronized playback.")
            if out.gradient is not None:
                self._validate_sfp_gradient(port, out)
            elif out.transition_id != 0:
                raise ValueError("a segment identity requires a native SFP transition")
            if port in sfp and not self.scoped_sfp:
                raise TransportError("Scoped SFP lighting has not been verified for this switch")
            if port in sfp and self._steady_generation is not self._sfp_generation:
                raise TransportError("Scoped SFP lighting has not been initialized for this switch")
        pending = []
        refresh = []
        for port, out in frame.items():
            if out.gradient is not None:
                transition = self.sfp_transition(port)
                # Reasserting a finite target would restart the hardware transition.
                unchanged = transition is not None and transition.output == out and self.sfp_acknowledged(port, out)
            elif out.breath is not None:
                # Native playback advances on the MCU. Reenrollment reverses its shared phase.
                unchanged = self._native_active.get(port) == (self.transport.lighting_generation, out)
            else:
                same = (self._last.get(port) == out if self.transport.direct_rgb
                        else out.precise_rgb is None and looks_the_same(self._last.get(port), out))
                unchanged = same and not force and (bool(sfp) or not full)
                if port in self._native_active:
                    unchanged = False
                if port in self._sfp_transitions:
                    unchanged = False
                if port in sfp and not self.sfp_acknowledged(port, out):
                    unchanged = False
                if unchanged and sfp and now - self._last_sent.get(port, float("-inf")) >= self.keepalive:
                    refresh.append((port, out))
            if not unchanged:
                pending.append((port, out))
        if sfp:
            # Changed indicators go first; spread unchanged copper refreshes across frames.
            pending.sort(key=lambda item: item[0] not in sfp)
            if refresh:
                pending.append(min(refresh, key=lambda item: self._last_sent.get(item[0], float("-inf"))))
        sent = 0
        # Separate native enrollment from the streamed frame. SFPs may share a selector transaction;
        # every target remains a distinct acknowledged port request with the existing pacing.
        for selector in sorted(GRADIENT_BEHAVIORS):
            native = {port: out for port, out in pending if out.gradient == selector}
            if not native:
                continue
            started = time.perf_counter()
            accepted = await self._send_sfp_gradients(native, max_commands=max_commands, stop=stop,
                                                     budget_getter=budget_getter)
            for port in accepted:
                self._last[port] = native[port]
                self._last_sent[port] = timing.monotonic()
            if accepted:
                deadlines = self.sfp_segment_deadlines({port: native[port] for port in accepted}, max_commands)
                deadline = min(deadlines.values()) - sfp_preparation_lead(max_commands)
                yield_at = deadline if yield_at is None else min(yield_at, deadline)
            sent += len(accepted)
            if accepted:
                ms = (time.perf_counter() - started) * 1000
                self.stats.writes += 1
                self.stats.last_write_ms = ms
                self.stats.avg_write_ms = (ms if self.stats.writes == 1
                                           else self.stats.avg_write_ms * 0.9 + ms * 0.1)
        pending = [(port, out) for port, out in pending if out.gradient is None]
        for offset in range(0, len(pending), 8):
            if stop is not None and stop.is_set():
                break
            chunk = pending[offset:offset + 8]
            started = time.perf_counter()
            accepted = 0
            for port, out in chunk:
                if (port <= self.num_ports and yield_at is not None
                        and timing.monotonic() + 1 / max_commands >= yield_at):
                    break
                if not await self._send_port(port, out.color, out.level, max_commands=max_commands,
                                             stop=stop, budget_getter=budget_getter,
                                             precise_rgb=out.precise_rgb, breath=out.breath):
                    break
                self._last[port] = out
                self._last_sent[port] = timing.monotonic()
                accepted += 1
                sent += 1
            if not accepted:
                break
            ms = (time.perf_counter() - started) * 1000
            s = self.stats
            s.writes += 1
            s.last_write_ms = ms
            s.avg_write_ms = ms if s.writes == 1 else s.avg_write_ms * 0.9 + ms * 0.1
        if full and (not sfp or force) and sent == len(pending):
            self._last_full = timing.monotonic()
        elif sfp and self.num_ports and all(port in self._last_sent for port in range(1, self.num_ports + 1)):
            self._last_full = min(self._last_sent[port] for port in range(1, self.num_ports + 1))
        return sent

    async def read_health(self) -> dict:
        return parse_health(await self.transport.read_health())

    async def read_cpu(self) -> dict | None:
        """The switch's CPU idle share since the previous call, and its 1-minute load average.

        Uses the shell's own `read`, so it doesn't start any process on the switch.
        """
        out = await self.transport.run(
            'read -r a < /proc/stat; echo "$a"; read -r b < /proc/loadavg; echo "$b"', channel="links", timeout=5
        )
        lines = out.splitlines()
        try:
            ticks = [int(v) for v in lines[0].split()[1:]]
            load = float(lines[1].split()[0])
        except (IndexError, ValueError):
            return None
        if len(ticks) < 4 or any(tick < 0 for tick in ticks) or not math.isfinite(load):
            return None
        idle, total = ticks[3] + (ticks[4] if len(ticks) > 4 else 0), sum(ticks)
        previous, self._cpu_ticks = self._cpu_ticks, (idle, total)
        if previous is None or total <= previous[1]:
            return {"idle": None, "load": load}
        return {"idle": (idle - previous[0]) / (total - previous[1]), "load": load}

    async def set_port(self, port: int, color: Color, level: int = 100) -> None:
        """One-off write of a single port (no keepalive: etherlightd takes it back within seconds)."""
        if not 1 <= port <= self.num_ports:
            raise ValueError(f"ports are numbered 1 to {self.num_ports}")
        await self._send_port(port, color, level)

    async def read_links(self) -> dict[int, PortLink]:
        out = await self.transport.run("swctrl port show", channel="links", timeout=10)
        return parse_port_show(out)

    async def read_poe(self) -> dict[int, PoePort]:
        """PoE status per port. Empty on switches (or firmware) without PoE or with an unfamiliar format."""
        if self._has_poe is False:
            return {}
        out = await self.transport.run("swctrl poe show 2>/dev/null", channel="links", timeout=10)
        self.poe_sample = parse_poe_sample(out)
        ports = self.poe_sample.ports
        if self._has_poe is None:
            self._has_poe = bool(ports)
        return ports

    async def benchmark(self, count: int = 20, port: int = 1, color: Color = WHITE) -> dict:
        """Measure write latency with a fixed color and at most 30 commands per second."""
        if isinstance(count, bool) or not isinstance(count, int) or not 1 <= count <= 60:
            raise ValueError("count must be an integer from 1 to 60")
        if not 1 <= port <= self.num_ports:
            raise ValueError(f"ports are numbered 1 to {self.num_ports}")
        port_rgb_values(port, color, 100)
        async def measure(size: int) -> float:
            elapsed = 0.0
            for _ in range(size):
                await self._send_port(port, color, 100)
                elapsed += self._last_ack_seconds
            return elapsed * 1000

        single = sum([await measure(1) for _ in range(count)]) / count
        elapsed = 0.0
        batch_size = min(8, self.num_ports)
        for offset in range(0, count, batch_size):
            elapsed += await measure(min(batch_size, count - offset))
        batched = elapsed / count
        frame_ms = batched * self.num_ports
        return {
            "single_command_ms": round(single, 2),
            "batched_per_command_ms": round(batched, 2),
            "full_frame_ms": round(frame_ms, 2),
            "max_full_frame_fps": round(1000 / frame_ms, 1) if frame_ms else None,
        }
