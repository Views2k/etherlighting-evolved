"""HTTP and WebSocket API for the EE web UI. docs/API.md is the full contract."""

from __future__ import annotations

import asyncio
import contextlib
import hashlib
import html
import json
import logging
from contextlib import asynccontextmanager
from contextvars import ContextVar
from datetime import datetime
from typing import Any
from weakref import ref

from fastapi import APIRouter, Body, Depends, FastAPI, Header, HTTPException, WebSocket, WebSocketDisconnect
from fastapi.exception_handlers import request_validation_exception_handler
from fastapi.exceptions import RequestValidationError
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import HTMLResponse, JSONResponse
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel, Field

from . import DISCLAIMER, PROJECT_NAME, SHORT_NAME, TAGLINE, __version__
from .color import NAMED_COLORS, Color
from .config import Config
from .controller_api import ControllerIntegration
from .effects import list_effects
from .engine import Engine, NotFound
from .guard import LOOPBACK_BINDS, RequestGuard
from .lighting_library import attach_library_routes
from .port_names import attach_port_name_routes
from .presets import PresetStore, ProfileConflict, ProfileStoreError
from .rules import FIELDS, STYLES, TEMPLATES, Rule
from .scene_history import attach_routes as attach_history_routes
from .schedules import ACTIONS, Scheduler, ScheduleStore, ScheduleStoreError
from .session import LoginError, SessionManager
from .transport import SimulatedTransport, TransportError

log = logging.getLogger(__name__)

ColorValue = str | list[int] | dict[str, int]


class UIStaticFiles(StaticFiles):
    """Keep a saved browser profile from reusing an earlier app's interface."""

    def is_not_modified(self, response_headers, request_headers) -> bool:
        return False

    async def get_response(self, path, scope):
        response = await super().get_response(path, scope)
        response.headers["Cache-Control"] = "no-store"
        return response


class LoginRequest(BaseModel):
    host: str = Field(description='Switch IP or hostname. "demo" logs in to the built-in simulator.')
    username: str = ""
    password: str = ""
    port: int = 22
    remember: bool = False


class SceneChoiceRequest(BaseModel):
    connection_id: str = Field(min_length=1, max_length=128)
    choice: str
    legacy_revision: str = Field(default="", max_length=128)


class EffectRequest(BaseModel):
    id: str
    params: dict[str, Any] = Field(default_factory=dict)
    clear_overrides: bool = False


class PlaybackEffectRequest(BaseModel):
    zone: str = Field(min_length=1, max_length=128)
    id: str = Field(min_length=1, max_length=128)
    params: dict[str, Any] = Field(default_factory=dict)
    expected_revision: str = Field(pattern=r"^[a-f0-9]{64}$")


class ParamsRequest(BaseModel):
    params: dict[str, Any]


class ZonesRequest(BaseModel):
    zones: list[dict[str, Any]]


class RulesRequest(BaseModel):
    rules: list[dict[str, Any]]


class OrderRequest(BaseModel):
    ids: list[str]


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


class PresetSave(BaseModel):
    name: str
    scene: dict[str, Any] | None = None
    expected_revision: str | None = Field(default=None, min_length=1)
    overwrite: bool = False


class RuleTest(BaseModel):
    port: int = Field(ge=1, le=256)
    rules: list[dict[str, Any]] = Field(max_length=256)
    at: str | None = None


class ProfileChange(BaseModel):
    expected_revision: int = Field(ge=1)
    action: str = "replace"
    name: str | None = None
    disable_schedules: bool = False


class PresetAction(BaseModel):
    name: str
    action: str
    target: str | None = None


class SimLink(BaseModel):
    up: bool


class SimAnomaly(BaseModel):
    code: int = Field(ge=0)


class SimPoe(BaseModel):
    pd_class: int | None = Field(default=None, ge=0, le=8, alias="class")


class HostKeyForget(BaseModel):
    host: str
    port: int = 22


def scene_envelope(scene: dict) -> dict:
    def canonical(value):
        if isinstance(value, float) and value.is_integer():
            return int(value)
        if isinstance(value, dict):
            return {key: canonical(item) for key, item in value.items()}
        if isinstance(value, list):
            return [canonical(item) for item in value]
        return value

    encoded = json.dumps(canonical(scene), sort_keys=True, separators=(",", ":"), allow_nan=False).encode("utf-8")
    return {"scene": scene, "revision": hashlib.sha256(encoded).hexdigest()}


