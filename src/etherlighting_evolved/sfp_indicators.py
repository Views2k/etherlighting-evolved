"""The SFP port indicators: their settings, the output each one needs, and its confirmation by the switch.

Each change to what an indicator should show gets a new revision. The render loop acknowledges a revision only after
the switch confirms the output written for it, and an indicator that needs no output is handed back to the switch.
"""

from __future__ import annotations

import asyncio
from collections.abc import Iterable
from datetime import datetime
from typing import TYPE_CHECKING, Protocol

from . import timing
from .color import BLACK, Color
from .command_budget import CommandBudget
from .links import PortLink
from .port_output import display_rgb, to_output
from .rules import PortFacts, Rule, first_match
from .sfp import NATIVE_PERIODS, SfpLight, SfpSync
from .sfp_playback import SfpPlayback, displayed_rgb
from .switch import EtherlightSwitch, PortOutput

if TYPE_CHECKING:
    from .engine import Zone


class SfpEngine(Protocol):
    """The engine state that the indicators render from and that targets them."""

    rules: list[Rule]
    brightness: int
    gamma: float
    mode: str
    num_ports: int
    links: dict[int, PortLink]
    budget: CommandBudget

    def facts(self, port: int, now: datetime | None = None) -> PortFacts: ...

    def identifying(self, port: int) -> bool: ...

    def identify_color(self) -> Color: ...

    def port_color(self, port: int) -> tuple[Color, float]: ...

    def scene_elapsed(self) -> float: ...


