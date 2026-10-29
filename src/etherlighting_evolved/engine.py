"""The render engine.

Each frame is built in this order, and each step can override the one before it:

    zones      each zone (a group of ports) runs its own effect
    rules      the first matching rule sets the port's look (offline, PoE, anomaly, time of day, ...)
    overrides  colors set by hand on single ports
    brightness global, unless a rule or override sets its own

Only ports that changed are sent to the switch, plus a full refresh every `keepalive` seconds, because the
switch's own Etherlighting service keeps reapplying its colors. A change made through the API wakes the render
loop immediately instead of waiting for the next frame.
"""

from __future__ import annotations

import asyncio
import contextlib
import logging
import re
import time
from dataclasses import dataclass
from datetime import datetime
from typing import Any

from .color import BLACK, Color
from .effects import create_effect
from .effects.base import Effect, RenderContext
from .links import PortLink, TrafficMeter
from .mailbox import Mailbox
from .poe import PoePort
from .rules import DEFAULT_RULES, PortFacts, Rule, first_match
from .switch import EtherlightSwitch, PortOutput
from .transport import TransportError

log = logging.getLogger(__name__)


class NotFound(KeyError):
    """A zone, rule or schedule id that doesn't exist."""

    def __str__(self) -> str:
        return str(self.args[0]) if self.args else "not found"


def percent(value: Any) -> int:
    try:
        number = float(value)
    except (TypeError, ValueError):
        raise ValueError("brightness must be a number from 0 to 100") from None
    return int(round(min(100.0, max(0.0, number))))


