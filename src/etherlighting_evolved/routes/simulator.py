"""Simulator controls, available only after a demo login."""

from __future__ import annotations

from fastapi import APIRouter, HTTPException
from pydantic import BaseModel, Field

from .context import ApiContext, apply


class SimLink(BaseModel):
    up: bool


class SimAnomaly(BaseModel):
    code: int = Field(ge=0)


class SimPoe(BaseModel):
    pd_class: int | None = Field(default=None, ge=0, le=8, alias="class")


def attach(api: APIRouter, ctx: ApiContext) -> None:
    @api.put("/simulator/links/{port}")
    async def simulator_link(port: int, body: SimLink):
        apply(ctx.simulator().set_link, port, body.up)
        return {"port": port, "up": body.up}

    @api.put("/simulator/anomaly/{port}")
    async def simulator_anomaly(port: int, body: SimAnomaly):
        apply(ctx.simulator().set_anomaly, port, body.code)
        return {"port": port, "code": hex(body.code)}

    @api.put("/simulator/poe/{port}")
    async def simulator_poe(port: int, body: SimPoe):
        sim = ctx.simulator()
        if port not in sim.poe_class:
            raise HTTPException(400, detail={"code": "invalid", "message": f"port {port} has no PoE"})
        sim.poe_class[port] = body.pd_class
        return {"port": port, "class": body.pd_class}