class SfpIndicators:
    def __init__(self, engine: SfpEngine, switch: EtherlightSwitch, ports: Iterable[int]) -> None:
        ports = tuple(ports)
        self._engine = engine
        self.switch = switch
        self.lights: dict[int, SfpLight] = {port: SfpLight() for port in ports}
        self.sync = SfpSync()
        self.zones: dict[int, Zone] = {}  # indicators that belong to an effect group
        self.zone_colors: dict[int, Color] = {}
        self.playback = SfpPlayback()
        self.revisions = {port: 0 for port in ports}
        self.acknowledged = {port: 0 for port in ports}
        self.release_ports: set[int] = set()
        self.releasing: set[int] = set()
        self.engaged = False
        self.release_pending = False
        self.changed = asyncio.Event()
        self.rule_ids: dict[int, str | None] = {port: None for port in ports}

    def add_ports(self, ports: Iterable[int]) -> None:
        for port in ports:
            self.lights.setdefault(port, SfpLight())
            self.revisions.setdefault(port, 0)
            self.acknowledged.setdefault(port, 0)
            self.rule_ids.setdefault(port, None)

    # Settings

    @property
    def leader(self) -> int:
        return min(self.lights)

    def light(self, port: int) -> SfpLight:
        return self.lights[self.leader if self.sync.mode == "together" else port]

    def update_light(self, port: int, changes: dict) -> None:
        if type(port) is not int or port not in self.switch.sfp_ports:
            raise ValueError("SFP lighting is unavailable for this port")
        if not changes:
            raise ValueError("provide an SFP lighting setting")
        updated = self.lights[port].updated(changes)
        if port in self.zones and set(changes) != {"brightness"}:
            raise ValueError("Remove this SFP port from its effect group before changing its indicator settings")
        self.lights[port] = updated
        affected = {port}
        if self.sync.mode == "together" and port == self.leader:
            affected.update(p for p in self.switch.sfp_ports if p not in self.zones)
        self.revise(affected)
        self.sync_rules()

    def update_sync(self, changes: dict) -> None:
        if len(self.switch.sfp_ports) < 2:
            raise ValueError("SFP synchronization is unavailable for this switch")
        if not changes:
            raise ValueError("provide an SFP synchronization setting")
        updated = self.sync.updated(changes)
        num_ports = self._engine.num_ports
        if (updated.mode == "ethernet" or num_ports) and updated.source_port > num_ports:
            raise ValueError("SFP source must be an available Ethernet port")
        self.sync = updated
        self.revise(set(self.switch.sfp_ports) - self.zones.keys())
        self.sync_rules()

    def replace_zones(self, zones: list[Zone]) -> set[int]:
        """Track which indicators belong to the new effect groups; return those whose group or effect changed."""
        previous = self.zones
        self.zones = {port: zone for zone in zones for port in zone.ports if port in self.lights}
        changed = {port for port in previous.keys() | self.zones.keys()
                   if port not in previous or port not in self.zones
                   or previous[port].effect is not self.zones[port].effect}
        for port in changed:
            self.zone_colors.pop(port, None)
        return changed

    def load(self, lights: dict[int, SfpLight], sync: SfpSync, affected: set[int],
             required_before: dict[int, bool], revisions_before: dict[int, int]) -> set[int]:
        """Apply the indicator settings of a loaded scene and return every indicator with a new revision.

        `affected` holds the indicators that the scene changes, and the two maps record whether each indicator needed
        output and which revision it had before the scene's zones and rules were applied.
        """
        self.lights, self.sync = lights, sync
        for port in affected:
            self.revisions[port] += 1
            if self.requires_output(port):
                self.release_ports.discard(port)
            elif required_before[port]:
                self.release_ports.add(port)
            else:
                self.acknowledged[port] = self.revisions[port]
        self.sync_rules()
        affected = affected | {port for port in self.lights if self.revisions[port] != revisions_before[port]}
        for port in affected & set(self.switch.sfp_ports):
            if self.requires_output(port):
                self.acknowledged[port] = -1
        if affected:
            self.changed.set()
        return affected

    # Rules and output need

    def indicator_active(self, port: int) -> bool:
        return self.sync.mode == "ethernet" or port in self.zones

    def rule(self, port: int, rules: list[Rule] | None = None, now: datetime | None = None) -> Rule | None:
        light = self.light(port)
        indicator_active = self.indicator_active(port)
        if light.mode == "off" and not indicator_active:
            return None
        # Existing all-copper rules must never silently acquire the SFP indicators.
        rule = first_match([rule for rule in (self._engine.rules if rules is None else rules) if rule.ports],
                           self._engine.facts(port, now))
        if rule is not None and rule.look.style == "effect" and light.mode == "unmanaged" and not indicator_active:
            return None
        return rule

    def requires_output(self, port: int) -> bool:
        return (self._engine.identifying(port) or port in self.zones or self.sync.mode == "ethernet"
                or self.light(port).mode != "unmanaged" or self.rule(port) is not None)

    def _port_animating(self, port: int) -> bool:
        if self._engine.identifying(port):
            return True
        rule = self.rule(port)
        if rule is not None and rule.look.style != "effect":
            return rule.look.style in ("pulse", "blink", "flash")
        zone = self.zones.get(port)
        if zone is not None:
            return zone.effect.category != "static"
        if self.sync.mode == "ethernet":
            return True
        light = self.light(port)
        return light.mode == "color" and light.effect != "steady"

    def animating(self) -> bool:
        return self._engine.mode == "running" and any(
            port not in self.zones and self._port_animating(port)
            and (self.sync.mode == "ethernet" or self.light(port).brightness > 0)
            and port not in self.releasing
            for port in self.switch.sfp_ports
        ) and self.switch.sfp_precise

    def sync_rules(self) -> None:
        for port in self.switch.sfp_ports:
            rule = self.rule(port)
            rule_id = rule.id if rule is not None else None
            if self.rule_ids[port] != rule_id:
                self.rule_ids[port] = rule_id
                self.revisions[port] += 1
                self.changed.set()
                if not self.requires_output(port):
                    self.release_ports.add(port)
            if self.requires_output(port):
                self.release_ports.discard(port)

    # Revisions and acknowledgments

    def revise(self, ports: set[int]) -> None:
        if not ports:
            return
        for port in ports:
            self.revisions[port] += 1
            if self.requires_output(port):
                self.release_ports.discard(port)
            else:
                self.release_ports.add(port)
        self.changed.set()

    def invalidate(self) -> None:
        """Give every indicator that needs output a new revision, after a change that affects all of them."""
        for port in self.switch.sfp_ports:
            if self.requires_output(port):
                self.revisions[port] += 1
        self.changed.set()

    def acknowledge(self, outputs: dict[int, PortOutput], revisions: dict[int, int]) -> None:
        """Acknowledge each written revision that is still current once the switch confirms its output."""
        for port, output in outputs.items():
            if self.revisions[port] == revisions[port] and self.switch.sfp_acknowledged(port, output):
                self.acknowledged[port] = revisions[port]
        self.changed.set()

    def request_release(self) -> None:
        self.release_pending |= self.engaged or any(
            self.switch.sfp_status(port)["owned"] or self.switch.sfp_status(port)["state"] == "uncertain"
            for port in self.lights)
        self.changed.set()

    def released(self) -> None:
        """Record a confirmed release; indicators that still need output wait for a new confirmation."""
        self.release_pending = self.engaged = False
        for port in self.lights:
            if not self.requires_output(port):
                self.acknowledged[port] = self.revisions[port]
                self.release_ports.discard(port)
            else:
                self.revisions[port] += 1
                self.acknowledged[port] = -1
        self.changed.set()

    def connection_lost(self) -> None:
        for port in self.switch.sfp_ports:
            if self.requires_output(port):
                self.revisions[port] += 1
                self.acknowledged[port] = -1
        self.changed.set()

    def control(self, port: int) -> dict:
        status = dict(self.switch.sfp_status(port))
        if (status["state"] != "uncertain"
                and (port in self.release_ports or port in self.releasing
                     or self.acknowledged[port] != self.revisions[port])):
            status["state"] = "pending"
        return status

    # Rendering

    def color(self, port: int, elapsed: float) -> tuple[Color, float]:
        engine = self._engine
        if engine.identifying(port):
            return engine.identify_color(), 100.0
        light = self.light(port)
        follows_ethernet = self.sync.mode == "ethernet"
        grouped = port in self.zones
        if grouped:
            color, brightness = self.zone_colors.get(port, BLACK), engine.brightness
        elif follows_ethernet:
            color, brightness = engine.port_color(self.sync.source_port)
        else:
            color, brightness = light.render(elapsed, port), engine.brightness
        rule = self.rule(port)
        if rule is not None:
            color = rule.look.render(elapsed, color)
            if rule.look.brightness is not None:
                brightness = rule.look.brightness
        if grouped:
            return color, brightness * self.lights[port].brightness / 100
        return color, brightness if follows_ethernet else brightness * light.brightness / 100

    def outputs(self) -> dict[int, PortOutput]:
        engine = self._engine
        elapsed = engine.scene_elapsed()
        outputs = {}
        native = {}
        for port in self.switch.sfp_ports:
            if port in self.releasing or not self.requires_output(port):
                self.playback.discard(port)
                continue
            spec = self._native_spec(port)
            if spec is not None:
                native[port] = spec
                continue
            self.playback.discard(port)
            color, brightness = self.color(port, elapsed)
            outputs[port] = to_output(color, brightness, engine.gamma,
                                      precise=self.switch.sfp_precise and (port in self.zones
                                                                           or self._port_animating(port)))
        if (self.sync.mode != "independent" and native and set(native) == set(self.switch.sfp_ports)
                and all(spec == native[self.leader] for spec in native.values())):
            signature, anchors, duration, follow = native[self.leader]
            outputs.update(self.playback.plan(
                tuple(self.switch.sfp_ports), signature, anchors, duration, self.switch,
                follow=follow, command_budget=engine.budget.value))
        else:
            for port, (signature, anchors, duration, follow) in native.items():
                outputs.update(self.playback.plan((port,), signature, anchors, duration,
                                                  self.switch, follow=follow, command_budget=engine.budget.value))
        return outputs

    def _native_spec(self, port: int) -> tuple | None:
        engine = self._engine
        if engine.identifying(port) or port in self.zones or not self.switch.sfp_native_gradient:
            return None
        rule = self.rule(port)
        if rule is not None and rule.look.style != "effect":
            return None
        follows = self.sync.mode == "ethernet"
        light = self.light(port)
        if not follows and light.native_duration is None:
            return None
        revision = tuple(self.revisions.values()) if self.sync.mode != "independent" else self.revisions[port]
        look = tuple(sorted(rule.look.to_dict().items())) if rule is not None else None
        key = (self.sync, revision, look, engine.gamma)
        color, brightness = self.color(port, engine.scene_elapsed())
        if follows:
            return key, (to_output(color, brightness, engine.gamma, precise=True),), 1, True
        anchors = tuple(to_output(color, brightness, engine.gamma, precise=True)
                        for color in light.native_anchors())
        return (*key, light, brightness), anchors, light.native_duration, False

    # What the UI sees

    def _playback_state(self, port: int) -> dict:
        spec = self._native_spec(port)
        light = self.light(port)
        kind = ("follow" if spec[3] else "native") if spec is not None else (
            "custom" if self._port_animating(port) else "steady")
        periods = [] if port in self.zones else list(NATIVE_PERIODS.get(light.effect, ()))
        return {"kind": kind, "periods": periods,
                "segment_seconds": spec[2] if spec is not None else None}

    def _transition_state(self, port: int) -> dict | None:
        transition = self.switch.sfp_transition(port)
        if transition is None:
            return None
        return {"duration": transition.duration,
                "elapsed": max(0, min(transition.duration, timing.monotonic() - transition.started_at)),
                "from_rgb16": list(transition.start_rgb), "to_rgb16": list(transition.output.precise_rgb)}

    def _display(self, port: int, elapsed: float) -> list[float] | None:
        if self.control(port)["state"] == "uncertain":
            return None
        if self.switch.sfp_transition(port) is not None or self._native_spec(port) is not None:
            return displayed_rgb(self.switch.sfp_rgb16(port), self._engine.gamma)
        return display_rgb(*self.color(port, elapsed))

    def state(self) -> list[dict]:
        engine = self._engine
        outputs = self.outputs()
        elapsed = engine.scene_elapsed()
        return [
            {"port": port, **self.lights[port].to_dict(),
             "effect_zone": self.zones[port].id if port in self.zones else None,
             "effective_settings": (None if port in self.zones or self.sync.mode == "ethernet"
                                    else self.light(port).to_dict()),
             "playback": self._playback_state(port), "native_transition": self._transition_state(port),
             "control": self.control(port), "revision": self.revisions[port],
             "acknowledged_revision": self.acknowledged[port],
             "link": engine.links[port].link_up if port in engine.links else None,
             "anomaly": bool(engine.links[port].anomaly) if port in engine.links else None,
             "rule": {"id": rule.id, "name": rule.name} if (rule := self.rule(port)) else None,
             "preview": outputs[port].preview().to_hex() if port in outputs else None,
             "display_rgb": self._display(port, elapsed) if port in outputs else None,
             "facts": {k: v for k, v in vars(engine.facts(port)).items() if k not in ("port", "now")}}
            for port in self.switch.sfp_ports
        ]
