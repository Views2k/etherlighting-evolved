"""Acknowledgment-driven SFP segment plans; rendering never advances an unconfirmed target."""

from __future__ import annotations

from dataclasses import dataclass, replace

from . import timing
from .color import Color
from .switch import EtherlightSwitch, PortOutput, sfp_preparation_lead


@dataclass
class SegmentPlan:
    signature: tuple
    outputs: dict[int, PortOutput]
    next_anchor: int = 1
    settle_at: float | None = None


class SfpPlayback:
    def __init__(self) -> None:
        self._plans: dict[tuple[int, ...], SegmentPlan] = {}
        self._sequence = 0

    def discard(self, port: int) -> None:
        for ports in tuple(self._plans):
            if port in ports:
                del self._plans[ports]

    def next_deadline(self, switch: EtherlightSwitch, command_budget: float = 30) -> float | None:
        deadlines = []
        for plan in self._plans.values():
            if plan.settle_at is not None:
                deadlines.append(plan.settle_at)
                continue
            transitions = [switch.sfp_transition(port) for port in plan.outputs]
            # A staged next target still has the current native segment's completion deadline.
            if all(transition is not None for transition in transitions):
                targets = switch.sfp_segment_deadlines(plan.outputs, command_budget)
                deadlines.append(min(targets.values()) - sfp_preparation_lead(command_budget))
        return min(deadlines) if deadlines else None

    def plan(self, ports: tuple[int, ...], signature: tuple, anchors: tuple[PortOutput, ...], duration: int,
             switch: EtherlightSwitch, *, follow: bool = False, command_budget: float = 30) -> dict[int, PortOutput]:
        current = self._plans.get(ports)
        if current is None or current.signature != signature:
            for port in ports:
                self.discard(port)
            if follow:
                starts = {port: self._steady(switch.sfp_rgb16(port)) for port in ports}
            else:
                starts = dict.fromkeys(ports, anchors[0])
            current = self._plans[ports] = SegmentPlan(signature, starts, 0 if follow else 1)

        now = timing.monotonic()
        finishes = []
        for port, output in current.outputs.items():
            if not switch.sfp_acknowledged(port, output):
                return current.outputs
            if output.gradient is not None:
                transition = switch.sfp_transition(port)
                if transition is None or transition.output != output:
                    return current.outputs
                finishes.append(transition.finishes_at)
        if finishes:
            deadlines = switch.sfp_segment_deadlines(current.outputs, command_budget)
            if now < min(deadlines.values()) - sfp_preparation_lead(command_budget):
                return current.outputs

        # The writer prepares the selector now and gates each target on its prior segment deadline.
        target = anchors[current.next_anchor % len(anchors)]
        if not follow:
            for _ in anchors:
                if any(out.precise_rgb != target.precise_rgb for out in current.outputs.values()):
                    break
                current.next_anchor = (current.next_anchor + 1) % len(anchors)
                target = anchors[current.next_anchor]
        same = all(out.precise_rgb == target.precise_rgb for out in current.outputs.values())
        if same:
            if finishes and now < max(finishes):
                current.settle_at = max(finishes)
                return current.outputs
            current.settle_at = None
            current.outputs = dict.fromkeys(ports, target)
            return current.outputs
        current.settle_at = None
        self._sequence += 1
        output = replace(target, gradient=f"gradient_{duration}s", transition_id=self._sequence)
        current.outputs = dict.fromkeys(ports, output)
        if not follow:
            current.next_anchor = (current.next_anchor + 1) % len(anchors)
        return current.outputs

    @staticmethod
    def _steady(channels: tuple[float, float, float]) -> PortOutput:
        values = tuple(max(0, min(65280, round(value))) for value in channels)
        return PortOutput(Color(*(round(value / 256) for value in values)), 100, values)


def displayed_rgb(channels: tuple[float, float, float], gamma: float) -> list[float]:
    return [255 * (max(0.0, min(65280.0, value)) / 65280) ** (1 / gamma) for value in channels]
