"""HTTP and WebSocket API for the EE web UI. docs/API.md is the full contract."""

from __future__ import annotations

import asyncio
import contextlib
import html
from contextlib import asynccontextmanager
from typing import Any

from fastapi import APIRouter, Body, Depends, FastAPI, HTTPException
from fastapi.exception_handlers import request_validation_exception_handler
from fastapi.exceptions import RequestValidationError
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import HTMLResponse, JSONResponse
from pydantic import BaseModel, Field

from . import DISCLAIMER, PROJECT_NAME, TAGLINE, __version__
from .config import Config
from .controller import ControllerError
from .controller_api import ControllerIntegration
from .engine import Engine
from .guard import LOOPBACK_BINDS, RequestGuard
from .lighting_library import attach_library_routes
from .port_names import attach_port_name_routes
from .presets import PresetStore, ProfileConflict, ProfileStoreError
from .routes import about as about_routes
from .routes import fans as fan_routes
from .routes import lighting as lighting_routes
from .routes import rules as rule_routes
from .routes import session as session_routes
from .routes import simulator as simulator_routes
from .routes import ws as ws_routes
from .routes.context import ApiContext, apply, scene_envelope
from .schedules import ACTIONS, Scheduler, ScheduleStore, ScheduleStoreError
from .session import LoginError, SessionManager


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