def to_output(color: Color, brightness: int, dimming: str = "level") -> PortOutput:
    """Turn a color and a brightness into what the LED controller receives.

    With "level" dimming the color is normalized to full strength and its own brightness moves into the
    hardware level, which fades more smoothly than very small RGB values. "rgb" sends the color as it is.
    """
    if brightness <= 0 or color.is_off:
        return PortOutput.off()
    if dimming == "rgb":
        return PortOutput(color, brightness)
    peak = color.max_channel
    level = round(brightness * peak / 255)
    if level <= 0:
        return PortOutput.off()
    return PortOutput(color.scaled(255 / peak), level)


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

    def __init__(
        self,
        switch: EtherlightSwitch,
        *,
        fps: float = 10.0,
        link_poll: float = 1.0,
        brightness: int = 100,
        dimming: str = "level",
        effect: str = "static",
        effect_params: dict | None = None,
        rules: list[dict] | None = None,
        reconnect_delay: float = 3.0,
    ) -> None:
        if dimming not in ("level", "rgb"):
            raise ValueError("dimming must be 'level' or 'rgb'")
        self.switch = switch
        self.num_ports = switch.num_ports
        self.fps = min(30.0, max(1.0, float(fps)))
        self.link_poll = max(0.5, float(link_poll))
        self.brightness = percent(brightness)
        self.dimming = dimming
        self.reconnect_delay = reconnect_delay

        everything = tuple(range(1, self.num_ports + 1))
        base = create_effect(effect, self.num_ports, effect_params)
        self.zones: list[Zone] = [Zone("all", "All ports", everything, base)]
        specs = DEFAULT_RULES if rules is None else rules
        self.rules: list[Rule] = [Rule.parse(r, num_ports=self.num_ports) for r in specs]
        self.overrides: dict[int, Override] = {}

        self.mode = "running"  # "released" hands the LEDs back to the switch's own Etherlighting
        self.connected = False
        self.error: str | None = None
        self.switch_info: dict = {}
        self.links: dict[int, PortLink] = {}
        self.poe: dict[int, PoePort] = {}
        self.traffic: dict[int, float] = {}
        self.revision = 0
        self.frames = 0
        self.commands = 0

        self.last_colors: list[Color] = [BLACK] * self.num_ports
        self.last_outputs: list[PortOutput] = [PortOutput.off()] * self.num_ports
        self.last_sources: list[dict] = [{"kind": "zone", "id": "all"}] * self.num_ports

        self._meter = TrafficMeter()
        self._t0 = time.monotonic()
        self._dirty = asyncio.Event()
        self._tasks: list[asyncio.Task] = []
        self._subscribers: set[Mailbox] = set()

    # Lifecycle

    async def start(self) -> None:
        self._tasks = [
            asyncio.create_task(self._keep_connected(), name="ee-connection"),
            asyncio.create_task(self._render_loop(), name="ee-render"),
            asyncio.create_task(self._status_loop(), name="ee-status"),
        ]

    async def stop(self) -> None:
        for task in self._tasks:
            task.cancel()
        for task in self._tasks:
            with contextlib.suppress(asyncio.CancelledError, Exception):
                await task
        self._tasks = []
        with contextlib.suppress(Exception):
            await self.switch.close()
        self.connected = False

    # Zones

    def _port(self, port: Any) -> int:
        port = int(port)
        if not 1 <= port <= self.num_ports:
            raise ValueError(f"ports are numbered 1 to {self.num_ports}")
        return port

    def zone(self, zone_id: str | None = None) -> Zone:
        if zone_id is None:
            return self.zones[0]
        for zone in self.zones:
            if zone.id == zone_id:
                return zone
        raise NotFound(f"no zone {zone_id!r}")

    def _build_zones(self, specs: list[dict]) -> list[Zone]:
        if not specs:
            raise ValueError("there must be at least one zone")
        zones: list[Zone] = []
        claimed: set[int] = set()
        for spec in specs:
            name = str(spec.get("name") or "Zone").strip()[:40]
            ports = tuple(sorted({self._port(p) for p in spec.get("ports") or []}))
            if not ports:
                raise ValueError(f"zone {name!r} has no ports")
            if claimed & set(ports):
                raise ValueError(f"port {min(claimed & set(ports))} is in more than one zone")
            claimed |= set(ports)
            effect_spec = spec.get("effect") or {}
            if isinstance(effect_spec, str):
                effect_spec = {"id": effect_spec}
            effect = create_effect(effect_spec.get("id", "static"), len(ports), effect_spec.get("params") or {})
            zone_id = str(spec.get("id") or "") or _slug(name, {z.id for z in zones})
            if zone_id in {z.id for z in zones}:
                raise ValueError(f"duplicate zone id {zone_id!r}")
            zones.append(Zone(zone_id, name, ports, effect))
        return zones

    def set_zones(self, specs: list[dict]) -> None:
        """Replace all zones. Each spec: {"name", "ports": [...], "effect": {"id", "params"}, optional "id"}.
        Ports left out of every zone stay dark unless a rule or override lights them."""
        self.zones = self._build_zones(specs)
        self._t0 = time.monotonic()
        self._changed()

    def set_effect(self, effect_id: str, params: dict | None = None, zone: str | None = None) -> None:
        target = self.zone(zone)
        target.effect = create_effect(effect_id, len(target.ports), params or {})
        self._t0 = time.monotonic()
        self._changed()

    def update_params(self, params: dict, zone: str | None = None) -> None:
        self.zone(zone).effect.update(params)
        self._changed()

    # Rules

    def set_rules(self, specs: list[dict]) -> None:
        """Replace the whole rule list; its order is the priority (first match wins)."""
        self.rules = [Rule.parse(spec, num_ports=self.num_ports) for spec in specs]
        self._changed()

    def add_rule(self, spec: dict, *, position: int | None = None) -> Rule:
        rule = Rule.parse(spec, num_ports=self.num_ports)
        if rule.id in {r.id for r in self.rules}:
            raise ValueError(f"a rule with id {rule.id!r} already exists")
        self.rules.insert(len(self.rules) if position is None else max(0, position), rule)
        self._changed()
        return rule

    def update_rule(self, rule_id: str, changes: dict) -> Rule:
        index = self._rule_index(rule_id)
        merged = {**self.rules[index].to_dict(), **changes, "id": rule_id}
        self.rules[index] = Rule.parse(merged, num_ports=self.num_ports)
        self._changed()
        return self.rules[index]

    def delete_rule(self, rule_id: str) -> None:
        del self.rules[self._rule_index(rule_id)]
        self._changed()

    def reorder_rules(self, ids: list[str]) -> None:
        """Put the rules in this order. Every rule must be listed exactly once."""
        if sorted(ids) != sorted(r.id for r in self.rules):
            raise ValueError("list every rule id exactly once")
        by_id = {r.id: r for r in self.rules}
        self.rules = [by_id[i] for i in ids]
        self._changed()

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
        self._changed()

    def set_fps(self, value: Any) -> None:
        self.fps = min(30.0, max(1.0, float(value)))
        self._changed()

    def release(self) -> None:
        """Stop driving the LEDs. The switch's own Etherlighting takes over again within a couple of seconds."""
        self.mode = "released"
        self.switch.forget()
        self._changed()

    def resume(self) -> None:
        self.mode = "running"
        self.switch.forget()
        self._changed()

    # Scenes (presets, autosave)

    def snapshot(self) -> dict:
        return {
            "version": 2,
            "zones": [z.to_dict() for z in self.zones],
            "rules": [r.to_dict() for r in self.rules],
            "brightness": self.brightness,
            "overrides": {str(p): o.to_dict() for p, o in sorted(self.overrides.items())},
        }

    def apply_snapshot(self, scene: dict) -> None:
        """Load a scene. Everything is validated before anything changes."""
        if "zones" in scene:
            zones = self._build_zones(scene["zones"])
        else:  # version 1: a single effect for all ports
            everything = tuple(range(1, self.num_ports + 1))
            zones = [Zone("all", "All ports", everything,
                          create_effect(scene["effect"], self.num_ports, scene.get("params") or {}))]
        rules = [Rule.parse(r, num_ports=self.num_ports) for r in scene.get("rules", [r.to_dict() for r in self.rules])]
        overrides = {}
        for port, spec in (scene.get("overrides") or {}).items():
            b = spec.get("brightness")
            overrides[self._port(port)] = Override(Color.parse(spec["color"]), None if b is None else percent(b))
        brightness = percent(scene.get("brightness", self.brightness))

        self.zones, self.rules, self.overrides, self.brightness = zones, rules, overrides, brightness
        self._t0 = time.monotonic()
        self._changed()

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
        stats = self.switch.stats
        return {
            "mode": self.mode,
            "connected": self.connected,
            "error": self.error,
            "switch": self.switch_info,
            "num_ports": self.num_ports,
            "fps": self.fps,
            "brightness": self.brightness,
            "dimming": self.dimming,
            "zones": [z.to_dict() for z in self.zones],
            "rules": [r.to_dict() for r in self.rules],
            "overrides": {str(p): o.to_dict() for p, o in sorted(self.overrides.items())},
            "revision": self.revision,
            "stats": {
                "frames": self.frames,
                "commands": self.commands,
                "last_write_ms": round(stats.last_write_ms, 2),
                "avg_write_ms": round(stats.avg_write_ms, 2),
            },
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
                "source": self.last_sources[i],
                "override": self.overrides[port].to_dict() if port in self.overrides else None,
                "link": link.to_dict() if link else None,
                "poe": poe.to_dict() if poe else None,
                "facts": {k: v for k, v in vars(facts).items() if k not in ("port", "now")},
                "traffic_bps": round(self.traffic.get(port, 0.0)),
            })
        return result

    def state_message(self) -> dict:
        return {"type": "state", "state": self.state()}

    def frame_message(self) -> dict:
        return {"type": "frame", "mode": self.mode, "connected": self.connected, "ports": self.ports()}

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
        self.revision += 1
        self._dirty.set()
        if self._subscribers:
            self._publish(self.state_message())

    # Rendering

    def compose(self, t: float | None = None, now: datetime | None = None) -> tuple[list[Color], list[PortOutput]]:
        t = time.monotonic() - self._t0 if t is None else t
        now = now or datetime.now()
        colors = [BLACK] * self.num_ports
        sources: list[dict] = [{"kind": "none"}] * self.num_ports

        for zone in self.zones:
            ctx = RenderContext(
                num_ports=len(zone.ports),
                links={i + 1: self.links[p] for i, p in enumerate(zone.ports) if p in self.links},
                traffic_bps={i + 1: self.traffic[p] for i, p in enumerate(zone.ports) if p in self.traffic},
                now=now,
            )
            try:
                rendered = list(zone.effect.render(t, ctx))
            except Exception:
                log.exception("effect %s in zone %s failed", zone.effect.id, zone.id)
                rendered = []
            rendered = (rendered + [BLACK] * len(zone.ports))[: len(zone.ports)]
            for port, color in zip(zone.ports, rendered, strict=True):
                colors[port - 1] = color
                sources[port - 1] = {"kind": "zone", "id": zone.id, "name": zone.name}

        outputs = []
        for i in range(self.num_ports):
            port = i + 1
            brightness = self.brightness
            rule = first_match(self.rules, self.facts(port, now)) if self.rules else None
            if rule is not None:
                colors[i] = rule.look.render(t, colors[i])
                if rule.look.brightness is not None:
                    brightness = rule.look.brightness
                sources[i] = {"kind": "rule", "id": rule.id, "name": rule.name}
            override = self.overrides.get(port)
            if override is not None:
                colors[i] = override.color
                if override.brightness is not None:
                    brightness = override.brightness
                sources[i] = {"kind": "override"}
            outputs.append(to_output(colors[i], brightness, self.dimming))

        self.last_sources = sources
        return colors, outputs

    def _lost(self, exc: Exception) -> None:
        if self.connected:
            log.warning("lost the switch: %s", exc)
        self.connected = False
        self.error = str(exc)
        self.switch.forget()
        self._publish(self.state_message())

    async def _keep_connected(self) -> None:
        while True:
            if not self.connected:
                try:
                    await self.switch.connect()
                    info = await self.switch.probe()
                    if not info.get("port_rgb"):
                        raise TransportError("this switch does not expose etherlight.mcu port_rgb")
                    self.switch_info = info
                    self.connected, self.error = True, None
                    self.switch.forget()
                    self._changed()
                except Exception as exc:
                    self.connected, self.error = False, str(exc)
                    with contextlib.suppress(Exception):
                        await self.switch.close()
                    self._publish(self.state_message())
                    await asyncio.sleep(self.reconnect_delay)
                    continue
            await asyncio.sleep(1.0)

    async def _render_loop(self) -> None:
        next_tick = time.monotonic()
        last_frame = 0.0
        while True:
            with contextlib.suppress(asyncio.TimeoutError):
                await asyncio.wait_for(self._dirty.wait(), max(0.0, next_tick - time.monotonic()))
            self._dirty.clear()

            since = time.monotonic() - last_frame
            if since < 1.0 / self.MAX_INSTANT_RATE:
                await asyncio.sleep(1.0 / self.MAX_INSTANT_RATE - since)
            now = time.monotonic()
            if now >= next_tick:
                next_tick = max(next_tick + 1.0 / self.fps, now)

            self.last_colors, self.last_outputs = self.compose()
            if self.connected and self.mode == "running":
                try:
                    self.commands += await self.switch.write(self.last_outputs)
                    self.frames += 1
                except TransportError as exc:
                    self._lost(exc)
            last_frame = time.monotonic()
            if self._subscribers:
                self._publish(self.frame_message())

    async def _status_loop(self) -> None:
        while True:
            if not self.connected:
                await asyncio.sleep(0.2)
                continue
            try:
                links = await self.switch.read_links()
                self.traffic = self._meter.update(links)
                changed = {p: (k.link_up, k.anomaly) for p, k in links.items()} != {
                    p: (k.link_up, k.anomaly) for p, k in self.links.items()
                }
                self.links = links
                self.poe = await self.switch.read_poe()
                if changed:
                    self._dirty.set()  # a cable was pulled: show it now, not on the next frame
            except TransportError as exc:
                self._lost(exc)
            await asyncio.sleep(self.link_poll)
