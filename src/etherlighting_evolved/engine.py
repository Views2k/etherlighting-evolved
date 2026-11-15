"""The render engine.

Each frame is built in this order, and each step can override the one before it:

    zones      each zone (a group of ports) runs its own effect
    rules      the first matching rule sets the port's look (offline, PoE, anomaly, time of day, ...)
    overrides  colors set by hand on single ports
    brightness global, unless a rule or override sets its own

Only ports that changed visibly are sent to the switch, plus a full refresh every `keepalive` seconds, because the
switch's own Etherlighting service keeps reapplying its colors. A change made through the API wakes the render
loop immediately instead of waiting for the next frame.

Output starts at a conservative rate. The persistent color channel can increase its rate when recent CPU,
acknowledgment latency, and demand support it; shell output stays at thirty commands per second or less.
Colors are gamma corrected, and a port whose zone, rule or effect changes crossfades to its new color.
"""

from __future__ import annotations

import asyncio
import contextlib
import copy
import logging
import math
import random
import re
import secrets
from dataclasses import dataclass, field
from datetime import datetime
from typing import Any

from . import timing
from .bandwidth import UplinkBandwidth
from .color import BLACK, Color
from .effects import create_effect
from .effects.base import Effect, RenderContext
from .health import HEALTH_INTERVAL, HealthParseError
from .links import PortLink, TrafficMeter
from .mailbox import Mailbox
from .poe import PoePort, summarize_poe
from .rules import DEFAULT_RULES, PortFacts, Rule, first_match
from .sfp import NATIVE_PERIODS, SfpLight, SfpSync
from .sfp_playback import SfpPlayback, displayed_rgb
from .switch import EtherlightSwitch, PortOutput
from .transport import (
    AuthenticationError,
    HostKeyMismatchError,
    PortSampleUnavailable,
    SimulatedTransport,
    TransportError,
)

log = logging.getLogger(__name__)


class NotFound(KeyError):
    """A zone, rule or schedule id that doesn't exist."""

    def __str__(self) -> str:
        return str(self.args[0]) if self.args else "not found"


def bounded_number(value: Any, low: float, high: float, name: str) -> float:
    try:
        number = float(value)
    except (TypeError, ValueError, OverflowError):
        raise ValueError(f"{name} must be a finite number") from None
    if isinstance(value, bool) or not math.isfinite(number):
        raise ValueError(f"{name} must be a finite number")
    return min(high, max(low, number))


def percent(value: Any) -> int:
    return round(bounded_number(value, 0, 100, "brightness"))


def to_output(color: Color, brightness: float, dimming: str = "rgb", gamma: float = 2.2,
              *, precise: bool = False) -> PortOutput:
    """Turn a color and a brightness into what the LED controller receives.

    Apply the configured gamma curve to both color and effective brightness. gamma=1 disables the curve.

    "level" dimming normalizes the color to full strength and puts its brightness in the hardware level.
    "rgb" carries all brightness changes in the color bytes and keeps the hardware level at 100, including black.
    """
    if precise:
        scale = (max(0.0, min(100.0, brightness)) / 100) ** gamma
        channels = tuple(round(65280 * (c / 255) ** gamma * scale) for c in color.precise_channels)
        preview = Color(*(round(c / 256) for c in channels))
        return PortOutput(preview, 100, channels)
    if brightness <= 0 or color.is_off:
        return PortOutput.off()
    linear = [(c / 255) ** gamma for c in (color.r, color.g, color.b)]
    scale = (min(100.0, brightness) / 100) ** gamma
    if dimming == "rgb":
        rgb = Color(*(round(255 * v * scale) for v in linear))
        return PortOutput.off() if rgb.is_off else PortOutput(rgb, 100)
    peak = max(linear)
    level = round(100 * peak * scale)
    if level <= 0:
        return PortOutput.off()
    return PortOutput(Color(*(round(255 * v / peak) for v in linear)), level)


def ease(x: float) -> float:
    return x * x * (3 - 2 * x)


@dataclass
class Override:
    color: Color
    brightness: int | None = None

    def to_dict(self) -> dict:
        return {"color": self.color.to_hex(), "brightness": self.brightness}


@dataclass
class Zone:
    id: str
    name: str
    ports: tuple[int, ...]
    effect: Effect
    started_at: float = field(default_factory=lambda: timing.monotonic())
    wall_started_at: float = field(default_factory=lambda: timing.monotonic())

    def to_dict(self) -> dict:
        return {
            "id": self.id,
            "name": self.name,
            "ports": list(self.ports),
            "effect": {"id": self.effect.id, "name": self.effect.name, "params": self.effect.params_json()},
        }


def _slug(name: str, taken: set[str]) -> str:
    base = re.sub(r"[^a-z0-9]+", "-", name.lower()).strip("-") or "zone"
    slug, n = base, 2
    while slug in taken:
        slug, n = f"{base}-{n}", n + 1
    return slug