def handle_ws_command(engine: Engine, msg: dict) -> dict | None:
    """The quick, high-frequency controls. Everything else goes through REST."""
    kind = msg.get("type")
    if kind == "ping":
        return {"type": "pong", "id": msg.get("id")}
    if kind == "set_port":
        engine.set_override(msg["port"], msg["color"], msg.get("brightness"))
    elif kind == "set_ports":
        engine.set_overrides({int(k): v for k, v in msg["ports"].items()})
    elif kind == "clear_port":
        engine.clear_override(msg.get("port"))
    elif kind == "set_effect":
        clear = msg.get("clear_overrides", False)
        if not isinstance(clear, bool):
            raise ValueError("clear_overrides must be true or false")
        engine.set_effect(msg["id"], msg.get("params"), zone=msg.get("zone"), clear_overrides=clear)
    elif kind == "set_params":
        engine.update_params(msg["params"], zone=msg.get("zone"))
    elif kind == "update_rule":
        engine.update_rule(msg["id"], msg["changes"])
    elif kind == "set_brightness":
        engine.set_brightness(msg["value"])
    elif kind == "release":
        engine.release()
    elif kind == "resume":
        engine.resume()
    else:
        raise ValueError(f"unknown message type {kind!r}")
    return {"type": "ack", "id": msg["id"]} if msg.get("id") is not None else None


