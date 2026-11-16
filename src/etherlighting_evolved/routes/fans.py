"""Fan readings and the fan mode."""

from __future__ import annotations

from fastapi import APIRouter, HTTPException
from pydantic import BaseModel

from ..transport import TransportError
from .context import ApiContext


class FanChange(BaseModel):
    mode: str | None = None
    curve: str | None = None
    manual_percent: int | None = None
    target_celsius: int | None = None


def attach(api: APIRouter, ctx: ApiContext) -> None:
    @api.get("/fans")
    async def fans():
        e = ctx.engine()
        return e.fans.to_dict() if e.fans is not None else None

    @api.put("/fans")
    async def change_fans(body: FanChange):
        e = ctx.engine()
        change = body.model_dump(exclude_none=True)
        try:
            await e.update_fans(change)
        except TransportError as exc:
            raise HTTPException(409, detail={"code": "fans_unconfirmed", "message": str(exc)}) from exc
        except ValueError as exc:
            raise HTTPException(400, detail={"code": "invalid", "message": str(exc)}) from exc
        return e.fans.to_dict()
