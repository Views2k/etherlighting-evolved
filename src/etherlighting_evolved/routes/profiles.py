"""Saved profiles: the stable records under /profiles and the older name-based /presets routes."""

from __future__ import annotations

from typing import Any

from fastapi import APIRouter, HTTPException
from pydantic import BaseModel, Field

from ..presets import ProfileConflict
from .context import ApiContext, apply, limit_rules, scene_envelope


class PresetSave(BaseModel):
    name: str
    scene: dict[str, Any] | None = None
    expected_revision: str | None = Field(default=None, min_length=1)
    overwrite: bool = False


class ProfileChange(BaseModel):
    expected_revision: int = Field(ge=1)
    action: str = "replace"
    name: str | None = None
    disable_schedules: bool = False


def attach(api: APIRouter, ctx: ApiContext) -> None:
    engine, presets, schedules, scheduler = ctx.engine, ctx.presets, ctx.schedules, ctx.scheduler

    def dependent_schedules(profile_id):
        scheduler.migrate_profiles()
        return [schedule for schedule in schedules.items if schedule.action.get("profile_id") == profile_id]

    @api.get("/profiles")
    async def profile_records():
        scheduler.migrate_profiles()
        return {"profiles": [{**row, "schedules": [{"id": item.id, "name": item.name, "enabled": item.enabled}
                for item in schedules.items if item.action.get("profile_id") == row["id"]]}
                for row in presets.records()]}

    @api.patch("/profiles/{profile_id}")
    async def change_profile(profile_id: str, body: ProfileChange):
        ctx.require_workspace()
        dependents = dependent_schedules(profile_id)
        try:
            row = presets.record(profile_id=profile_id)
            if row["revision"] != body.expected_revision:
                raise ProfileConflict("This profile changed. Refresh profiles before trying again.")
            if body.action == "delete":
                enabled = [item for item in dependents if item.enabled]
                if enabled and not body.disable_schedules:
                    raise HTTPException(409, detail={"code": "profile_in_use", "message":
                        "This profile has enabled schedules. Disable those schedules when deleting it.",
                        "schedules": [{"id": item.id, "name": item.name} for item in enabled]})
                for item in enabled:
                    schedules.update(item.id, {"enabled": False})
                presets.change(profile_id, body.expected_revision, delete=True)
            elif body.action == "replace":
                presets.change(profile_id, body.expected_revision, scene=engine().snapshot())
            elif body.action in {"rename", "duplicate"} and body.name is not None:
                presets.change(profile_id, body.expected_revision, name=body.name, duplicate=body.action == "duplicate")
            else:
                raise ValueError("Choose a profile action and a name when renaming or duplicating.")
        except (ProfileConflict, KeyError) as exc:
            raise HTTPException(409, detail={"code": "profile_changed", "message":
                "This profile changed or was removed. Refresh profiles before trying again."}) from exc
        except FileExistsError as exc:
            raise HTTPException(409, detail={"code": "already_exists", "message": str(exc)}) from exc
        except ValueError as exc:
            raise HTTPException(400, detail={"code": "invalid", "message": str(exc)}) from exc
        return await profile_records()

    @api.get("/presets")
    async def list_presets():
        return presets.list()

    @api.post("/presets", status_code=201)
    async def save_preset(body: PresetSave):
        e = engine()
        scheduler.migrate_profiles()
        current = scene_envelope(e.snapshot())
        if body.scene is not None and body.expected_revision is None:
            raise HTTPException(400, detail={"code": "scene_revision_required", "message":
                                            "Refresh the lighting preview before saving a profile."})
        if body.expected_revision is not None and body.expected_revision != current["revision"]:
            raise HTTPException(409, detail={"code": "scene_changed", "message":
                                            "The lighting setup changed. Review the preview before saving."})
        if body.scene is not None:
            limit_rules(body.scene.get("rules"))
        try:
            snapshot = current["scene"] if body.scene is None else apply(e.validate_snapshot, body.scene)
        except AttributeError as exc:
            raise HTTPException(400, detail={"code": "invalid", "message":
                                            "The lighting setup has invalid settings."}) from exc
        try:
            apply(presets.save, body.name, snapshot, overwrite=False)
        except FileExistsError as exc:
            raise HTTPException(409, detail={"code": "already_exists", "message": str(exc)}) from exc
        return presets.list()

    @api.post("/presets/{name}/apply")
    async def apply_preset(name: str):
        e = engine()
        try:
            scene = presets.get(name)
        except KeyError:
            raise HTTPException(404, detail={"code": "not_found", "message": f"no preset named {name!r}"}) from None
        await ctx.commit_scene(e, scene, preserve_unchanged=False, confirm_output=True)
        return e.state()

    @api.delete("/presets/{name}")
    async def delete_preset(name: str):
        try:
            row = presets.record(name=name)
            if any(item.enabled for item in dependent_schedules(row["id"])):
                raise HTTPException(409, detail={"code": "profile_in_use", "message":
                    "This profile has enabled schedules. Review it in Profiles before deleting it."})
            presets.delete(name)
        except KeyError:
            raise HTTPException(404, detail={"code": "not_found", "message": f"no preset named {name!r}"}) from None
        return presets.list()

    def transfer_preset(action, source: str, target: str):
        try:
            scheduler.migrate_profiles()
            action(source, target)
        except KeyError:
            raise HTTPException(404, detail={
                "code": "not_found", "message": "The saved profile could not be found."
            }) from None
        except FileExistsError as exc:
            raise HTTPException(409, detail={"code": "already_exists", "message": str(exc)}) from exc
        except ValueError as exc:
            raise HTTPException(400, detail={"code": "invalid", "message": str(exc)}) from exc
        return presets.list()

    @api.post("/presets/{name}/duplicate", status_code=201)
    async def duplicate_preset(name: str, body: PresetSave):
        return transfer_preset(presets.duplicate, name, body.name)

    @api.post("/presets/{name}/rename")
    async def rename_preset(name: str, body: PresetSave):
        return transfer_preset(presets.rename, name, body.name)
