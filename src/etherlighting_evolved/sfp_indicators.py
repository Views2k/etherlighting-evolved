"""The SFP port indicators: their settings, the output each one needs, and its confirmation by the switch.

Each change to what an indicator should show gets a new revision. The render loop acknowledges a revision only after
the switch confirms the output written for it, and an indicator that needs no output is handed back to the switch.
"""

from __future__ import annotations

import asyncio
from collections.abc import Iterable, Mapping
from datetime import datetime
from types import MappingProxyType
from typing import TYPE_CHECKING, Protocol

from . import timing
from .color import BLACK, Color
from .command_budget import CommandBudget
from .identify import Identification
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
    identification: Identification

    def facts(self, port: int, now: datetime | None = None) -> PortFacts: ...

    def identifying(self, port: int) -> bool: ...

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
        self._zone_colors: dict[int, Color] = {}
        self.playback = SfpPlayback()
        self._revisions = {port: 0 for port in ports}
        self._acknowledged = {port: 0 for port in ports}
        self._release_ports: set[int] = set()
        self._releasing: set[int] = set()
        self._engaged = False
        self._release_pending = False
        self.changed = asyncio.Event()
        self._rule_ids: dict[int, str | None] = {port: None for port in ports}

    def add_ports(self, ports: Iterable[int]) -> None:
        for port in ports:
            self.lights.setdefault(port, SfpLight())
            self._revisions.setdefault(port, 0)
            self._acknowledged.setdefault(port, 0)
            self._rule_ids.setdefault(port, None)

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
            self._zone_colors.pop(port, None)
        return changed

    def load(self, lights: dict[int, SfpLight], sync: SfpSync, affected: set[int],
             required_before: dict[int, bool], revisions_before: dict[int, int]) -> set[int]:
        """Apply the indicator settings of a loaded scene and return every indicator with a new revision.

        `affected` holds the indicators that the scene changes, and the two maps record whether each indicator needed
        output and which revision it had before the scene's zones and rules were applied.
        """
        self.lights, self.sync = lights, sync
        for port in affected:
            self._revisions[port] += 1
            if self.requires_output(port):
                self._release_ports.discard(port)
            elif required_before[port]:
                self._release_ports.add(port)
            else:
                self._acknowledged[port] = self._revisions[port]
        self.sync_rules()
        affected = affected | {port for port in self.lights if self._revisions[port] != revisions_before[port]}
        for port in affected & set(self.switch.sfp_ports):
            if self.requires_output(port):
                self._acknowledged[port] = -1
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
            and port not in self._releasing
            for port in self.switch.sfp_ports
        ) and self.switch.sfp_precise

    def sync_rules(self) -> None:
        for port in self.switch.sfp_ports:
            rule = self.rule(port)
            rule_id = rule.id if rule is not None else None
            if self._rule_ids[port] != rule_id:
                self._rule_ids[port] = rule_id
                self._revisions[port] += 1
                self.changed.set()
                if not self.requires_output(port):
                    self._release_ports.add(port)
            if self.requires_output(port):
                self._release_ports.discard(port)

    # Revisions and acknowledgments

    @property
    def revisions(self) -> Mapping[int, int]:
        return MappingProxyType(self._revisions)

    @property
    def acknowledged(self) -> Mapping[int, int]:
        """The last revision of each indicator that the switch confirmed, or -1 after its confirmation was discarded."""
        return MappingProxyType(self._acknowledged)

    @property
    def release_ports(self) -> tuple[int, ...]:
        """The indicators waiting to be handed back to the switch, in the order the render loop releases them."""
        return tuple(self._release_ports)

    @property
    def release_pending(self) -> bool:
        """Whether a global release must still hand every indicator back to the switch."""
        return self._release_pending

    def revise(self, ports: set[int]) -> None:
        if not ports:
            return
        for port in ports:
            self._revisions[port] += 1
            if self.requires_output(port):
                self._release_ports.discard(port)
            else:
                self._release_ports.add(port)
        self.changed.set()

    def invalidate(self) -> None:
        """Give every indicator that needs output a new revision, after a change that affects all of them."""
        for port in self.switch.sfp_ports:
            if self.requires_output(port):
                self._revisions[port] += 1
        self.changed.set()

    def frame(self) -> tuple[dict[int, PortOutput], dict[int, int]]:
        """Return the output to write in this frame and the revision each output is for.

        Once any output is written, a global release has to hand the indicators back to the switch.
        """
        outputs = self.outputs()
        revisions = {port: self._revisions[port] for port in outputs}
        self._engaged |= bool(outputs)
        return outputs, revisions

    def acknowledge(self, outputs: dict[int, PortOutput], revisions: dict[int, int]) -> None:
        """Acknowledge each written revision that is still current once the switch confirms its output."""
        for port, output in outputs.items():
            if self._revisions[port] == revisions[port] and self.switch.sfp_acknowledged(port, output):
                self._acknowledged[port] = revisions[port]
        self.changed.set()

    def begin_release(self, port: int) -> int | None:
        """Mark an indicator that waits to be handed back to the switch as releasing, and return its revision.

        Returns None when the indicator does not wait for release or needs output again.
        """
        if port not in self._release_ports or self.requires_output(port):
            return None
        self._releasing.add(port)
        return self._revisions[port]

    def release_superseded(self, port: int, revision: int) -> bool:
        """Whether the indicator needs output again or got a newer revision since its release began."""
        return self.requires_output(port) or self._revisions[port] != revision

    def release_confirmed(self, port: int, revision: int) -> None:
        """Acknowledge a release the switch confirmed, unless a newer request superseded it."""
        if not self.release_superseded(port, revision):
            self._acknowledged[port] = revision
            self._release_ports.discard(port)

    def end_release(self, port: int) -> None:
        self._releasing.discard(port)
        self.changed.set()

    def request_release(self) -> None:
        self._release_pending |= self._engaged or any(
            self.switch.sfp_status(port)["owned"] or self.switch.sfp_status(port)["state"] == "uncertain"
            for port in self.lights)
        self.changed.set()

    def released(self) -> None:
        """Record a confirmed release; indicators that still need output wait for a new confirmation."""
        self._release_pending = self._engaged = False
        for port in self.lights:
            if not self.requires_output(port):
                self._acknowledged[port] = self._revisions[port]
                self._release_ports.discard(port)
            else:
                self._revisions[port] += 1
                self._acknowledged[port] = -1
        self.changed.set()

    def recovered(self) -> None:
        """Drop the pending release once recovery cleaned up the switch output; indicators that need output are
        written again."""
        self._release_pending = self._engaged = False
        self.invalidate()

    def connection_lost(self) -> None:
        for port in self.switch.sfp_ports:
            if self.requires_output(port):
                self._revisions[port] += 1
                self._acknowledged[port] = -1
        self.changed.set()

    def control(self, port: int) -> dict:
        status = dict(self.switch.sfp_status(port))
        if (status["state"] != "uncertain"
                and (port in self._release_ports or port in self._releasing
                     or self._acknowledged[port] != self._revisions[port])):
            status["state"] = "pending"
        return status

    # Rendering

    def record_zone_color(self, port: int, color: Color) -> None:
        """Keep the color that the indicator's effect group rendered for it in the latest frame."""
        self._zone_colors[port] = color

    def color(self, port: int, elapsed: float) -> tuple[Color, float]:
        engine = self._engine
        if engine.identifying(port):
            return engine.identification.color(), 100.0
        light = self.light(port)
        follows_ethernet = self.sync.mode == "ethernet"
        grouped = port in self.zones
        if grouped:
            color, brightness = self._zone_colors.get(port, BLACK), engine.brightness
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
            if port in self._releasing or not self.requires_output(port):
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
        revision = tuple(self._revisions.values()) if self.sync.mode != "independent" else self._revisions[port]
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
             "control": self.control(port), "revision": self._revisions[port],
             "acknowledged_revision": self._acknowledged[port],
             "link": engine.links[port].link_up if port in engine.links else None,
             "anomaly": bool(engine.links[port].anomaly) if port in engine.links else None,
             "rule": {"id": rule.id, "name": rule.name} if (rule := self.rule(port)) else None,
             "preview": outputs[port].preview().to_hex() if port in outputs else None,
             "display_rgb": self._display(port, elapsed) if port in outputs else None,
             "facts": {k: v for k, v in vars(engine.facts(port)).items() if k not in ("port", "now")}}
            for port in self.switch.sfp_ports
        ]