class Engine:
    MAX_INSTANT_RATE = 30.0
    SFP_FRAME_RATE = 50.0
    MAX_COMMANDS = 200.0
    INITIAL_COMMANDS = 30.0
    BUSY_IDLE = 0.50
    CALM_IDLE = 0.75
    TELEMETRY_AGE = 3.0
    MAX_RECONNECT_ATTEMPTS = 5

    def __init__(
        self,
        switch: EtherlightSwitch,
        *,
        fps: float = 25.0,
        max_commands: float = 200.0,
        transition: float = 0.6,
        gamma: float = 2.2,
        link_poll: float = 1.0,
        brightness: int = 100,
        dimming: str = "rgb",
        effect: str = "static",
        effect_params: dict | None = None,
        rules: list[dict] | None = None,
        reconnect_delay: float = 3.0,
    ) -> None:
        if dimming not in ("level", "rgb"):
            raise ValueError("dimming must be 'level' or 'rgb'")
        self.switch = switch
        self.num_ports = switch.num_ports
        self._profile_model = getattr(getattr(switch, "hardware_profile", None), "key", None)
        self._output_sfp_ports = tuple(switch.sfp_ports)
        self.fps = bounded_number(fps, 1, 30, "frame rate")
        self.max_commands = bounded_number(max_commands, 5, self.MAX_COMMANDS, "command limit")
        self.transition = bounded_number(transition, 0, 5, "transition")
        self.gamma = bounded_number(gamma, 1, 3, "gamma")
        self.link_poll = bounded_number(link_poll, 0.5, 3600, "status interval")
        self.brightness = percent(brightness)
        # Keep accepting older configurations while encoding effect fades in RGB.
        # Level-only updates did not reproduce those fades on the Pro XG 8 PoE.
        self.dimming = "rgb"
        self.reconnect_delay = bounded_number(reconnect_delay, 1, 300, "reconnect interval")
        self._reconnect_failures = 0
        self._reconnect_blocked = False
        self._command_budget = min(self.max_commands, self.INITIAL_COMMANDS)
        self._cpu_updated_at: float | None = None
        self._healthy_cpu_samples = 0
        self._budget_changed_at = timing.monotonic()
        self._animation_updated_at = timing.monotonic()
        self._animation_time = self._animation_updated_at
        self._animation_origin = self._animation_time
        self._last_write_seconds = 0.0
        self.cpu_idle: float | None = None
        self.cpu_load: float | None = None
        self._switch_health: dict | None = None
        self.fans = None
        self._health_updated_at: float | None = None
        self._health_error: str | None = None

        everything = tuple(range(1, self.num_ports + 1))
        self.zones: list[Zone] = []
        if everything:
            base = create_effect(effect, self.num_ports, effect_params)
            self.zones.append(Zone("all", "All ports", everything, base, self._animation_time))
        specs = DEFAULT_RULES if rules is None else rules
        self.rules: list[Rule] = self._parse_rules(specs)
        self.overrides: dict[int, Override] = {}
        # Preserve pre-probe eight-port scene storage; only switch.sfp_ports can receive output.
        sfp_ports = self._output_sfp_ports or ((9, 10) if self.num_ports == 8 and self._profile_model is None else ())
        self.sfp: dict[int, SfpLight] = {port: SfpLight() for port in sfp_ports}
        self.sfp_sync = SfpSync()
        self._sfp_zones: dict[int, Zone] = {}
        self._sfp_zone_colors: dict[int, Color] = {}
        self._sfp_playback = SfpPlayback()
        self._sfp_revisions = {port: 0 for port in sfp_ports}
        self._sfp_acknowledged = {port: 0 for port in sfp_ports}
        self._sfp_release_ports: set[int] = set()
        self._sfp_releasing: set[int] = set()
        self._sfp_engaged = False
        self._sfp_release_pending = False
        self._sfp_changed = asyncio.Event()
        self._sfp_rule_ids: dict[int, str | None] = {port: None for port in sfp_ports}

        self.mode = "running"  # "released" hands the LEDs back to the switch's own Etherlighting
        self.connected = False
        self.error: str | None = None
        self.switch_info: dict = {}
        self.links: dict[int, PortLink] = {}
        self.poe: dict[int, PoePort] = {}
        self.traffic: dict[int, float] = {}
        self._telemetry_generation = None
        self._telemetry_token: str | None = None
        self._telemetry_sequence = 0
        self._telemetry_updated_at: float | None = None
        self._telemetry_sampled_at: str | None = None
        self._poe_updated_at: float | None = None
        self._identify: dict | None = None
        self._identify_base: tuple[Color, float] | None = None
        self._identify_timer: asyncio.TimerHandle | None = None
        self._history = None
        self._history_binding: str | None = None
        self.history_error: str | None = None
        self.revision = 0
        self.frames = 0
        self.commands = 0

        self.last_colors: list[Color] = [BLACK] * self.num_ports
        self.last_outputs: list[PortOutput] = [PortOutput.off()] * self.num_ports
        self.last_sources: list[dict] = [{"kind": "zone", "id": "all"}] * self.num_ports

        # Crossfades: what each port shows, what set it, and fades in progress (start color, start brightness, time)
        self._shown: list[tuple[Color, float]] = [(BLACK, 0.0)] * self.num_ports
        self._keys: list[tuple | None] = [None] * self.num_ports
        self._fades: dict[int, tuple[Color, float, float]] = {}
        self._sent: list[tuple[float, int]] = []  # (time, commands) for the last few seconds

        self._meter = TrafficMeter()
        self._bandwidth = UplinkBandwidth(self._telemetry_ports(), poll_interval=self.link_poll)
        self._t0 = timing.monotonic()
        self._dirty = asyncio.Event()
        self._write_idle = asyncio.Event()
        self._write_idle.set()
        self._output_halt = asyncio.Event()
        self._native_engaged = False
        self._native_release_pending = False
        self._native_blocked = False
        self._native_quiesce_lock = asyncio.Lock()
        self._initialization_idle = asyncio.Event()
        self._initialization_idle.set()
        self._stopping = False
        self._tasks: list[asyncio.Task] = []
        self._subscribers: set[Mailbox] = set()

    # Lifecycle

    async def start(self) -> None:
        if self._tasks:
            return
        self._stopping = False
        if self.connected:
            self.connected = False
            self._initialization_idle.clear()
            try:
                self.switch.require_output_profile()
                self._validate_switch_topology()
                await self.switch.initialize_output()
                self._validate_native_zones(self.zones, require_capability=True)
            except BaseException as exc:
                self.error = str(exc) or "lighting initialization was interrupted"
                if self._output_uncertain():
                    self._native_blocked = True
                    self.mode = "released"
                    self._output_halt.set()
                raise
            finally:
                self._initialization_idle.set()
            self.connected = True
        if self.mode == "running":
            self._output_halt = asyncio.Event()
        self._tasks = [
            asyncio.create_task(self._keep_connected(), name="ee-connection"),
            asyncio.create_task(self._render_loop(), name="ee-render"),
            asyncio.create_task(self._status_loop(), name="ee-status"),
            asyncio.create_task(self._health_loop(), name="ee-health"),
            asyncio.create_task(self._fan_loop(), name="ee-fans"),
        ]

    async def stop(self) -> None:
        self._stopping = True
        self.release()
        cleanup_error = None
        try:
            await self.wait_for_output_idle()
        except Exception as exc:
            cleanup_error = (exc if isinstance(exc, TransportError) else
                             TransportError("Lighting shutdown could not be confirmed"))
            if self.switch.output_lease is not None:
                self.switch.output_lease.uncertain = True
        for task in self._tasks:
            task.cancel()
        for task in self._tasks:
            with contextlib.suppress(asyncio.CancelledError, Exception):
                await task
        self._tasks = []
        lease = self.switch.output_lease
        desktop_output = getattr(self.switch.transport, "desktop_output", None)
        if (cleanup_error is not None and lease is not None
                and (self.switch.native_uncertain or
                     (desktop_output is not None and lease.keep_for_player and self._sfp_release_pending))):
            outputs = [to_output(color, brightness, self.dimming, self.gamma, precise=True)
                       for color, brightness in self._shown]
            try:
                await self.switch.recover_output(outputs)
                cleanup_error = None
            except Exception:
                # Keep ownership records when reconnection or device cleanup is unconfirmed.
                pass
        try:
            if desktop_output is not None and self.switch.connected:
                with contextlib.suppress(TimeoutError, TransportError):
                    await asyncio.wait_for(desktop_output.release(), timeout=2)
            await self.switch.close()
        except Exception:
            cleanup_error = cleanup_error or TransportError("The lighting connection did not close cleanly")
            # The unfinished lease stays on disk until a later confirmed close.
        self.connected = False
        if cleanup_error is not None:
            raise cleanup_error
        if self.switch.output_lease is not None:
            self.switch.output_lease.finish()

    # Temporary identification stays outside the saved scene.

    def start_identify(self, port: Any) -> dict:
        port = self._port(port, effect_port=True)
        if (not self.connected or self.mode != "running" or self._stopping
                or self._native_blocked or self._output_uncertain() or self._reconnect_blocked):
            raise ValueError("Resume connected lighting before identifying a port.")
        self.switch.require_output_profile()
        if not isinstance(self.switch.transport, SimulatedTransport):
            lease = self.switch.output_lease
            if lease is None or lease.closed or lease.uncertain or not all(lease.owns(path) for path in lease.paths):
                raise ValueError("EE must own lighting output before identifying a port.")
            lease.before_write(getattr(self.switch.transport, "host_key_fingerprint", None))
        if port > self.num_ports and not self.switch.scoped_sfp:
            raise ValueError("This SFP port does not have qualified lighting output.")
        if self._identify is not None:
            raise ValueError("Another port is being identified. Stop it before starting another.")
        loop = asyncio.get_running_loop()
        token = secrets.token_hex(16)
        self._identify = {"id": token, "port": port, "started": timing.monotonic(),
                          "expires": timing.monotonic() + 10.0}
        self._identify_timer = loop.call_later(10.0, self.stop_identify, token)
        self._identify_changed(port)
        return self.identify_state()

    def stop_identify(self, token: str | None = None) -> None:
        current = self._identify
        if current is None:
            return
        if token is not None and token != current["id"]:
            raise ValueError("The identification changed. Refresh before stopping it.")
        self._identify = None
        self._identify_base = None
        if self._identify_timer is not None:
            self._identify_timer.cancel()
            self._identify_timer = None
        self._identify_changed(current["port"])

    def _identify_changed(self, port: int) -> None:
        if port in self.sfp:
            self._revise_sfp_outputs({port})
        else:
            self._keys[port - 1] = None
            self._fades.pop(port - 1, None)
        if self.mode == "running":
            self._output_halt.set()
            self._output_halt = asyncio.Event()
        self._dirty.set()
        self._sfp_changed.set()
        if self._subscribers:
            self._publish(self.state_message())

    def identify_state(self) -> dict | None:
        current = self._identify
        if current is None:
            return None
        return {"id": current["id"], "port": current["port"],
                "seconds_remaining": max(0, round(current["expires"] - timing.monotonic(), 1))}

    def _identifying(self, port: int) -> bool:
        current = self._identify
        return bool(current is not None and current["port"] == port
                    and self.connected and self.mode == "running"
                    and timing.monotonic() < current["expires"])

    def _identify_color(self) -> Color:
        phase = (timing.monotonic() - self._identify["started"]) % 1.0
        return Color(255, 255, 255) if phase < 0.5 else Color(0, 128, 255)

    def attach_history(self, history, binding: str | None) -> None:
        self._history, self._history_binding = history, binding
        self._record_history()

    def _record_history(self) -> None:
        if self._history is None or self._history_binding is None:
            return
        try:
            self._history.record(self._history_binding, self.snapshot())
            self.history_error = None
        except OSError:
            self.history_error = "Lighting history could not be saved. Check the data folder."

    def telemetry_state(self) -> dict:
        age = None if self._telemetry_updated_at is None else max(0, timing.monotonic() - self._telemetry_updated_at)
        return {"generation": self._telemetry_token, "sequence": self._telemetry_sequence,
                "sampled_at": self._telemetry_sampled_at,
                "sample_age_ms": None if age is None else round(age * 1000),
                "stale": not self.connected or age is None or age > max(5.0, self.link_poll * 2.5)}

    def poe_summary(self) -> dict:
        age = None if self._poe_updated_at is None else max(0, timing.monotonic() - self._poe_updated_at)
        return summarize_poe(getattr(self.switch, "poe_sample", None), range(1, self.num_ports + 1),
                             age=age, connected=self.connected, max_age=max(5.0, self.link_poll * 2.5))

    # Zones

    def _port(self, port: Any, *, effect_port: bool = False) -> int:
        error = ("Select an available Ethernet or SFP effect port" if effect_port
                 else f"ports are numbered 1 to {self.num_ports}")
        try:
            number = float(port)
        except (TypeError, ValueError, OverflowError):
            raise ValueError(error) from None
        if isinstance(port, bool) or not math.isfinite(number) or not number.is_integer():
            raise ValueError(error)
        port = int(number)
        if effect_port and port in self.switch.sfp_ports:
            return port
        if not 1 <= port <= self.num_ports:
            raise ValueError(error)
        return port

    def zone(self, zone_id: str | None = None) -> Zone:
        if zone_id is None and self.zones:
            return self.zones[0]
        for zone in self.zones:
            if zone.id == zone_id:
                return zone
        raise NotFound(f"no zone {zone_id!r}")

    @staticmethod
    def _synchronized(effect: Effect) -> bool:
        return effect.id == "breathing" and effect.p.get("playback", "custom") == "synchronized"

    def _validate_native_zones(self, zones: list[Zone], *, require_capability: bool = False) -> None:
        synchronized = [zone for zone in zones if self._synchronized(zone.effect)]
        if len({zone.effect.p["pace"] for zone in synchronized}) > 1:
            raise ValueError("synchronized Breathing groups must use the same pace")
        if (any(port <= self.num_ports for zone in synchronized for port in zone.ports)
                and (require_capability or self.switch_info)
                and not self.switch.native_breathing
                and not isinstance(self.switch.transport, SimulatedTransport)):
            raise ValueError("synchronized Breathing is unavailable on this switch")

    @staticmethod
    def _scene_effect(effect_id: Any, num_ports: int, params: Any) -> Effect:
        if not isinstance(effect_id, str) or not isinstance(params, dict):
            raise ValueError("an effect requires a name and an object of parameters")
        try:
            return create_effect(effect_id, num_ports, params)
        except (TypeError, KeyError, AttributeError, OverflowError) as exc:
            raise ValueError("invalid effect parameters") from exc

    def _build_zones(self, specs: list[dict]) -> list[Zone]:
        if not isinstance(specs, list):
            raise ValueError("zones must be a list")
        if not specs and self.num_ports:
            raise ValueError("there must be at least one zone")
        zones: list[Zone] = []
        claimed: set[int] = set()
        for spec in specs:
            if not isinstance(spec, dict):
                raise ValueError("a zone must be an object")
            name = str(spec.get("name") or "Zone").strip()[:40]
            raw_ports = spec.get("ports", [])
            if not isinstance(raw_ports, list):
                raise ValueError("zone ports must be a list")
            ports = tuple(sorted({self._port(p, effect_port=True) for p in raw_ports}))
            if not ports:
                raise ValueError(f"zone {name!r} has no ports")
            if claimed & set(ports):
                raise ValueError(f"port {min(claimed & set(ports))} is in more than one zone")
            claimed |= set(ports)
            effect_spec = spec.get("effect", {})
            if isinstance(effect_spec, str):
                effect_spec = {"id": effect_spec}
            if not isinstance(effect_spec, dict):
                raise ValueError("a zone effect must be an object or an effect name")
            effect = self._scene_effect(effect_spec.get("id", "static"), len(ports), effect_spec.get("params", {}))
            zone_id = str(spec.get("id") or "") or _slug(name, {z.id for z in zones})
            if zone_id in {z.id for z in zones}:
                raise ValueError(f"duplicate zone id {zone_id!r}")
            zones.append(Zone(zone_id, name, ports, effect))
        return zones

    def _replace_zones(self, zones: list[Zone]) -> set[int]:
        previous = self._sfp_zones
        self.zones = zones
        self._sfp_zones = {port: zone for zone in zones for port in zone.ports if port in self.sfp}
        changed = {port for port in previous.keys() | self._sfp_zones.keys()
                   if port not in previous or port not in self._sfp_zones
                   or previous[port].effect is not self._sfp_zones[port].effect}
        for port in changed:
            self._sfp_zone_colors.pop(port, None)
        return changed

    def _revise_sfp_outputs(self, ports: set[int]) -> None:
        if not ports:
            return
        for port in ports:
            self._sfp_revisions[port] += 1
            if self._sfp_requires_output(port):
                self._sfp_release_ports.discard(port)
            else:
                self._sfp_release_ports.add(port)
        self._sfp_changed.set()

    def set_zones(self, specs: list[dict], *, clear_overrides: bool = False) -> None:
        """Replace all zones. Each spec: {"name", "ports": [...], "effect": {"id", "params"}, optional "id"}.
        Ports left out of every zone stay dark unless a rule or override lights them."""
        zones = self._build_zones(specs)
        self._validate_native_zones(zones, require_capability=self.connected)
        clock = self._animation_clock()
        existing = {zone.id: zone for zone in self.zones}
        for zone in zones:
            zone.started_at = clock
            previous = existing.get(zone.id)
            if (previous is not None and previous.ports == zone.ports
                    and previous.effect.id == zone.effect.id
                    and previous.effect.params_json() == zone.effect.params_json()):
                zone.effect = previous.effect
                zone.started_at = previous.started_at
                zone.wall_started_at = previous.wall_started_at
        self._revise_sfp_outputs(self._replace_zones(zones))
        self._sync_sfp_rules()
        if clear_overrides:
            for zone in zones:
                for port in zone.ports:
                    self.overrides.pop(port, None)
        self._changed()

    def set_effect(self, effect_id: str, params: dict | None = None, zone: str | None = None,
                   *, clear_overrides: bool = False) -> None:
        target = self.zone(zone)
        effect = create_effect(effect_id, len(target.ports), params or {})
        if self._synchronized(effect):
            existing = next((item.effect for item in self.zones
                             if item is not target and self._synchronized(item.effect)), None)
            if existing is not None:
                effect.update({"pace": existing.p["pace"]})
        planned = [Zone(item.id, item.name, item.ports, effect if item is target else item.effect)
                   for item in self.zones]
        self._validate_native_zones(planned, require_capability=True)
        target.effect = effect
        target.started_at = self._animation_clock()
        target.wall_started_at = timing.monotonic()
        self._revise_sfp_outputs(self._sfp_zones.keys() & target.ports)
        if clear_overrides:
            for port in target.ports:
                self.overrides.pop(port, None)
        self._changed()

    def update_params(self, params: dict, zone: str | None = None) -> None:
        target = self.zone(zone)
        candidate = copy.deepcopy(target.effect)
        candidate.update(params)
        synchronized = self._synchronized(candidate)
        changes = dict(params)
        shared = [item for item in self.zones if item is not target and self._synchronized(item.effect)]
        if synchronized and "pace" not in changes and shared:
            changes["pace"] = shared[0].effect.p["pace"]
            candidate.update({"pace": changes["pace"]})
        planned = []
        for item in self.zones:
            effect = candidate if item is target else copy.deepcopy(item.effect)
            if synchronized and item in shared:
                effect.update({"pace": candidate.p["pace"]})
            planned.append(Zone(item.id, item.name, item.ports, effect))
        self._validate_native_zones(planned, require_capability=True)
        clock = self._animation_clock()
        before = target.effect.params_json()
        effect_time = (timing.monotonic() - target.wall_started_at
                       if target.effect.category in {"time", "data"} else clock - target.started_at)
        target.effect.update_at(changes, effect_time)
        if synchronized:
            for item in shared:
                item.effect.update_at({"pace": candidate.p["pace"]}, clock - item.started_at)
                for port in item.ports:
                    self._fades.pop(port - 1, None)
        after = target.effect.params_json()
        if synchronized or before.get("playback") == "synchronized":
            for port in target.ports:
                self._fades.pop(port - 1, None)
        elif self.transition > 0 and any(before.get(key) != value for key, value in after.items()
                                       if key not in {"speed", "period", "rate"}):
            for port in target.ports:
                i = port - 1
                if port <= self.num_ports and self._keys[i] is not None and self.last_sources[i].get("kind") == "zone":
                    color, brightness = self._shown[i]
                    self._fades[i] = (color, brightness, clock)
        affected = self._sfp_zones.keys() & target.ports
        if synchronized:
            affected.update(port for zone in shared for port in zone.ports if port in self._sfp_zones)
        self._revise_sfp_outputs(affected)
        self._changed()

    # Rules

    def _parse_rule(self, spec: dict) -> Rule:
        if not isinstance(spec, dict):
            raise ValueError("a rule must be an object")
        if spec.get("ports") is not None and not isinstance(spec["ports"], list):
            raise ValueError("rule ports must be a list")
        if "conditions" in spec and not isinstance(spec["conditions"], list):
            raise ValueError("rule conditions must be a list")
        if "look" in spec and not isinstance(spec["look"], dict):
            raise ValueError("a rule look must be an object")
        try:
            return Rule.parse(spec, num_ports=self.num_ports, extra_ports=self.switch.sfp_ports)
        except (TypeError, KeyError, AttributeError, OverflowError) as exc:
            raise ValueError("invalid rule settings") from exc

    def _parse_rules(self, specs: list[dict]) -> list[Rule]:
        if not isinstance(specs, list):
            raise ValueError("rules must be a list")
        rules = [self._parse_rule(spec) for spec in specs]
        if len({rule.id for rule in rules}) != len(rules):
            raise ValueError("Every automation must have a unique ID.")
        return rules

    def _rules_changed(self) -> None:
        self._invalidate_sfp_intent()
        self._sync_sfp_rules()
        self._changed()

    def set_rules(self, specs: list[dict]) -> None:
        """Replace the whole rule list; its order is the priority (first match wins)."""
        self.rules = self._parse_rules(specs)
        self._rules_changed()

    def add_rule(self, spec: dict, *, position: int | None = None) -> Rule:
        rule = self._parse_rule(spec)
        if rule.id in {r.id for r in self.rules}:
            raise ValueError(f"a rule with id {rule.id!r} already exists")
        self.rules.insert(len(self.rules) if position is None else max(0, position), rule)
        self._rules_changed()
        return rule

    def update_rule(self, rule_id: str, changes: dict) -> Rule:
        index = self._rule_index(rule_id)
        merged = {**self.rules[index].to_dict(), **changes, "id": rule_id}
        self.rules[index] = self._parse_rule(merged)
        self._rules_changed()
        return self.rules[index]

    def delete_rule(self, rule_id: str) -> None:
        del self.rules[self._rule_index(rule_id)]
        self._rules_changed()

    def reorder_rules(self, ids: list[str]) -> None:
        """Put the rules in this order. Every rule must be listed exactly once."""
        if len(set(ids)) != len(ids) or sorted(ids) != sorted(r.id for r in self.rules):
            raise ValueError("list every rule id exactly once")
        by_id = {r.id: r for r in self.rules}
        self.rules = [by_id[i] for i in ids]
        self._rules_changed()

    def _rule_index(self, rule_id: str) -> int:
        for index, rule in enumerate(self.rules):
            if rule.id == rule_id:
                return index
        raise NotFound(f"no rule {rule_id!r}")

    # Overrides, brightness, mode

    def set_override(self, port: Any, color: Any, brightness: Any = None) -> None:
        port = self._port(port)
        self.overrides[port] = Override(Color.parse(color), None if brightness is None else percent(brightness))
        self._changed()

    def set_overrides(self, mapping: dict) -> None:
        staged = {}
        for port, spec in mapping.items():
            b = spec.get("brightness")
            staged[self._port(port)] = Override(Color.parse(spec["color"]), None if b is None else percent(b))
        self.overrides.update(staged)
        self._changed()

    def clear_override(self, port: Any = None) -> None:
        if port is None:
            self.overrides.clear()
        else:
            self.overrides.pop(self._port(port), None)
        self._changed()

    def set_brightness(self, value: Any) -> None:
        self.brightness = percent(value)
        self._invalidate_sfp_intent()
        self._changed()

    def _invalidate_sfp_intent(self) -> None:
        for port in self.switch.sfp_ports:
            if self._sfp_requires_output(port):
                self._sfp_revisions[port] += 1
        self._sfp_changed.set()

    def _output_uncertain(self) -> bool:
        return (bool(self.switch.output_lease and self.switch.output_lease.uncertain)
                or self.switch.native_uncertain or any(self.switch.sfp_status(port)["state"] == "uncertain"
                                                       for port in self.sfp))

    def set_sfp(self, port: int, changes: dict) -> None:
        if type(port) is not int or port not in self.switch.sfp_ports:
            raise ValueError("SFP lighting is unavailable for this port")
        if not changes:
            raise ValueError("provide an SFP lighting setting")
        updated = self.sfp[port].updated(changes)
        if port in self._sfp_zones and set(changes) != {"brightness"}:
            raise ValueError("Remove this SFP port from its effect group before changing its indicator settings")
        self.sfp[port] = updated
        affected = {port}
        if self.sfp_sync.mode == "together" and port == self._sfp_leader:
            affected.update(p for p in self.switch.sfp_ports if p not in self._sfp_zones)
        self._revise_sfp_outputs(affected)
        self._sync_sfp_rules()
        self._changed()

    def set_sfp_sync(self, changes: dict) -> None:
        if len(self.switch.sfp_ports) < 2:
            raise ValueError("SFP synchronization is unavailable for this switch")
        if not changes:
            raise ValueError("provide an SFP synchronization setting")
        updated = self.sfp_sync.updated(changes)
        if (updated.mode == "ethernet" or self.num_ports) and updated.source_port > self.num_ports:
            raise ValueError("SFP source must be an available Ethernet port")
        self.sfp_sync = updated
        self._revise_sfp_outputs(set(self.switch.sfp_ports) - self._sfp_zones.keys())
        self._sync_sfp_rules()
        self._changed()

    async def wait_for_sfp_sync(self) -> None:
        for port in self.switch.sfp_ports:
            await self.wait_for_sfp(port)

    @property
    def _sfp_leader(self) -> int:
        return min(self.sfp)

    def _sfp_light(self, port: int) -> SfpLight:
        return self.sfp[self._sfp_leader if self.sfp_sync.mode == "together" else port]

    def _sfp_color(self, port: int, elapsed: float) -> tuple[Color, float]:
        if self._identifying(port):
            return self._identify_color(), 100.0
        light = self._sfp_light(port)
        follows_ethernet = self.sfp_sync.mode == "ethernet"
        grouped = port in self._sfp_zones
        if grouped:
            color, brightness = self._sfp_zone_colors.get(port, BLACK), self.brightness
        elif follows_ethernet:
            color, brightness = (self._identify_base if self._identifying(self.sfp_sync.source_port)
                                 and self._identify_base is not None else self._shown[self.sfp_sync.source_port - 1])
        else:
            color, brightness = light.render(elapsed, port), self.brightness
        rule = self._sfp_rule(port)
        if rule is not None:
            color = rule.look.render(elapsed, color)
            if rule.look.brightness is not None:
                brightness = rule.look.brightness
        if grouped:
            return color, brightness * self.sfp[port].brightness / 100
        return color, brightness if follows_ethernet else brightness * light.brightness / 100

    @staticmethod
    def _display_rgb(color: Color, brightness: float) -> list[float]:
        """Display the composed color before the switch's output transfer curve."""
        scale = max(0.0, min(100.0, brightness)) / 100
        return [channel * scale for channel in color.precise_channels]

    def sfp_outputs(self) -> dict[int, PortOutput]:
        elapsed = timing.monotonic() - self._t0
        outputs = {}
        native = {}
        for port in self.switch.sfp_ports:
            if port in self._sfp_releasing or not self._sfp_requires_output(port):
                self._sfp_playback.discard(port)
                continue
            spec = self._sfp_native_spec(port)
            if spec is not None:
                native[port] = spec
                continue
            self._sfp_playback.discard(port)
            color, brightness = self._sfp_color(port, elapsed)
            outputs[port] = to_output(color, brightness, self.dimming, self.gamma,
                                      precise=self.switch.sfp_precise and (port in self._sfp_zones
                                                                           or self._sfp_port_animating(port)))
        if (self.sfp_sync.mode != "independent" and native and set(native) == set(self.switch.sfp_ports)
                and all(spec == native[self._sfp_leader] for spec in native.values())):
            signature, anchors, duration, follow = native[self._sfp_leader]
            outputs.update(self._sfp_playback.plan(
                tuple(self.switch.sfp_ports), signature, anchors, duration, self.switch,
                follow=follow, command_budget=self.command_budget))
        else:
            for port, (signature, anchors, duration, follow) in native.items():
                outputs.update(self._sfp_playback.plan((port,), signature, anchors, duration,
                                                       self.switch, follow=follow, command_budget=self.command_budget))
        return outputs

    def _sfp_native_spec(self, port: int) -> tuple | None:
        if self._identifying(port) or port in self._sfp_zones or not self.switch.sfp_native_gradient:
            return None
        rule = self._sfp_rule(port)
        if rule is not None and rule.look.style != "effect":
            return None
        follows = self.sfp_sync.mode == "ethernet"
        light = self._sfp_light(port)
        if not follows and light.native_duration is None:
            return None
        revision = (tuple(self._sfp_revisions.values()) if self.sfp_sync.mode != "independent"
                    else self._sfp_revisions[port])
        look = tuple(sorted(rule.look.to_dict().items())) if rule is not None else None
        key = (self.sfp_sync, revision, look, self.gamma, self.dimming)
        color, brightness = self._sfp_color(port, timing.monotonic() - self._t0)
        if follows:
            return key, (to_output(color, brightness, self.dimming, self.gamma, precise=True),), 1, True
        anchors = tuple(to_output(color, brightness, self.dimming, self.gamma, precise=True)
                        for color in light.native_anchors())
        return (*key, light, brightness), anchors, light.native_duration, False

    def _sfp_playback_state(self, port: int) -> dict:
        spec = self._sfp_native_spec(port)
        light = self._sfp_light(port)
        kind = ("follow" if spec[3] else "native") if spec is not None else (
            "custom" if self._sfp_port_animating(port) else "steady")
        periods = [] if port in self._sfp_zones else list(NATIVE_PERIODS.get(light.effect, ()))
        return {"kind": kind, "periods": periods,
                "segment_seconds": spec[2] if spec is not None else None}

    def _sfp_transition_state(self, port: int) -> dict | None:
        transition = self.switch.sfp_transition(port)
        if transition is None:
            return None
        return {"duration": transition.duration,
                "elapsed": max(0, min(transition.duration, timing.monotonic() - transition.started_at)),
                "from_rgb16": list(transition.start_rgb), "to_rgb16": list(transition.output.precise_rgb)}

    def _sfp_display(self, port: int, elapsed: float) -> list[float] | None:
        if self._sfp_control(port)["state"] == "uncertain":
            return None
        if self.switch.sfp_transition(port) is not None or self._sfp_native_spec(port) is not None:
            return displayed_rgb(self.switch.sfp_rgb16(port), self.gamma)
        return self._display_rgb(*self._sfp_color(port, elapsed))

    def _sfp_rule(self, port: int) -> Rule | None:
        light = self._sfp_light(port)
        follows_ethernet = self.sfp_sync.mode == "ethernet"
        indicator_active = follows_ethernet or port in self._sfp_zones
        if light.mode == "off" and not indicator_active:
            return None
        # Existing all-copper rules must never silently acquire the SFP indicators.
        rule = first_match([rule for rule in self.rules if rule.ports], self.facts(port))
        if rule is not None and rule.look.style == "effect" and light.mode == "unmanaged" and not indicator_active:
            return None
        return rule

    def _sfp_requires_output(self, port: int) -> bool:
        return (self._identifying(port) or port in self._sfp_zones or self.sfp_sync.mode == "ethernet"
                or self._sfp_light(port).mode != "unmanaged" or self._sfp_rule(port) is not None)

    def _sfp_port_animating(self, port: int) -> bool:
        if self._identifying(port):
            return True
        rule = self._sfp_rule(port)
        if rule is not None and rule.look.style != "effect":
            return rule.look.style in ("pulse", "blink", "flash")
        zone = self._sfp_zones.get(port)
        if zone is not None:
            return zone.effect.category != "static"
        if self.sfp_sync.mode == "ethernet":
            return True
        light = self._sfp_light(port)
        return light.mode == "color" and light.effect != "steady"

    def _sync_sfp_rules(self) -> None:
        for port in self.switch.sfp_ports:
            rule = self._sfp_rule(port)
            rule_id = rule.id if rule is not None else None
            if self._sfp_rule_ids[port] != rule_id:
                self._sfp_rule_ids[port] = rule_id
                self._sfp_revisions[port] += 1
                self._sfp_changed.set()
                if not self._sfp_requires_output(port):
                    self._sfp_release_ports.add(port)
            if self._sfp_requires_output(port):
                self._sfp_release_ports.discard(port)

    def _sfp_animating(self) -> bool:
        return self.mode == "running" and any(
            port not in self._sfp_zones and self._sfp_port_animating(port)
            and (self.sfp_sync.mode == "ethernet" or self._sfp_light(port).brightness > 0)
            and port not in self._sfp_releasing
            for port in self.switch.sfp_ports
        ) and self.switch.sfp_precise

    def _sfp_control(self, port: int) -> dict:
        status = dict(self.switch.sfp_status(port))
        if (status["state"] != "uncertain"
                and (port in self._sfp_release_ports or port in self._sfp_releasing
                     or self._sfp_acknowledged[port] != self._sfp_revisions[port])):
            status["state"] = "pending"
        return status

    def sfp_state(self) -> list[dict]:
        outputs = self.sfp_outputs()
        elapsed = timing.monotonic() - self._t0
        return [
            {"port": port, **self.sfp[port].to_dict(),
             "effect_zone": self._sfp_zones[port].id if port in self._sfp_zones else None,
             "effective_settings": (None if port in self._sfp_zones or self.sfp_sync.mode == "ethernet"
                                    else self._sfp_light(port).to_dict()),
             "playback": self._sfp_playback_state(port), "native_transition": self._sfp_transition_state(port),
             "control": self._sfp_control(port), "revision": self._sfp_revisions[port],
             "acknowledged_revision": self._sfp_acknowledged[port],
             "link": self.links[port].link_up if port in self.links else None,
             "anomaly": bool(self.links[port].anomaly) if port in self.links else None,
             "rule": {"id": rule.id, "name": rule.name} if (rule := self._sfp_rule(port)) else None,
             "preview": outputs[port].preview().to_hex() if port in outputs else None,
             "display_rgb": self._sfp_display(port, elapsed) if port in outputs else None,
             "facts": {k: v for k, v in vars(self.facts(port)).items() if k not in ("port", "now")}}
            for port in self.switch.sfp_ports
        ]

    async def _release_sfp_port(self, port: int) -> None:
        async with self._native_quiesce_lock:
            if port not in self._sfp_release_ports or self._sfp_requires_output(port):
                return
            revision = self._sfp_revisions[port]
            self._sfp_releasing.add(port)
            try:
                await self._write_idle.wait()
                if self._sfp_requires_output(port) or self._sfp_revisions[port] != revision:
                    return
                await self.switch.release_sfp(port)
                if not self._sfp_requires_output(port) and self._sfp_revisions[port] == revision:
                    self._sfp_acknowledged[port] = revision
                    self._sfp_release_ports.discard(port)
            except (TransportError, asyncio.CancelledError) as exc:
                self._native_blocked = True
                self.mode = "released"
                self.error = str(exc) or "SFP release was interrupted before confirmation"
                self._output_halt.set()
                raise
            finally:
                self._sfp_releasing.discard(port)
                self._sfp_changed.set()
                self._dirty.set()

    async def wait_for_sfp(self, port: int) -> None:
        revision = self._sfp_revisions[port]
        if not self._sfp_requires_output(port):
            await self._release_sfp_port(port)
        deadline = timing.monotonic() + 5.0
        while True:
            if self._sfp_revisions[port] != revision:
                raise ValueError("SFP change was superseded by a newer request")
            if self._sfp_control(port)["state"] == "uncertain" or self._native_blocked:
                raise TransportError(self.error or "SFP lighting could not be confirmed")
            if self._sfp_acknowledged[port] == revision:
                return
            if not self.connected:
                raise TransportError(self.error or "the switch is disconnected")
            if self.mode != "running":
                return  # Requested intent remains pending until output resumes.
            remaining = deadline - timing.monotonic()
            if remaining <= 0:
                return  # Preserve explicit pending state instead of echoing an acknowledgment.
            self._sfp_changed.clear()
            try:
                await asyncio.wait_for(self._sfp_changed.wait(), remaining)
            except TimeoutError:
                return

    def set_fps(self, value: Any) -> None:
        fps = bounded_number(value, 1, 30, "frame rate")
        self._animation_clock()
        self.fps = fps
        self._refresh_budget()
        self._changed()

    def set_output(self, *, gamma: Any = None, transition: Any = None, max_commands: Any = None) -> None:
        """Advanced output settings. Each one is optional; the others keep their values."""
        values = {
            "gamma": self.gamma if gamma is None else bounded_number(gamma, 1, 3, "gamma"),
            "transition": self.transition if transition is None else bounded_number(transition, 0, 5, "transition"),
            "max_commands": self.max_commands if max_commands is None else
                bounded_number(max_commands, 5, self.MAX_COMMANDS, "command limit"),
        }
        self._animation_clock()
        if self.gamma != values["gamma"]:
            self._invalidate_sfp_intent()
        self.gamma, self.transition, self.max_commands = values.values()
        self._refresh_budget()
        self.switch.forget()
        self._changed()

    def release(self) -> None:
        """Halt new frames; wait_for_output_idle also ends any native animation."""
        self.mode = "released"
        self.stop_identify()
        self._output_halt.set()
        self._native_release_pending |= self._native_engaged or self.switch.native_uncertain
        self._sfp_release_pending |= self._sfp_engaged or any(
            self.switch.sfp_status(port)["owned"] or self.switch.sfp_status(port)["state"] == "uncertain"
            for port in self.sfp)
        self._sfp_changed.set()
        self.switch.forget()
        self._changed()

    async def wait_for_output_idle(self) -> None:
        """Drain the submitted frame and acknowledge steady output on native animation ports."""
        await self._write_idle.wait()
        await self._initialization_idle.wait()
        async with self._native_quiesce_lock:
            if not self._native_release_pending and not self._sfp_release_pending:
                return
            outputs = [to_output(color, brightness, self.dimming, self.gamma, precise=self.switch.precise_rgb)
                       for color, brightness in self._shown]
            try:
                if self._native_release_pending:
                    await self.switch.quiesce_native(outputs)
                if self._sfp_release_pending:
                    await self.switch.quiesce_sfp()
            except (TransportError, asyncio.CancelledError) as exc:
                self._native_blocked = True
                self.mode = "released"
                self.error = str(exc) or "lighting release was interrupted before confirmation"
                self._output_halt.set()
                self._publish(self.state_message())
                self._sfp_changed.set()
                raise
            self._native_release_pending = self._native_engaged = False
            self._sfp_release_pending = self._sfp_engaged = False
            for port in self.sfp:
                if not self._sfp_requires_output(port):
                    self._sfp_acknowledged[port] = self._sfp_revisions[port]
                    self._sfp_release_ports.discard(port)
                else:
                    self._sfp_revisions[port] += 1
                    self._sfp_acknowledged[port] = -1
            self._sfp_changed.set()

    def resume(self) -> None:
        if self._stopping:
            raise TransportError("The lighting session is closing. Reconnect after shutdown completes.")
        if self._native_blocked or self._output_uncertain():
            raise TransportError("Reconnect before resuming lighting; "
                                 "the previous animation could not be confirmed stopped")
        if self._reconnect_blocked:
            raise TransportError("Reconnect your switch before resuming lighting")
        if self._native_release_pending or self._sfp_release_pending:
            raise ValueError("lighting release is still finishing")
        self.mode = "running"
        self._output_halt = asyncio.Event()
        self.switch.forget()
        self._sfp_changed.set()
        self._changed()

    async def recover_connection(self) -> None:
        """Revalidate and clean up owned lighting before resuming the retained scene."""
        if getattr(self, "_recovering", False):
            raise TransportError("Switch recovery is already in progress.")
        if self._stopping:
            raise TransportError("Finish closing this lighting session before reconnecting.")
        self._recovering = True
        try:
            await self._recover_connection()
        finally:
            self._recovering = False

    async def _recover_connection(self) -> None:
        self.release()
        self.connected = False
        self._native_blocked = True
        self._reconnect_blocked = True
        self._publish(self.state_message())
        await self._write_idle.wait()
        await self._initialization_idle.wait()
        async with self._native_quiesce_lock:
            outputs = [to_output(color, brightness, self.dimming, self.gamma, precise=True)
                       for color, brightness in self._shown]
            try:
                info = await self.switch.recover_output(outputs)
            except BaseException as exc:
                self.error = "Lighting recovery is not confirmed. Check the connection and try Reconnect switch again."
                self._reconnect_blocked = True
                self._publish(self.state_message())
                raise TransportError(self.error) from exc
            self.switch_info = info
            self._native_release_pending = self._native_engaged = False
            self._sfp_release_pending = self._sfp_engaged = False
            self._native_blocked = self._reconnect_blocked = False
            self._reconnect_failures = 0
            self.connected, self.error = True, None
            self._invalidate_sfp_intent()
            if not self._stopping:
                self.resume()
                if self._tasks and not any(
                    task.get_name() == "ee-connection" and not task.done() for task in self._tasks
                ):
                    self._tasks.append(asyncio.create_task(self._keep_connected(), name="ee-connection"))

    # Scenes (presets, autosave)

    def _telemetry_ports(self) -> tuple[int, ...]:
        profile = getattr(self.switch, "hardware_profile", None)
        if profile is not None:
            return (*profile.copper_ports, *profile.optical_ports, *profile.stack_ports)
        return (*range(1, self.num_ports + 1), *self.switch.sfp_ports)

    def _validate_switch_topology(self) -> None:
        model = getattr(getattr(self.switch, "hardware_profile", None), "key", None)
        if self.switch.num_ports != self.num_ports or (self._profile_model and model != self._profile_model):
            raise ValueError("The switch layout changed. Start a new lighting session for this device.")
        ports = tuple(self.switch.sfp_ports)
        if ports != self._output_sfp_ports:
            if self._output_sfp_ports or self.switch_info or (self.sfp and set(ports) != set(self.sfp)):
                raise ValueError("The available SFP lights changed. Start a new lighting session for this device.")
            for port in ports:
                self.sfp.setdefault(port, SfpLight())
                self._sfp_revisions.setdefault(port, 0)
                self._sfp_acknowledged.setdefault(port, 0)
                self._sfp_rule_ids.setdefault(port, None)
            self._output_sfp_ports = ports
        self._profile_model = model or self._profile_model
        self._bandwidth.set_physical_ports(self._telemetry_ports())

    def scene_topology(self) -> dict:
        return {"version": 1, "model": self._profile_model,
                "copper_ports": list(range(1, self.num_ports + 1)), "sfp_ports": sorted(self.sfp)}

    def _validate_scene_topology(self, scene: dict) -> None:
        if not isinstance(scene, dict):
            raise ValueError("a scene must be an object")
        version = scene.get("version", 2 if "zones" in scene else 1)
        if type(version) is not int or version not in (1, 2, 3):
            raise ValueError("unsupported scene version")
        if version < 3:
            if "topology" in scene:
                raise ValueError("scene topology requires version 3")
            # Legacy scenes used 9/10 for SFP; those IDs can be copper on other models.
            if version == 2 and (self.num_ports != 8 or set(self.sfp) != {9, 10}):
                raise ValueError("This legacy scene belongs to an eight-port switch. Save a profile for this layout.")
            if version == 1 and any(key in scene for key in ("zones", "sfp", "sfp_sync")):
                raise ValueError("invalid legacy scene layout")
            if version == 1 and self.num_ports != 8 and (scene.get("overrides") or scene.get("rules")):
                raise ValueError("Legacy port settings require an eight-port switch.")
            return
        topology = scene.get("topology")
        if not isinstance(topology, dict) or set(topology) != {"version", "model", "copper_ports", "sfp_ports"}:
            raise ValueError("a scene requires its saved switch topology")
        if type(topology["version"]) is not int or topology["version"] != 1:
            raise ValueError("unsupported scene topology version")
        model = topology["model"]
        if model is not None and (not isinstance(model, str) or not model.strip()):
            raise ValueError("invalid scene switch model")
        for kind in ("copper_ports", "sfp_ports"):
            ports = topology[kind]
            if (not isinstance(ports, list) or any(type(port) is not int or not 1 <= port <= 64 for port in ports)
                    or len(ports) != len(set(ports))):
                raise ValueError("invalid scene port topology")
        if set(topology["copper_ports"]) & set(topology["sfp_ports"]):
            raise ValueError("a scene port cannot be both Ethernet and SFP")
        current = self.scene_topology()
        if (any(sorted(topology[kind]) != current[kind] for kind in ("copper_ports", "sfp_ports"))
                or (model is not None and current["model"] is not None and model != current["model"])):
            raise ValueError("This scene was saved for a different switch layout.")

    def snapshot(self) -> dict:
        return {
            "version": 3,
            "topology": self.scene_topology(),
            "zones": [z.to_dict() for z in self.zones],
            "rules": [r.to_dict() for r in self.rules],
            "brightness": self.brightness,
            "overrides": {str(p): o.to_dict() for p, o in sorted(self.overrides.items())},
            "sfp": {str(p): light.to_dict() for p, light in self.sfp.items()},
            "sfp_sync": self.sfp_sync.to_dict(),
        }

    def _prepare_snapshot(self, scene: dict) -> tuple[
        list[Zone], list[Rule], dict[int, Override], int, dict[int, SfpLight], SfpSync
    ]:
        """Parse a scene without changing the active lighting or output signals."""
        self._validate_scene_topology(scene)
        if "zones" in scene:
            zones = self._build_zones(scene["zones"])
        else:  # version 1: a single effect for all ports
            if scene.get("version", 1) != 1 or not self.num_ports:
                raise ValueError("a scene requires zones for this switch layout")
            everything = tuple(range(1, self.num_ports + 1))
            zones = [Zone("all", "All ports", everything,
                          self._scene_effect(scene.get("effect"), self.num_ports, scene.get("params", {})))]
        self._validate_native_zones(zones, require_capability=self.connected)
        rules = self._parse_rules(scene.get("rules", [r.to_dict() for r in self.rules]))
        overrides = {}
        saved_overrides = scene.get("overrides", {})
        if not isinstance(saved_overrides, dict):
            raise ValueError("port colors must be an object")
        for port, spec in saved_overrides.items():
            if not isinstance(spec, dict) or "color" not in spec:
                raise ValueError("a port color must contain a color setting")
            b = spec.get("brightness")
            number = self._port(port)
            if number in overrides:
                raise ValueError("duplicate port color")
            try:
                overrides[number] = Override(Color.parse(spec["color"]), None if b is None else percent(b))
            except (TypeError, KeyError, AttributeError, OverflowError) as exc:
                raise ValueError("invalid port color settings") from exc
        brightness = percent(scene.get("brightness", self.brightness))
        sfp = {port: SfpLight() for port in self.sfp}
        saved_sfp = scene.get("sfp", {})
        if not isinstance(saved_sfp, dict) or set(saved_sfp) - {str(port) for port in sfp}:
            raise ValueError("invalid SFP lighting ports")
        for port, settings in saved_sfp.items():
            sfp[int(port)] = sfp[int(port)].updated(settings)
        sfp_sync = SfpSync().updated(scene.get("sfp_sync", {}))
        if (sfp_sync.mode == "ethernet" or self.num_ports) and sfp_sync.source_port > self.num_ports:
            raise ValueError("SFP source must be an available Ethernet port")
        if sfp_sync.mode != "independent" and len(self.sfp) < 2:
            raise ValueError("SFP synchronization is unavailable for this switch")
        if self.connected and sfp_sync.mode != "independent" and len(self.switch.sfp_ports) < 2:
            raise ValueError("SFP synchronization is unavailable for this switch")

        return zones, rules, overrides, brightness, sfp, sfp_sync

    def validate_snapshot(self, scene: dict) -> dict:
        """Return a canonical saved scene without applying it."""
        zones, rules, overrides, brightness, sfp, sfp_sync = self._prepare_snapshot(scene)
        return {
            "version": 3,
            "topology": self.scene_topology(),
            "zones": [zone.to_dict() for zone in zones],
            "rules": [rule.to_dict() for rule in rules],
            "brightness": brightness,
            "overrides": {str(port): override.to_dict() for port, override in sorted(overrides.items())},
            "sfp": {str(port): light.to_dict() for port, light in sfp.items()},
            "sfp_sync": sfp_sync.to_dict(),
        }

    def apply_snapshot(self, scene: dict, *, preserve_unchanged: bool = False) -> set[int]:
        """Load a scene. Everything is validated before anything changes."""
        zones, rules, overrides, brightness, sfp, sfp_sync = self._prepare_snapshot(scene)
        clock = self._animation_clock()
        previous_sfp_output = {port: self._sfp_requires_output(port) for port in self.sfp}
        previous_revisions = dict(self._sfp_revisions)
        affected_sfp = set(self.sfp)
        if preserve_unchanged:
            affected_sfp = {
                port for port in self.sfp
                if brightness != self.brightness or sfp_sync != self.sfp_sync
                or sfp[self._sfp_leader if sfp_sync.mode == "together" else port] != self._sfp_light(port)
                or [rule.to_dict() for rule in rules if rule.ports and port in rule.ports]
                != [rule.to_dict() for rule in self.rules if rule.ports and port in rule.ports]
            }
        existing = {zone.id: zone for zone in self.zones}
        for zone in zones:
            zone.started_at = clock
            previous = existing.get(zone.id)
            if (preserve_unchanged and previous is not None and previous.ports == zone.ports
                    and previous.effect.id == zone.effect.id
                    and previous.effect.params_json() == zone.effect.params_json()):
                zone.effect = previous.effect
                zone.started_at = previous.started_at
                zone.wall_started_at = previous.wall_started_at
        affected_sfp.update(self._replace_zones(zones))
        affected_sfp.update(port for port in self._sfp_zones if sfp[port].brightness != self.sfp[port].brightness)
        self.rules, self.overrides, self.brightness = rules, overrides, brightness
        self.sfp, self.sfp_sync = sfp, sfp_sync
        for port in affected_sfp:
            self._sfp_revisions[port] += 1
            if self._sfp_requires_output(port):
                self._sfp_release_ports.discard(port)
            elif previous_sfp_output[port]:
                self._sfp_release_ports.add(port)
            else:
                self._sfp_acknowledged[port] = self._sfp_revisions[port]
        self._sync_sfp_rules()
        affected_sfp |= {port for port in self.sfp if self._sfp_revisions[port] != previous_revisions[port]}
        for port in affected_sfp & set(self.switch.sfp_ports):
            if self._sfp_requires_output(port):
                self._sfp_acknowledged[port] = -1
        if affected_sfp:
            self._sfp_changed.set()
        if not preserve_unchanged:
            self._animation_origin = clock
            self._t0 = timing.monotonic()
        self._changed()
        return affected_sfp & set(self.switch.sfp_ports)

    # What the UI sees

    def facts(self, port: int, now: datetime | None = None) -> PortFacts:
        link = self.links.get(port)
        poe = self.poe.get(port)
        traffic = self.traffic.get(port)
        return PortFacts(
            port=port,
            now=now or datetime.now(),
            link=link.link_up if link else None,
            speed=float(link.speed_mbps) if link else None,
            full_duplex=link.full_duplex if link and link.link_up else None,
            uplink=link.uplink if link else None,
            anomaly=bool(link.anomaly) if link else None,
            stp_state=link.stp_state if link else None,
            lag=bool(link.lag) if link else None,
            traffic=traffic / 1e6 if traffic is not None else None,
            poe=poe.tier if poe else None,
            poe_power=poe.power_w if poe else None,
        )

    def state(self) -> dict:
        return {
            "schedule_clock": self.schedule_clock(),
            "identify": self.identify_state(),
            "telemetry": self.telemetry_state(),
            "poe_summary": self.poe_summary(),
            "history_error": self.history_error,
            "mode": self.mode,
            "connected": self.connected,
            "error": self.error,
            "reconnect_required": self._reconnect_blocked or self._native_blocked or self._output_uncertain(),
            "switch": self.switch_info,
            "hardware_profile": (self.switch.hardware_profile.public_dict()
                                 if getattr(self.switch, "hardware_profile", None) is not None else None),
            "num_ports": self.num_ports,
            "sfp": self.sfp_state(),
            "sfp_sync": self.sfp_sync.to_dict(),
            "fps": self.fps,
            "brightness": self.brightness,
            "dimming": self.dimming,
            "output": {"gamma": self.gamma, "transition": self.transition, "max_commands": self.max_commands},
            "zones": [z.to_dict() for z in self.zones],
            "rules": [r.to_dict() for r in self.rules],
            "overrides": {str(p): o.to_dict() for p, o in sorted(self.overrides.items())},
            "revision": self.revision,
            "stats": self.output_stats(),
            "uplink_bandwidth": self._bandwidth.snapshot(now=timing.monotonic(), connected=self.connected),
            "fans": self.fans.to_dict() if self.fans is not None else None,
        }

    @staticmethod
    def schedule_clock() -> dict:
        local = datetime.now().astimezone()
        return {"local_iso": local.isoformat(timespec="seconds"), "zone_name": local.tzname() or "Local time"}

    def output_stats(self) -> dict:
        stats = self.switch.stats
        now = timing.monotonic()
        ack = self.switch.ack_latency(now)
        return {
            "frames": self.frames,
            "commands": self.commands,
            "commands_per_second": round(self.command_rate(), 1),
            "command_budget": round(self.command_budget, 1),
            "direct_rgb": self.switch.transport.direct_rgb,
            "ack_p95_ms": None if ack["p95"] is None else round(ack["p95"] * 1000, 3),
            "cpu_sample_age_ms": None if self._cpu_updated_at is None else
                round(max(0.0, now - self._cpu_updated_at) * 1000, 1),
            "switch_cpu_idle": None if self.cpu_idle is None else round(self.cpu_idle, 2),
            "switch_load": self.cpu_load,
            "switch_health": self._switch_health,
            "health_sample_age_ms": None if self._health_updated_at is None else
                round(max(0.0, now - self._health_updated_at) * 1000, 1),
            "health_error": self._health_error,
            "last_write_ms": round(stats.last_write_ms, 2),
            "avg_write_ms": round(stats.avg_write_ms, 2),
        }

    def ports(self) -> list[dict]:
        now = datetime.now()
        result = []
        for i in range(self.num_ports):
            port = i + 1
            link, poe, output = self.links.get(port), self.poe.get(port), self.last_outputs[i]
            facts = self.facts(port, now)
            result.append({
                "port": port,
                "color": self.last_colors[i].to_hex(),
                "output": {"color": output.color.to_hex(), "level": output.level},
                "preview": output.preview().to_hex(),
                "display_rgb": self._display_rgb(*self._shown[i]),
                "source": self.last_sources[i],
                "override": self.overrides[port].to_dict() if port in self.overrides else None,
                "link": link.to_dict() if link else None,
                "poe": poe.to_dict() if poe else None,
                "facts": {k: v for k, v in vars(facts).items() if k not in ("port", "now")},
                "traffic_bps": round(self.traffic[port]) if port in self.traffic else None,
            })
        return result

    def state_message(self) -> dict:
        return {"type": "state", "state": self.state()}

    def frame_message(self) -> dict:
        return {"type": "frame", "mode": self.mode, "connected": self.connected,
                "schedule_clock": self.schedule_clock(),
                "identify": self.identify_state(), "telemetry": self.telemetry_state(),
                "poe_summary": self.poe_summary(), "history_error": self.history_error,
                "error": self.error,
                "reconnect_required": self._reconnect_blocked or self._native_blocked or self._output_uncertain(),
                "stats": self.output_stats(),
                "uplink_bandwidth": self._bandwidth.snapshot(now=timing.monotonic(), connected=self.connected),
                "ports": self.ports(), "sfp": self.sfp_state(), "sfp_sync": self.sfp_sync.to_dict()}

    # Websocket fan-out

    def subscribe(self) -> Mailbox:
        mailbox = Mailbox()
        self._subscribers.add(mailbox)
        return mailbox

    def attach(self, mailbox: Mailbox) -> None:
        self._subscribers.add(mailbox)

    def unsubscribe(self, mailbox: Mailbox) -> None:
        self._subscribers.discard(mailbox)

    def _publish(self, message: dict) -> None:
        for mailbox in list(self._subscribers):
            mailbox.put(message)

    def _changed(self) -> None:
        self._record_history()
        self.revision += 1
        if self.mode == "running":
            self._output_halt.set()
            self._output_halt = asyncio.Event()
        self._dirty.set()
        if self._subscribers:
            self._publish(self.state_message())

    # Rendering

    def _animation_clock(self) -> float:
        """Keep motion sampled consistently when the command budget or CPU load slows full frames."""
        tick = timing.monotonic()
        full_frame_rate = min(self.fps, self.command_budget / max(1, self.num_ports))
        if self._last_write_seconds > 0:
            full_frame_rate = min(full_frame_rate, 1 / self._last_write_seconds)
        scale = min(1.0, full_frame_rate / 3.75)
        self._animation_time += max(0.0, tick - self._animation_updated_at) * scale
        self._animation_updated_at = tick
        return self._animation_time

    def compose(self, t: float | None = None, now: datetime | None = None,
                clock: float | None = None) -> tuple[list[Color], list[PortOutput]]:
        tick = timing.monotonic()
        explicit_time = t
        animation_tick = self._animation_clock() if explicit_time is None else 0.0
        t = tick - self._t0 if t is None else t
        now = now or datetime.now()
        colors = [BLACK] * self.num_ports
        native: dict[int, tuple[Color, str]] = {}
        sources: list[dict] = [{"kind": "none"}] * self.num_ports

        for zone in self.zones:
            ctx = RenderContext(
                num_ports=len(zone.ports),
                links={i + 1: self.links[p] for i, p in enumerate(zone.ports) if p in self.links},
                traffic_bps={i + 1: self.traffic[p] for i, p in enumerate(zone.ports) if p in self.traffic},
                poe_power_w={i + 1: self.poe[p].power_w for i, p in enumerate(zone.ports)
                             if p in self.poe and self.poe[p].delivering},
                now=now,
            )
            try:
                effect_time = animation_tick - zone.started_at if explicit_time is None else explicit_time
                if explicit_time is None and zone.effect.category in {"time", "data"}:
                    effect_time = tick - zone.wall_started_at
                rendered = list(zone.effect.render(effect_time, ctx))
            except Exception:
                log.exception("effect %s in zone %s failed", zone.effect.id, zone.id)
                rendered = []
            rendered = (rendered + [BLACK] * len(zone.ports))[: len(zone.ports)]
            for port, color in zip(zone.ports, rendered, strict=True):
                if port in self._sfp_zones:
                    self._sfp_zone_colors[port] = color
                    continue
                colors[port - 1] = color
                sources[port - 1] = {"kind": "zone", "id": zone.id, "name": zone.name}
                if self._synchronized(zone.effect) and self.switch.native_breathing:
                    base = zone.effect.p["color"].scaled(zone.effect.p.get("brightness_color", 100) / 100)
                    selector = {1: "breath_3s", 2: "breath_2s", 3: "breath_1s"}[zone.effect.p["pace"]]
                    native[port] = base, selector

        if clock is None:
            clock = animation_tick if explicit_time is None else timing.monotonic()
        outputs = []
        for i in range(self.num_ports):
            port = i + 1
            brightness: float = self.brightness
            key: tuple = ("zone", sources[i].get("id"), self._effect_of(port))
            rule = first_match(self.rules, self.facts(port, now)) if self.rules else None
            if rule is not None:
                rule_time = t if explicit_time is not None else animation_tick - self._animation_origin
                colors[i] = rule.look.render(rule_time, colors[i])
                if rule.look.brightness is not None:
                    brightness = rule.look.brightness
                sources[i] = {"kind": "rule", "id": rule.id, "name": rule.name}
                key = ("rule", rule.id, rule.look.color, rule.look.style, rule.look.rate, rule.look.brightness)
            override = self.overrides.get(port)
            if override is not None:
                colors[i] = override.color
                if override.brightness is not None:
                    brightness = override.brightness
                sources[i] = {"kind": "override"}
                key = ("override",)
            if self._identifying(port):
                self._identify_base = (colors[i], brightness)
                colors[i], brightness = self._identify_color(), 100.0
                sources[i] = {"kind": "identify"}
                self._keys[i] = ("identify",)
                self._fades.pop(i, None)
                self._shown[i] = (colors[i], brightness)
                outputs.append(to_output(colors[i], brightness, self.dimming, self.gamma,
                                         precise=self.switch.precise_rgb))
            elif port in native and rule is None and override is None:
                self._keys[i] = ("native", sources[i].get("id"), self._effect_of(port))
                self._fades.pop(i, None)
                self._shown[i] = (colors[i], brightness)
                base, selector = native[port]
                output = to_output(base, brightness, self.dimming, self.gamma, precise=True)
                outputs.append(PortOutput(output.color, output.level, output.precise_rgb, breath=selector))
            else:
                colors[i], brightness = self._crossfade(i, key, colors[i], brightness, clock)
                outputs.append(to_output(colors[i], brightness, self.dimming, self.gamma,
                                         precise=self.switch.precise_rgb))

        self.last_sources = sources
        return colors, outputs

    def _effect_of(self, port: int) -> int | None:
        zone = next((z for z in self.zones if port in z.ports), None)
        return id(zone.effect) if zone else None

    def _crossfade(self, i: int, key: tuple, color: Color, brightness: float, clock: float) -> tuple[Color, float]:
        """Fade a port to its new color when what drives it changes (a new effect, a rule starting or ending).

        Manual colors apply at once so the color picker stays instant, and the first frame has nothing to fade from.
        """
        previous, self._keys[i] = self._keys[i], key
        if previous is not None and previous[0] == "native":
            self._fades.pop(i, None)
            previous = None
        if previous is not None and key != previous:
            if self.transition > 0 and key != ("override",):
                shown_color, shown_brightness = self._shown[i]
                self._fades[i] = (shown_color, shown_brightness, clock)
            else:
                self._fades.pop(i, None)
        fade = self._fades.get(i)
        if fade is not None:
            start_color, start_brightness, started = fade
            progress = (clock - started) / self.transition if self.transition > 0 else 1.0
            if progress >= 1.0:
                del self._fades[i]
            else:
                # Blend at full strength and fade the brightness separately, so a dim port fading to a bright one
                # doesn't dip through a darker mix on the way.
                p = ease(progress)
                color = start_color.lerp(color, p)
                brightness = start_brightness + (brightness - start_brightness) * p
        self._shown[i] = (color, brightness)
        return color, brightness

    def command_rate(self, window: float = 5.0) -> float:
        cutoff = timing.monotonic() - window
        self._sent = [(at, n) for at, n in self._sent if at >= cutoff]
        return sum(n for _, n in self._sent) / window

    def _budget_ceiling(self) -> float:
        transport_limit = self.MAX_COMMANDS if self.switch.transport.direct_rgb else self.INITIAL_COMMANDS
        active_ports = self.num_ports or len(self.switch.sfp_ports) or 1
        return min(self.max_commands, transport_limit, active_ports * self.fps)

    @property
    def command_budget(self) -> float:
        return min(self._command_budget, self._budget_ceiling())

    @property
    def throttle(self) -> float:
        return self.command_budget / min(self.max_commands, self._budget_ceiling())

    def _set_command_budget(self, value: float) -> None:
        value = max(min(5.0, self._budget_ceiling()), min(value, self._budget_ceiling()))
        if value == self._command_budget:
            return
        before = self._command_budget
        self._animation_clock()
        self._command_budget = value
        self._budget_changed_at = timing.monotonic()
        self._healthy_cpu_samples = 0
        if value < before and self.mode == "running":
            self._output_halt.set()
            self._output_halt = asyncio.Event()
            self._dirty.set()

    def _refresh_budget(self) -> None:
        """Expire ramp evidence and reduce pressure without waiting for another CPU poll."""
        now = timing.monotonic()
        ack = self.switch.ack_latency(now)
        fresh_cpu = self._cpu_updated_at is not None and 0 <= now - self._cpu_updated_at <= self.TELEMETRY_AGE
        fresh_ack = ack["last_at"] is not None and 0 <= now - ack["last_at"] <= self.TELEMETRY_AGE
        if not fresh_cpu or (self.switch.transport.direct_rgb and not fresh_ack):
            self._healthy_cpu_samples = 0
            self._set_command_budget(min(self.command_budget, self.INITIAL_COMMANDS))
        else:
            self._set_command_budget(self.command_budget)
        if (self.switch.transport.direct_rgb and fresh_ack and ack["p95"] is not None
                and ack["p95"] > 1 / self.command_budget):
            self._set_command_budget(self.command_budget / 2)

    def _current_command_budget(self) -> float:
        self._refresh_budget()
        return self.command_budget

    def _adjust_throttle(self, idle: float | None) -> None:
        """Increase direct output only with fresh headroom, fast acknowledgments, and actual demand."""
        now = timing.monotonic()
        if idle is None or not math.isfinite(idle) or not 0 <= idle <= 1:
            self._cpu_updated_at = None
            self._refresh_budget()
            return
        if self._cpu_updated_at is None or not 0 <= now - self._cpu_updated_at <= self.TELEMETRY_AGE:
            self._healthy_cpu_samples = 0
            self._set_command_budget(min(self.command_budget, self.INITIAL_COMMANDS))
        self._cpu_updated_at = now
        self._refresh_budget()
        if idle < self.BUSY_IDLE:
            self._healthy_cpu_samples = 0
            self._set_command_budget(self.command_budget / 2)
            return
        if idle < self.CALM_IDLE:
            self._healthy_cpu_samples = 0
            return
        self._healthy_cpu_samples += 1
        if self._healthy_cpu_samples < 3 or now - self._budget_changed_at < 2:
            return
        proposed = min(self.command_budget + 15, self._budget_ceiling())
        if self.switch.transport.direct_rgb:
            ack = self.switch.ack_latency(now)
            demand = sum(n for at, n in self._sent if 0 <= now - at <= self.TELEMETRY_AGE) / self.TELEMETRY_AGE
            if (ack["count"] < min(16, max(3, int(self.command_budget * 2))) or ack["p95"] is None
                    or ack["p95"] > 0.5 / proposed or demand < self.command_budget * 0.75):
                self._healthy_cpu_samples = 0
                return
        self._set_command_budget(proposed)

    def _lost(self, exc: Exception) -> None:
        if self.connected:
            log.warning("lost the switch: %s", exc)
        self.connected = False
        self.stop_identify()
        self._telemetry_generation = None
        self._telemetry_token = None
        self._telemetry_updated_at = None
        self._poe_updated_at = None
        self.traffic = {}
        self._meter = TrafficMeter()
        self._output_halt.set()
        self._bandwidth.reset()
        if self._output_uncertain():
            self._native_blocked = True
            self.mode = "released"
        elif self.mode == "running":
            self._output_halt = asyncio.Event()
        self.switch.revoke_precision()
        self.error = str(exc)
        self._cpu_updated_at = None
        self._switch_health = None
        self._health_updated_at = None
        self._healthy_cpu_samples = 0
        self._set_command_budget(min(self.INITIAL_COMMANDS, self._budget_ceiling()))
        self.switch.reset_timing()
        self.switch.forget()
        for port in self.switch.sfp_ports:
            if self._sfp_requires_output(port):
                self._sfp_revisions[port] += 1
                self._sfp_acknowledged[port] = -1
        self._sfp_changed.set()
        self._publish(self.state_message())

    async def _keep_connected(self) -> None:
        while True:
            if self._stopping or self._reconnect_blocked:
                return
            if self._native_blocked or self._output_uncertain():
                self.mode = "released"
                self._output_halt.set()
                await asyncio.sleep(1.0)
                continue
            if not self.connected:
                try:
                    await self.switch.connect()
                    info = await self.switch.probe()
                    if not info.get("port_rgb"):
                        raise ValueError("Per-port lighting is unavailable on this switch connection")
                    self.switch.require_output_profile()
                    self._validate_switch_topology()
                    if self._stopping:
                        return
                    self._initialization_idle.clear()
                    try:
                        await self.switch.initialize_output()
                    finally:
                        self._initialization_idle.set()
                    self._validate_native_zones(self.zones, require_capability=True)
                    self.switch_info = info
                    self.connected, self.error = True, None
                    self._reconnect_failures = 0
                    self.switch.forget()
                    self._changed()
                except Exception as exc:
                    self.connected, self.error = False, str(exc)
                    self._reconnect_failures += 1
                    if self._output_uncertain():
                        self._native_blocked = True
                        self.mode = "released"
                        self._output_halt.set()
                    permanent = isinstance(exc, (AuthenticationError, HostKeyMismatchError))
                    retryable = isinstance(exc, (TransportError, OSError)) and not permanent
                    self._reconnect_blocked = (self._native_blocked or not retryable
                                               or self._reconnect_failures >= self.MAX_RECONNECT_ATTEMPTS)
                    if self._reconnect_blocked:
                        self._output_halt.set()
                        if isinstance(exc, AuthenticationError):
                            self.error = "SSH credentials were not accepted. Reconnect your switch to sign in again."
                        elif isinstance(exc, HostKeyMismatchError):
                            self.error = "The switch identity changed. Verify the device before reconnecting."
                        elif not self._native_blocked:
                            self.error = (
                                "Automatic reconnect stopped. Reconnect your switch to try again."
                                if retryable else f"{exc}. Reconnect your switch after checking its settings."
                            )
                    with contextlib.suppress(Exception):
                        await self.switch.close()
                    self._publish(self.state_message())
                    if self._reconnect_blocked:
                        return
                    ceiling = max(60.0, self.reconnect_delay)
                    delay = min(ceiling, self.reconnect_delay * 2 ** (self._reconnect_failures - 1))
                    await asyncio.sleep(min(ceiling, delay * random.uniform(1.0, 1.2)))
                    continue
            await asyncio.sleep(1.0)

    async def _render_loop(self) -> None:
        next_tick = timing.monotonic()
        next_copper_tick = next_tick
        last_started = float("-inf")
        while not self._stopping:
            await timing.wait(max(0.0, next_tick - timing.monotonic()), self._dirty)
            if self._stopping:
                return
            changed = self._dirty.is_set()
            revision = self.revision
            self._dirty.clear()
            self._sync_sfp_rules()

            # SFP indicators have their own sampling clock. Copper targets retain their configured cadence.
            # Every write still passes through the same adaptive, acknowledged command budget.
            frame_rate = max(self.fps, self.SFP_FRAME_RATE) if self._sfp_animating() else self.fps
            wait = last_started + 1.0 / max(self.MAX_INSTANT_RATE, frame_rate) - timing.monotonic()
            if wait > 0:
                await timing.wait(wait)
            started = timing.monotonic()
            last_started = started
            next_tick = started + 1.0 / frame_rate
            self._refresh_budget()

            if not self._native_blocked and not self._output_uncertain():
                for port in tuple(self._sfp_release_ports):
                    try:
                        await self._release_sfp_port(port)
                    except TransportError as exc:
                        self._lost(exc)
                        break

            copper_due = changed or self.revision != revision or started >= next_copper_tick
            if copper_due:
                self.last_colors, self.last_outputs = self.compose()
                next_copper_tick = started + 1.0 / self.fps
            sent = 0
            attempted = 0
            if self.connected and self.mode == "running":
                self._write_idle.clear()
                before = self.switch.stats.attempted_commands
                try:
                    self._native_engaged |= any(output.breath is not None for output in self.last_outputs)
                    sfp = self.sfp_outputs()
                    sfp_revisions = {port: self._sfp_revisions[port] for port in sfp}
                    self._sfp_engaged |= bool(sfp)
                    sent = await self.switch.write(self.last_outputs,
                                                   max_commands=self.command_budget,
                                                   stop=self._output_halt,
                                                   budget_getter=self._current_command_budget,
                                                   yield_at=self._sfp_playback.next_deadline(
                                                       self.switch, self.command_budget),
                                                   **({"sfp": sfp} if sfp else {}))
                    self.commands += sent
                    self.frames += 1
                    for port, output in sfp.items():
                        if (self._sfp_revisions[port] == sfp_revisions[port]
                                and self.switch.sfp_acknowledged(port, output)):
                            self._sfp_acknowledged[port] = sfp_revisions[port]
                    self._sfp_changed.set()
                except TransportError as exc:
                    self._lost(exc)
                finally:
                    attempted = self.switch.stats.attempted_commands - before
                    if attempted and copper_due:
                        self._last_write_seconds = timing.monotonic() - started
                    self._write_idle.set()
            elif (self.mode == "released" and (self._native_release_pending or self._sfp_release_pending)
                  and not self._native_blocked and not self._output_uncertain()):
                with contextlib.suppress(TransportError):
                    await self.wait_for_output_idle()
            if attempted:
                finished = timing.monotonic()
                self._sent = [(at, n) for at, n in self._sent if finished - at <= 5]
                self._sent.append((finished, attempted))
                self._refresh_budget()
            if self._subscribers:
                self._publish(self.frame_message())

    async def _collect_health(self) -> None:
        generation = getattr(self.switch.transport, "lighting_generation", None)
        try:
            sample = await self.switch.read_health()
        except asyncio.CancelledError:
            raise
        except Exception as exc:
            if self.connected and generation is getattr(self.switch.transport, "lighting_generation", None):
                self._health_error = "invalid_response" if isinstance(exc, HealthParseError) else "unavailable"
            return
        if self.connected and generation is getattr(self.switch.transport, "lighting_generation", None):
            self._switch_health = sample
            self._health_updated_at = timing.monotonic()
            self._health_error = None

    async def _health_loop(self) -> None:
        while True:
            if self.connected:
                await self._collect_health()
            await asyncio.sleep(HEALTH_INTERVAL)

    def attach_fans(self, fans) -> None:
        self.fans = fans

    async def update_fans(self, change: dict) -> None:
        if self.fans is None or not self.connected:
            raise TransportError("Connect to the switch before changing the fans.")
        try:
            await self.fans.update(change)
        finally:
            self._publish_fans()

    def _publish_fans(self) -> None:
        if self.fans is not None and self._subscribers:
            self._publish({"type": "fans", "fans": self.fans.to_dict()})

    async def _fan_loop(self) -> None:
        from .fans import POLL_INTERVAL
        while True:
            if self.connected and self.fans is not None:
                with contextlib.suppress(TransportError):
                    await self.fans.poll()
                self._publish_fans()
            await asyncio.sleep(POLL_INTERVAL)

    async def _status_loop(self) -> None:
        while True:
            if not self.connected:
                await asyncio.sleep(0.2)
                continue
            try:
                generation = getattr(self.switch.transport, "lighting_generation", None)
                links = await self.switch.read_links()
                if not self.connected or generation is not getattr(self.switch.transport, "lighting_generation", None):
                    continue
                sample_time = timing.monotonic()
                if generation is not self._telemetry_generation:
                    self._telemetry_generation = generation
                    self._telemetry_token = secrets.token_hex(12)
                    self._meter = TrafficMeter()
                self._telemetry_updated_at = sample_time
                self._telemetry_sampled_at = datetime.now().astimezone().isoformat(timespec="milliseconds")
                self._telemetry_sequence += 1
                self._bandwidth.update(links, now=sample_time, generation=generation)
                self._bandwidth.publish(now=sample_time)
                measured = self._meter.update(links)
                self.traffic = {port: measured[port] for port in self._meter.valid}
                changed = {p: (k.link_up, k.anomaly) for p, k in links.items()} != {
                    p: (k.link_up, k.anomaly) for p, k in self.links.items()
                }
                self.links = links
                poe = await self.switch.read_poe()
                if not self.connected or generation is not getattr(self.switch.transport, "lighting_generation", None):
                    continue
                self.poe = poe
                self._poe_updated_at = timing.monotonic()
                cpu = await self.switch.read_cpu()
                if cpu is not None:
                    self.cpu_idle = cpu["idle"] if cpu["idle"] is not None else self.cpu_idle
                    self.cpu_load = cpu["load"]
                    self._adjust_throttle(cpu["idle"])
                else:
                    self._adjust_throttle(None)
                if changed:
                    self._dirty.set()  # a cable was pulled: show it now, not on the next frame
            except PortSampleUnavailable:
                # Existing samples age naturally until the next successful read.
                pass
            except TransportError as exc:
                self._lost(exc)
            await asyncio.sleep(self.link_poll)
