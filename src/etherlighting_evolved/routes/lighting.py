"""Live lighting: state, zones and effects, SFP lights, manual colors, global controls and the scene."""

from __future__ import annotations

from typing import Any

from fastapi import APIRouter, Body, HTTPException
from pydantic import BaseModel, Field

from ..scene_history import attach_routes as attach_history_routes
from .context import ApiContext, apply, await_output, scene_envelope

ColorValue = str | list[int] | dict[str, int]


class EffectRequest(BaseModel):
    id: str
    params: dict[str, Any] = Field(default_factory=dict)
    clear_overrides: bool = False


class ParamsRequest(BaseModel):
    params: dict[str, Any]


class ZonesRequest(BaseModel):
    zones: list[dict[str, Any]]


class SceneRestore(BaseModel):
    scene: dict[str, Any]
    expected_revision: str = Field(min_length=1)
    preserve_unchanged: bool = False
    confirm_output: bool = False


class PortColor(BaseModel):
    color: ColorValue
    brightness: int | None = Field(default=None, ge=0, le=100)


class BulkPorts(BaseModel):
    ports: dict[int, PortColor]


class NumberValue(BaseModel):
    value: float


class OutputSettings(BaseModel):
    gamma: float | None = Field(default=None, ge=1.0, le=3.0)
    transition: float | None = Field(default=None, ge=0.0, le=5.0)
    max_commands: float | None = Field(default=None, ge=5.0, le=200.0)


def attach(api: APIRouter, ctx: ApiContext) -> None:
    engine, controller = ctx.engine, ctx.controller

    @api.get("/state")
    async def state():
        return controller.state(engine().state())

    @api.get("/ports")
    async def ports():
        return controller.ports(engine().ports())

    # Zones and their effects

    @api.get("/zones")
    async def zones():
        return engine().state()["zones"]

    @api.put("/zones")
    async def set_zones(body: ZonesRequest):
        e = engine()
        return await ctx.apply_port_change(e, e.set_zones, body.zones)

    @api.put("/effect")
    async def set_effect_everywhere(body: EffectRequest):
        """One effect on every port (replaces all zones)."""
        e = engine()
        ports = sorted(set(range(1, e.num_ports + 1)) | {p for zone in e.zones for p in zone.ports})
        return await ctx.apply_port_change(e, e.set_zones, [{"id": "all", "name": "All ports", "ports": ports,
                                                             "effect": {"id": body.id, "params": body.params}}],
                                           clear_overrides=body.clear_overrides)

    @api.put("/zones/{zone_id}/effect")
    async def set_zone_effect(zone_id: str, body: EffectRequest):
        e = engine()
        return await ctx.apply_port_change(e, e.set_effect, body.id, body.params, zone=zone_id,
                                           clear_overrides=body.clear_overrides)

    @api.patch("/zones/{zone_id}/params")
    async def set_zone_params(zone_id: str, body: ParamsRequest):
        e = engine()
        return await ctx.apply_port_change(e, e.update_params, body.params, zone=zone_id)

    # SFP lights and colors set by hand. /sfp/sync must stay ahead of /sfp/{port}.

    @api.put("/sfp/sync")
    async def set_sfp_sync(settings: dict[str, Any] = Body(...)):
        e = engine()
        return controller.state(await ctx.apply_port_change(e, e.set_sfp_sync, settings))

    @api.put("/sfp/{port}")
    async def set_sfp(port: int, settings: dict[str, Any] = Body(...)):
        e = engine()
        return await ctx.apply_port_change(e, e.set_sfp, port, settings)

    @api.put("/ports")
    async def set_ports(body: BulkPorts):
        e = engine()
        apply(e.set_overrides, {p: v.model_dump() for p, v in body.ports.items()})
        return e.state()

    @api.delete("/ports")
    async def clear_ports():
        e = engine()
        e.clear_override()
        return e.state()

    @api.put("/ports/{port}")
    async def set_port(port: int, body: PortColor):
        e = engine()
        apply(e.set_override, port, body.color, body.brightness)
        return e.state()

    @api.delete("/ports/{port}")
    async def clear_port(port: int):
        e = engine()
        apply(e.clear_override, port)
        return e.state()

    # Global controls

    @api.put("/brightness")
    async def brightness(body: NumberValue):
        e = engine()
        apply(e.set_brightness, body.value)
        return e.state()

    @api.put("/fps")
    async def fps(body: NumberValue):
        e = engine()
        apply(e.set_fps, body.value)
        return e.state()

    @api.put("/output")
    async def output(body: OutputSettings):
        e = engine()
        apply(e.set_output, **body.model_dump())
        return e.state()

    @api.post("/release")
    async def release():
        e = engine()
        e.release()
        await await_output(e.wait_for_output_idle)
        return e.state()

    @api.post("/resume")
    async def resume():
        e = engine()
        apply(e.resume)
        return e.state()

    # The scene and its history

    @api.get("/scene")
    async def get_scene():
        return scene_envelope(engine().snapshot())

    @api.put("/scene")
    async def restore_scene(body: SceneRestore):
        e = engine()
        current = scene_envelope(e.snapshot())
        if body.expected_revision != current["revision"]:
            raise HTTPException(409, detail={
                "code": "scene_changed",
                "message": "The lighting setup changed. Review the current setup before applying.",
            })
        return await ctx.commit_scene(e, body.scene, preserve_unchanged=body.preserve_unchanged,
                                      confirm_output=body.confirm_output)

    async def restore_history_scene(scene, revision):
        return await restore_scene(SceneRestore(scene=scene, expected_revision=revision,
                                               preserve_unchanged=True, confirm_output=True))

    attach_history_routes(api, engine, apply, restore_history_scene)