def create_app(config: Config | None = None, *, demo: bool = False, autoconnect: bool = True,
               transport_factory=None, on_show=None, on_quit=None) -> FastAPI:
    """Build the API. demo=True logs straight in to the simulator (no switch needed).

    on_show and on_quit come from the desktop app. A second copy of the app uses them to bring the running copy's
    window to the front, and the installer uses them to stop it.
    """
    config = config or Config()
    config.api.validate()
    sessions = SessionManager(config, transport_factory=transport_factory)
    controller = ControllerIntegration(sessions, data_dir=config.data_dir)
    presets = PresetStore(config.data_dir / "presets.json")
    schedules = ScheduleStore(config.data_dir / "schedules.json")
    scheduler = Scheduler(schedules, presets, sessions)

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
    expected_workspace: ContextVar[str | None] = ContextVar("ee_expected_workspace", default=None)

    @app.middleware("http")
    async def workspace_context(request, call_next):
        marker = expected_workspace.set(request.headers.get("X-EE-Workspace"))
        try:
            return await call_next(request)
        finally:
            expected_workspace.reset(marker)

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

    token = config.api.token

    def require_token(authorization: str | None = Header(default=None)) -> None:
        if token and authorization != f"Bearer {token}":
            raise HTTPException(401, detail={"code": "bad_token", "message": "missing or wrong API token"})

    def require_workspace() -> None:
        expected = expected_workspace.get()
        if expected is not None and expected != sessions.workspace_id():
            raise HTTPException(409, detail={"code": "workspace_changed", "message":
                                            "The connected switch changed. Review your changes before applying."})

    def engine() -> Engine:
        require_workspace()
        if sessions.handoff_pending:
            raise HTTPException(409, detail={"code": "handoff_unconfirmed", "message":
                                            "Finish the pending handoff before changing lighting."})
        if sessions.engine is None:
            raise HTTPException(409, detail={"code": "not_logged_in", "message": "log in to a switch first"})
        return sessions.engine

    def apply(fn, *args, **kwargs):
        try:
            return fn(*args, **kwargs)
        except NotFound as exc:
            raise HTTPException(404, detail={"code": "not_found", "message": str(exc)}) from exc
        except TransportError as exc:
            raise HTTPException(409, detail={"code": "output_unconfirmed", "message": str(exc)}) from exc
        except (ValueError, KeyError, TypeError, OverflowError) as exc:
            raise HTTPException(400, detail={"code": "invalid", "message": str(exc).strip("'\"")}) from exc

    async def await_output(fn, *args):
        try:
            return await fn(*args)
        except (TransportError, ValueError) as exc:
            raise HTTPException(409, detail={"code": "output_unconfirmed", "message": str(exc)}) from exc

    async def confirm_sfp_output(e, expected_sfp: dict[int, int]):
        for port in sorted(expected_sfp):
            if e._sfp_revisions[port] != expected_sfp[port]:
                raise HTTPException(409, detail={
                    "code": "output_unconfirmed",
                    "message": "SFP settings changed during confirmation. Review the current setup.",
                })
            await await_output(e.wait_for_sfp, port)
        if any(e._sfp_revisions[p] != expected_sfp[p] for p in expected_sfp):
            raise HTTPException(409, detail={"code": "output_unconfirmed", "message":
                                            "SFP settings changed during confirmation. Review the current setup."})
        if e.mode == "running" and any(e._sfp_acknowledged[p] != expected_sfp[p] for p in expected_sfp):
            raise HTTPException(409, detail={"code": "output_unconfirmed", "message":
                                            "Lighting settings were saved, but SFP output is still pending."})

    async def apply_port_change(e, fn, *args, **kwargs):
        previous = dict(e._sfp_revisions)
        apply(fn, *args, **kwargs)
        await confirm_sfp_output(e, {port: e._sfp_revisions[port] for port in e.switch.sfp_ports
                                     if e._sfp_revisions[port] != previous[port]})
        return e.state()

    def simulator() -> SimulatedTransport:
        transport = engine().switch.transport
        if not isinstance(transport, SimulatedTransport):
            raise HTTPException(409, detail={"code": "not_simulated", "message": "only available after a demo login"})
        return transport

    api = APIRouter(prefix="/api", dependencies=[Depends(require_token)])
    controller.routes(api)
    attach_library_routes(api, engine, sessions, config)
    attach_port_name_routes(api, engine, sessions, controller.port_names)

    # About and login

    @api.get("/health")
    async def health():
        return {"ok": True, "name": PROJECT_NAME, "version": __version__, "logged_in": sessions.engine is not None}

    @api.get("/about")
    async def about():
        return {"name": PROJECT_NAME, "short_name": SHORT_NAME, "version": __version__, "tagline": TAGLINE,
                "disclaimer": DISCLAIMER}

    @api.get("/session")
    async def session_info():
        return sessions.info()

    @api.post("/session/login")
    async def login(body: LoginRequest):
        try:
            return await sessions.connect(body.host, body.username, body.password, port=body.port,
                                          remember=body.remember)
        except LoginError as exc:
            return JSONResponse(status_code=exc.status, content={"detail": {"code": exc.code, "message": exc.message}})

    @api.post("/session/scene-choice")
    async def scene_choice(body: SceneChoiceRequest):
        try:
            return await sessions.choose_scene(body.connection_id, body.choice, body.legacy_revision)
        except LoginError as exc:
            return JSONResponse(status_code=exc.status, content={"detail": {"code": exc.code, "message": exc.message}})

    @api.post("/session/logout")
    async def logout(forget: bool = False):
        require_workspace()
        try:
            return await sessions.disconnect(forget=forget)
        except LoginError as exc:
            return JSONResponse(status_code=exc.status, content={"detail": {"code": exc.code, "message": exc.message}})

    @api.post("/session/restore")
    async def restore_saved_login(resume: bool = False):
        try:
            return await sessions.restore_saved(resume=resume)
        except LoginError as exc:
            return JSONResponse(status_code=exc.status, content={"detail": {"code": exc.code, "message": exc.message}})

    @api.post("/session/switch-playback")
    async def save_switch_playback():
        require_workspace()
        try:
            return await sessions.save_to_switch()
        except LoginError as exc:
            return JSONResponse(status_code=exc.status, content={"detail": {"code": exc.code, "message": exc.message}})

    @api.post("/session/switch-playback/check")
    async def check_switch_playback():
        require_workspace()
        return await sessions.check_playback()

    async def playback_effect_response(body=None):
        require_workspace()
        try:
            return await sessions.playback_effects(body)
        except LoginError as exc:
            return JSONResponse(status_code=exc.status, content={"detail": {"code": exc.code, "message": exc.message}})

    @api.get("/session/switch-playback/effects")
    async def playback_effects():
        return await playback_effect_response()

    @api.put("/session/switch-playback/effects")
    async def edit_playback_effect(body: PlaybackEffectRequest):
        return await playback_effect_response(body)

    @api.post("/session/handoff")
    async def handoff_to_unifi():
        require_workspace()
        try:
            return await sessions.handoff()
        except LoginError as exc:
            return JSONResponse(status_code=exc.status, content={"detail": {"code": exc.code, "message": exc.message}})

    @api.post("/session/forget")
    async def forget_saved_login():
        try:
            return await sessions.forget_saved_login()
        except LoginError as exc:
            return JSONResponse(status_code=exc.status, content={"detail": {"code": exc.code, "message": exc.message}})

    if on_show is not None:
        @api.post("/desktop/show", include_in_schema=False)
        async def desktop_show():
            on_show()
            return {"ok": True}

    if on_quit is not None:
        @api.post("/desktop/quit", include_in_schema=False)
        async def desktop_quit():
            on_quit()
            return {"ok": True}

    @api.post("/session/forget-host-key")
    async def forget_host_key(body: HostKeyForget):
        return {"forgotten": sessions.forget_host_key(body.host, body.port)}

    # Catalogs: what the UI can offer (no login needed)

    @api.get("/effects")
    async def effects():
        return list_effects()

    @api.get("/rules/fields")
    async def rule_fields():
        return {"fields": [f.schema() for f in FIELDS.values()], "styles": list(STYLES)}

    @api.get("/rules/templates")
    async def rule_templates():
        return TEMPLATES

    @api.get("/colors")
    async def colors():
        return {name: Color(*rgb).to_hex() for name, rgb in NAMED_COLORS.items()}

    # Live state

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
        return await apply_port_change(e, e.set_zones, body.zones)

    @api.put("/effect")
    async def set_effect_everywhere(body: EffectRequest):
        """One effect on every port (replaces all zones)."""
        e = engine()
        ports = sorted(set(range(1, e.num_ports + 1)) | {p for zone in e.zones for p in zone.ports})
        return await apply_port_change(e, e.set_zones, [{"id": "all", "name": "All ports", "ports": ports,
                                                         "effect": {"id": body.id, "params": body.params}}],
                                       clear_overrides=body.clear_overrides)

    @api.put("/zones/{zone_id}/effect")
    async def set_zone_effect(zone_id: str, body: EffectRequest):
        e = engine()
        return await apply_port_change(e, e.set_effect, body.id, body.params, zone=zone_id,
                                       clear_overrides=body.clear_overrides)

    @api.patch("/zones/{zone_id}/params")
    async def set_zone_params(zone_id: str, body: ParamsRequest):
        e = engine()
        return await apply_port_change(e, e.update_params, body.params, zone=zone_id)

    # Rules

    @api.post("/rules/test")
    async def test_rules(body: RuleTest):
        e = engine()
        if body.port not in [*range(1, e.num_ports + 1), *e.switch.sfp_ports]:
            raise HTTPException(400, detail={"code": "invalid", "message": "Choose a port on this switch."})
        now = apply(datetime.fromisoformat, body.at) if body.at else datetime.now()
        if now.tzinfo is not None:
            raise HTTPException(400, detail={"code": "invalid", "message":
                "Use the EE computer's local date and time."})
        rules = [apply(Rule.parse, value, num_ports=e.num_ports, extra_ports=tuple(e.switch.sfp_ports))
                 for value in body.rules]
        facts = e.facts(body.port, now)
        optical = body.port in e.switch.sfp_ports
        indicator_active = optical and (e.sfp_sync.mode == "ethernet" or body.port in e._sfp_zones)
        light = e._sfp_light(body.port) if optical else None
        candidates = [rule for rule in rules if not optical or rule.ports]
        winner = next((rule for rule in candidates if rule.matches(facts)), None)
        suppressed = optical and ((light.mode == "off" and not indicator_active)
                     or (winner and winner.look.style == "effect"
                         and light.mode == "unmanaged" and not indicator_active))
        if suppressed:
            winner = None
        override = e.overrides.get(body.port) if not optical else None
        results = [{"id": rule.id, "name": rule.name, "enabled": rule.enabled,
                    "in_scope": bool((not optical or rule.ports) and (not rule.ports or body.port in rule.ports)),
                    "matches": bool(rule in candidates and rule.matches(facts)),
                    "conditions": [{**condition.to_dict(), "label": FIELDS[condition.field].label,
                                    "actual": facts.value(condition.field), "passes": condition.test(facts)}
                                   for condition in rule.conditions]} for rule in rules]
        return {"port": body.port, "at": now.isoformat(timespec="minutes"), "rules": results,
                "winner": {"id": winner.id, "name": winner.name} if winner else None,
                "source": ("individual_color" if override else "rule" if winner else
                           "port_effect" if not optical or indicator_active else "sfp_setting"),
                "output_running": e.mode == "running", "scene_revision": scene_envelope(e.snapshot())["revision"]}

    @api.get("/rules")
    async def rules():
        return engine().state()["rules"]

    @api.put("/rules")
    async def replace_rules(body: RulesRequest):
        e = engine()
        apply(e.set_rules, body.rules)
        return e.state()["rules"]

    @api.post("/rules", status_code=201)
    async def add_rule(rule: dict[str, Any] = Body(...)):
        e = engine()
        position = rule.pop("position", None)
        return apply(e.add_rule, rule, position=None if position is None else apply(int, position)).to_dict()

    @api.put("/rules/order")
    async def order_rules(body: OrderRequest):
        e = engine()
        apply(e.reorder_rules, body.ids)
        return e.state()["rules"]

    @api.patch("/rules/{rule_id}")
    async def update_rule(rule_id: str, changes: dict[str, Any] = Body(...)):
        return apply(engine().update_rule, rule_id, changes).to_dict()

    @api.delete("/rules/{rule_id}")
    async def delete_rule(rule_id: str):
        e = engine()
        apply(e.delete_rule, rule_id)
        return e.state()["rules"]

    # Colors set by hand

    @api.put("/sfp/sync")
    async def set_sfp_sync(settings: dict[str, Any] = Body(...)):
        e = engine()
        return controller.state(await apply_port_change(e, e.set_sfp_sync, settings))

    @api.put("/sfp/{port}")
    async def set_sfp(port: int, settings: dict[str, Any] = Body(...)):
        e = engine()
        return await apply_port_change(e, e.set_sfp, port, settings)

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

    @api.post("/session/recover")
    async def recover_connection():
        async with sessions._lock:
            e = engine()
            await await_output(e.recover_connection)
            return e.state()

    # Scene history

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
        try:
            affected_sfp = apply(e.apply_snapshot, body.scene, preserve_unchanged=body.preserve_unchanged)
        except AttributeError as exc:
            raise HTTPException(400, detail={
                "code": "invalid", "message": "The lighting setup has invalid settings.",
            }) from exc
        accepted = scene_envelope(e.snapshot())
        if body.confirm_output:
            await confirm_sfp_output(e, {port: e._sfp_revisions[port] for port in affected_sfp})
        return accepted

    async def restore_history_scene(scene, revision):
        return await restore_scene(SceneRestore(scene=scene, expected_revision=revision,
                                               preserve_unchanged=True, confirm_output=True))

    attach_history_routes(api, engine, apply, restore_history_scene)

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
        apply(e.apply_snapshot, scene)
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

    @api.post("/presets/action")
    async def preset_action(body: PresetAction):
        if body.action == "apply":
            return await apply_preset(body.name)
        if body.action == "delete":
            return await delete_preset(body.name)
        if body.action in {"rename", "duplicate"} and body.target is not None:
            operation = presets.rename if body.action == "rename" else presets.duplicate
            return transfer_preset(operation, body.name, body.target)
        raise HTTPException(400, detail={"code": "invalid", "message": "Choose a profile action and provide "
                                        "a new name when renaming or duplicating."})

    # Schedules

    @api.get("/schedules")
    async def list_schedules():
        return {"schedules": scheduler.list(), "actions": list(ACTIONS), "history": scheduler.history,
                "history_available": not scheduler._journal_error,
                "clock": sessions.engine.state().get("schedule_clock") if sessions.engine else None}

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

    # Simulator controls (demo login only)

    @api.put("/simulator/links/{port}")
    async def simulator_link(port: int, body: SimLink):
        apply(simulator().set_link, port, body.up)
        return {"port": port, "up": body.up}

    @api.put("/simulator/anomaly/{port}")
    async def simulator_anomaly(port: int, body: SimAnomaly):
        apply(simulator().set_anomaly, port, body.code)
        return {"port": port, "code": hex(body.code)}

    @api.put("/simulator/poe/{port}")
    async def simulator_poe(port: int, body: SimPoe):
        sim = simulator()
        if port not in sim.poe_class:
            raise HTTPException(400, detail={"code": "invalid", "message": f"port {port} has no PoE"})
        sim.poe_class[port] = body.pd_class
        return {"port": port, "class": body.pd_class}

    app.include_router(api)

    @app.websocket("/api/ws")
    async def websocket(ws: WebSocket):
        authorization = ws.headers.get("authorization")
        authenticated = authorization == f"Bearer {token}" if authorization else ws.query_params.get("token") == token
        if token and not authenticated:
            await ws.close(code=4401)
            return
        bound_engine = (ref(sessions.engine) if sessions.engine is not None and sessions.engine.connected
                        and not sessions.handoff_pending else None)
        bound_workspace = sessions.workspace_id()
        handshake_workspace = ws.headers.get("X-EE-Workspace")
        await ws.accept()
        mailbox = sessions.listen()
        mailbox.put({"type": "session", "session": sessions.info()})
        if sessions.engine is not None:
            mailbox.put(sessions.engine.state_message())
            mailbox.put(sessions.engine.frame_message())

        async def sender() -> None:
            while True:
                await ws.send_json(controller.message(await mailbox.get()))

        send_task = asyncio.create_task(sender())
        try:
            while True:
                msg = await ws.receive_json()
                if not isinstance(msg, dict):
                    mailbox.put({"type": "error", "code": "invalid", "message": "messages must be objects"})
                    continue
                current = None if sessions.handoff_pending else sessions.engine
                if current is None and msg.get("type") != "ping":
                    mailbox.put({"type": "error", "id": msg.get("id"), "code": "not_logged_in",
                                         "message": "log in to a switch first"})
                    continue
                if msg.get("type") != "ping" and (
                    bound_engine is None or current is not bound_engine()
                    or sessions.workspace_id() != bound_workspace
                    or handshake_workspace is not None and handshake_workspace != bound_workspace
                    or "workspace_id" in msg and msg["workspace_id"] != bound_workspace
                ):
                    mailbox.put({"type": "error", "id": msg.get("id"), "code": "workspace_changed",
                                 "message": "The switch session changed. Reopen this connection before "
                                            "changing lighting."})
                    continue
                try:
                    reply = handle_ws_command(current, msg) if current else {"type": "pong", "id": msg.get("id")}
                    if current and msg.get("type") == "release":
                        await current.wait_for_output_idle()
                except NotFound as exc:
                    reply = {"type": "error", "id": msg.get("id"), "code": "not_found", "message": str(exc)}
                except TransportError as exc:
                    reply = {"type": "error", "id": msg.get("id"), "code": "output_unconfirmed", "message": str(exc)}
                except (ValueError, KeyError, TypeError) as exc:
                    reply = {"type": "error", "id": msg.get("id"), "code": "invalid",
                             "message": str(exc).strip("'\"")}
                if reply:
                    mailbox.put(reply)
        except WebSocketDisconnect:
            pass
        except Exception:
            log.debug("websocket closed", exc_info=True)
        finally:
            send_task.cancel()
            with contextlib.suppress(asyncio.CancelledError, Exception):
                await send_task
            sessions.unlisten(mailbox)

    # The web UI, if one has been built into static_dir
    static_dir = config.resolve(config.api.static_dir)
    if (static_dir / "index.html").is_file():
        app.mount("/", UIStaticFiles(directory=static_dir, html=True), name="ui")
    else:
        @app.get("/", include_in_schema=False, response_class=HTMLResponse)
        async def root():
            return NO_UI_PAGE.format(name=PROJECT_NAME, version=__version__, disclaimer=html.escape(DISCLAIMER),
                                     folder=html.escape(str(static_dir)))

    return app


NO_UI_PAGE = """<!doctype html>
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
  <p>The server is running, but this copy doesn't include the web interface.</p>
  <p>Add the interface's build to <code>{folder}</code>, or use the <a href="/docs">API documentation</a> in the
  meantime.</p>
  <p class="muted">{disclaimer}</p>
</main>
</body>
</html>
"""