def create_app(config: Config | None = None, *, demo: bool = False, autoconnect: bool = True,
               transport_factory=None) -> FastAPI:
    """Build the API. demo=True logs straight in to the simulator (no switch needed)."""
    config = config or Config()
    config.api.validate()
    sessions = SessionManager(config, transport_factory=transport_factory)
    controller = ControllerIntegration(sessions, data_dir=config.data_dir)
    presets = PresetStore(config.data_dir / "presets.json")
    schedules = ScheduleStore(config.data_dir / "schedules.json")
    scheduler = Scheduler(schedules, presets, sessions)
    ctx = ApiContext(config, sessions, controller, presets, schedules, scheduler)

    @asynccontextmanager
    async def lifespan(app: FastAPI):
        startup = None
        if demo:
            await sessions.connect("demo")
        elif autoconnect:
            startup = asyncio.create_task(sessions.auto_connect(), name="ee-autoconnect")
        clock = asyncio.create_task(scheduler.run_forever(), name="ee-scheduler")
        topology = asyncio.create_task(controller.run(), name="ee-controller")
        try:
            yield
        finally:
            if startup is not None:
                startup.cancel()
                with contextlib.suppress(asyncio.CancelledError, Exception):
                    await startup
            clock.cancel()
            with contextlib.suppress(asyncio.CancelledError, Exception):
                await clock
            topology.cancel()
            with contextlib.suppress(asyncio.CancelledError, Exception):
                await topology
            try:
                await controller.close()
            finally:
                await sessions.close()

    app = FastAPI(
        title=f"{PROJECT_NAME} API",
        version=__version__,
        description=f"{TAGLINE}\n\n**{DISCLAIMER}**",
        lifespan=lifespan,
    )

    @app.middleware("http")
    async def workspace_context(request, call_next):
        marker = ctx.expected_workspace.set(request.headers.get("X-EE-Workspace"))
        try:
            return await call_next(request)
        finally:
            ctx.expected_workspace.reset(marker)

    @app.exception_handler(RequestValidationError)
    async def validation_error(request, exc):
        if request.url.path in {"/api/session/login", "/api/controller/connect", "/api/controller/consoles",
                                "/api/controller/preferences",
                                "/api/session/forget-host-key"}:
            return JSONResponse(status_code=422, content={"detail": {
                "code": "invalid_credentials", "message": "Check the connection fields and try again."
            }})
        return await request_validation_exception_handler(request, exc)

    @app.exception_handler(ProfileStoreError)
    @app.exception_handler(ScheduleStoreError)
    async def saved_data_storage_error(request, exc):
        return JSONResponse(status_code=409, content={"detail": {"code": exc.code, "message": str(exc)}})

    @app.exception_handler(LoginError)
    async def login_error(request, exc):
        return JSONResponse(status_code=exc.status, content={"detail": {"code": exc.code, "message": exc.message}})

    @app.exception_handler(ControllerError)
    async def controller_error(request, exc):
        return JSONResponse(status_code=exc.status, content={"detail": exc.detail()})
    app.state.sessions = sessions
    app.state.controller = controller
    app.state.presets = presets
    app.state.schedules = schedules
    app.state.scheduler = scheduler
    if config.api.cors_origins:
        app.add_middleware(
            CORSMiddleware, allow_origins=config.api.cors_origins, allow_methods=["*"], allow_headers=["*"]
        )
    app.add_middleware(
        RequestGuard, allowed_origins=config.api.cors_origins, local_only=config.api.host in LOOPBACK_BINDS
    )

    engine, require_workspace, commit_scene = ctx.engine, ctx.require_workspace, ctx.commit_scene

    api = APIRouter(prefix="/api", dependencies=[Depends(ctx.require_token)])
    controller.routes(api)
    attach_library_routes(api, engine, sessions, config)
    attach_port_name_routes(api, engine, sessions, controller.port_names)
    about_routes.attach(api, ctx)
    session_routes.attach(api, ctx)
    lighting_routes.attach(api, ctx)
    rule_routes.attach(api, ctx)
    fan_routes.attach(api, ctx)

    # Presets

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
        require_workspace()
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
        await commit_scene(e, scene, preserve_unchanged=False, confirm_output=True)
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

    # Schedules

    @api.get("/schedules")
    async def list_schedules():
        return {"schedules": scheduler.list(), "actions": list(ACTIONS), "history": scheduler.history,
                "history_available": not scheduler._journal_error,
                "clock": Engine.schedule_clock() if sessions.engine else None}

    def bind_schedule(data):
        require_workspace()
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
        require_workspace()
        current = apply(schedules.get, schedule_id)
        if changes.get("expected_revision", current.revision) != current.revision:
            raise HTTPException(409, detail={"code": "schedule_changed", "message":
                "This schedule changed. Refresh schedules before deleting it."})
        apply(schedules.delete, schedule_id)
        return scheduler.list()

    @api.post("/schedules/{schedule_id}/run")
    async def run_schedule(schedule_id: str, changes: dict[str, Any] = Body(default={})):
        require_workspace()
        scheduler.migrate_profiles()
        schedule = apply(schedules.get, schedule_id)
        if changes.get("expected_revision", schedule.revision) != schedule.revision:
            raise HTTPException(409, detail={"code": "schedule_changed", "message":
                "This schedule changed. Refresh schedules before running it."})
        if not scheduler.is_bound(schedule):
            raise HTTPException(409, detail={"code": "schedule_confirmation_required", "message":
                                            "Review and apply this schedule for the connected switch first."})
        return await scheduler.run_confirmed(schedule)

    simulator_routes.attach(api, ctx)

    app.include_router(api)
    ws_routes.attach(app, ctx)

    # The server provides the API only. The desktop app and the browser workspace start their own service.
    @app.get("/", include_in_schema=False, response_class=HTMLResponse)
    async def root():
        return API_ONLY_PAGE.format(name=PROJECT_NAME, version=__version__, disclaimer=html.escape(DISCLAIMER))

    return app


API_ONLY_PAGE = """<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{name}</title>
<style>
  :root {{ color-scheme: light dark; --bg: #f5f5f7; --fg: #1d1d1f; --muted: #6e6e73; }}
  @media (prefers-color-scheme: dark) {{ :root {{ --bg: #1c1c1e; --fg: #f5f5f7; --muted: #98989d; }} }}
  body {{ margin: 0; min-height: 100vh; display: grid; place-items: center; background: var(--bg); color: var(--fg);
         font: 15px/1.5 system-ui, -apple-system, "Segoe UI", sans-serif; }}
  main {{ max-width: 34rem; padding: 2rem; }}
  h1 {{ font-size: 1.4rem; margin: 0 0 .75rem; }}
  p {{ margin: 0 0 .75rem; }}
  .muted {{ color: var(--muted); font-size: .85rem; }}
  code {{ font-size: .9em; }}
  a {{ color: #0a84ff; }}
</style>
</head>
<body>
<main>
  <h1>{name} {version}</h1>
  <p>The server is running and provides the API only. The desktop app and the browser workspace start their own
  copy of this service.</p>
  <p>See the <a href="/docs">API documentation</a>.</p>
  <p class="muted">{disclaimer}</p>
</main>
</body>
</html>
"""
