"""Effect registry."""

from __future__ import annotations

from .ambient import AMBIENT_EFFECTS
from .base import Effect, Param, RenderContext
from .library import ALL_EFFECTS
from .power import PoeUsage

EFFECTS: dict[str, type[Effect]] = {cls.id: cls for cls in ALL_EFFECTS + AMBIENT_EFFECTS + (PoeUsage,)}


def create_effect(effect_id: str, num_ports: int, params: dict | None = None, *, seed: int | None = None) -> Effect:
    try:
        cls = EFFECTS[effect_id]
    except KeyError:
        raise ValueError(f"unknown effect {effect_id!r}; available: {', '.join(EFFECTS)}") from None
    return cls(num_ports, params, seed=seed)


def list_effects() -> list[dict]:
    return [cls.describe() for cls in EFFECTS.values()]


__all__ = ["EFFECTS", "Effect", "Param", "RenderContext", "create_effect", "list_effects"]
