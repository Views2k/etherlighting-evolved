"""Schedules: device-bound timed actions and their history."""

from __future__ import annotations

from typing import Any

from fastapi import APIRouter, Body, HTTPException

from ..engine import Engine
from ..schedules import ACTIONS
from .context import ApiContext, apply


def attach(api: APIRouter, ctx: ApiContext) -> None:
    sessions, schedules, scheduler = ctx.sessions, ctx.schedules, ctx.scheduler

    @api.get("/schedules")
    async def list_schedules():
        return {"schedules": scheduler.list(), "actions": list(ACTIONS), "history": scheduler.history,
                "history_available": not scheduler._journal_error,
                "clock": Engine.schedule_clock() if sessions.engine else None}

    def bind_schedule(data):
        ctx.require_workspace()
        identity = sessions.schedule_identity()
        if identity is None:
            raise HTTPException(409, detail={"code": "not_logged_in", "message": "Connect a switch before "
                                            "applying a schedule."})
        return apply(scheduler.bind_action, {**data, "device_binding": identity})

    @api.post("/schedules", status_code=201)
    async def add_schedule(schedule: dict[str, Any] = Body(...)):
        return scheduler.describe(apply(schedules.add, bind_schedule(schedule)))

    @api.patch("/schedules/{schedule_id}")
    async def update_schedule(schedule_id: str, changes: dict[str, Any] = Body(...)):
        current = apply(schedules.get, schedule_id)
        if changes.get("expected_revision", current.revision) != current.revision:
            raise HTTPException(409, detail={"code": "schedule_changed", "message":
                "This schedule changed. Refresh schedules before saving it."})
        return scheduler.describe(apply(schedules.update, schedule_id, bind_schedule(changes)))

    @api.delete("/schedules/{schedule_id}")
    async def delete_schedule(schedule_id: str, changes: dict[str, Any] = Body(default={})):
        ctx.require_workspace()
        current = apply(schedules.get, schedule_id)
        if changes.get("expected_revision", current.revision) != current.revision:
            raise HTTPException(409, detail={"code": "schedule_changed", "message":
                "This schedule changed. Refresh schedules before deleting it."})
        apply(schedules.delete, schedule_id)
        return scheduler.list()

    @api.post("/schedules/{schedule_id}/run")
    async def run_schedule(schedule_id: str, changes: dict[str, Any] = Body(default={})):
        ctx.require_workspace()
        scheduler.migrate_profiles()
        schedule = apply(schedules.get, schedule_id)
        if changes.get("expected_revision", schedule.revision) != schedule.revision:
            raise HTTPException(409, detail={"code": "schedule_changed", "message":
                "This schedule changed. Refresh schedules before running it."})
        if not scheduler.is_bound(schedule):
            raise HTTPException(409, detail={"code": "schedule_confirmation_required", "message":
                                            "Review and apply this schedule for the connected switch first."})
        return await scheduler.run_confirmed(schedule)
