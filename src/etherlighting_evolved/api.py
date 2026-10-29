"""HTTP and WebSocket API for the EE web UI. docs/API.md is the full contract."""

from __future__ import annotations

import asyncio
import contextlib
import logging
from contextlib import asynccontextmanager
from typing import Any

from fastapi import APIRouter, Body, Depends, FastAPI, Header, HTTPException, WebSocket, WebSocketDisconnect
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel, Field

from . import DISCLAIMER, PROJECT_NAME, SHORT_NAME, TAGLINE, __version__
from .color import NAMED_COLORS, Color
from .config import Config
from .effects import list_effects
from .engine import Engine, NotFound
from .presets import PresetStore
from .rules import FIELDS, STYLES, TEMPLATES
from .schedules import ACTIONS, Scheduler, ScheduleStore
from .session import LoginError, SessionManager
from .transport import SimulatedTransport

log = logging.getLogger(__name__)

ColorValue = str | list[int] | dict[str, int]


class LoginRequest(BaseModel):
    host: str = Field(description='Switch IP or hostname. "demo" logs in to the built-in simulator.')
    username: str = ""
    password: str = ""
    port: int = 22
    remember: bool = False


class EffectRequest(BaseModel):
    id: str
    params: dict[str, Any] = Field(default_factory=dict)


class ParamsRequest(BaseModel):
    params: dict[str, Any]


class ZonesRequest(BaseModel):
    zones: list[dict[str, Any]]


class RulesRequest(BaseModel):
    rules: list[dict[str, Any]]


class OrderRequest(BaseModel):
    ids: list[str]


class PortColor(BaseModel):
    color: ColorValue
    brightness: int | None = Field(default=None, ge=0, le=100)


class BulkPorts(BaseModel):
    ports: dict[int, PortColor]


class NumberValue(BaseModel):
    value: float


class PresetSave(BaseModel):
    name: str


class SimLink(BaseModel):
    up: bool


class SimAnomaly(BaseModel):
    code: int = Field(ge=0)


class SimPoe(BaseModel):
    pd_class: int | None = Field(default=None, ge=0, le=8, alias="class")


class HostKeyForget(BaseModel):
    host: str
    port: int = 22


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
        engine.set_effect(msg["id"], msg.get("params"), zone=msg.get("zone"))
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
               transport_factory=None) -> FastAPI:
    """Build the API. demo=True logs straight in to the simulator (no switch needed)."""
    config = config or Config()
    sessions = SessionManager(config, transport_factory=transport_factory)
    presets = PresetStore(config.data_dir / "presets.json")
    schedules = ScheduleStore(config.data_dir / "schedules.json")
    scheduler = Scheduler(schedules, presets, sessions)

    @asynccontextmanager
    async def lifespan(app: FastAPI):
        if demo:
            await sessions.connect("demo")
        elif autoconnect:
            asyncio.create_task(sessions.auto_connect())
        clock = asyncio.create_task(scheduler.run_forever(), name="ee-scheduler")
        try:
            yield
        finally:
            clock.cancel()
            with contextlib.suppress(asyncio.CancelledError):
                await clock
            await sessions.close()

    app = FastAPI(
        title=f"{PROJECT_NAME} API",
        version=__version__,
        description=f"{TAGLINE}\n\n**{DISCLAIMER}**",
        lifespan=lifespan,
    )
    app.state.sessions = sessions
    app.state.presets = presets
    app.state.schedules = schedules
    app.state.scheduler = scheduler
    app.add_middleware(
        CORSMiddleware, allow_origins=config.api.cors_origins, allow_methods=["*"], allow_headers=["*"]
    )

    token = config.api.token

    def require_token(authorization: str | None = Header(default=None)) -> None:
        if token and authorization != f"Bearer {token}":
            raise HTTPException(401, detail={"code": "bad_token", "message": "missing or wrong API token"})

    def engine() -> Engine:
        if sessions.engine is None:
            raise HTTPException(409, detail={"code": "not_logged_in", "message": "log in to a switch first"})
        return sessions.engine

    def apply(fn, *args, **kwargs):
        try:
            return fn(*args, **kwargs)
        except NotFound as exc:
            raise HTTPException(404, detail={"code": "not_found", "message": str(exc)}) from exc
        except (ValueError, KeyError, TypeError) as exc:
            raise HTTPException(400, detail={"code": "invalid", "message": str(exc).strip("'\"")}) from exc

    def simulator() -> SimulatedTransport:
        transport = engine().switch.transport
        if not isinstance(transport, SimulatedTransport):
            raise HTTPException(409, detail={"code": "not_simulated", "message": "only available after a demo login"})
        return transport

    api = APIRouter(prefix="/api", dependencies=[Depends(require_token)])

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

    @api.post("/session/logout")
    async def logout(forget: bool = False):
        return await sessions.disconnect(forget=forget)

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
        return engine().state()

    @api.get("/ports")
    async def ports():
        return engine().ports()

    # Zones and their effects

    @api.get("/zones")
    async def zones():
        return engine().state()["zones"]

    @api.put("/zones")
    async def set_zones(body: ZonesRequest):
        e = engine()
        apply(e.set_zones, body.zones)
        return e.state()

    @api.put("/effect")
    async def set_effect_everywhere(body: EffectRequest):
        """One effect on every port (replaces all zones)."""
        e = engine()
        apply(e.set_zones, [{"id": "all", "name": "All ports", "ports": list(range(1, e.num_ports + 1)),
                             "effect": {"id": body.id, "params": body.params}}])
        return e.state()

    @api.put("/zones/{zone_id}/effect")
    async def set_zone_effect(zone_id: str, body: EffectRequest):
        e = engine()
        apply(e.set_effect, body.id, body.params, zone=zone_id)
        return e.state()

    @api.patch("/zones/{zone_id}/params")
    async def set_zone_params(zone_id: str, body: ParamsRequest):
        e = engine()
        apply(e.update_params, body.params, zone=zone_id)
        return e.state()

    # Rules

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
        return apply(e.add_rule, rule, position=None if position is None else int(position)).to_dict()

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

    @api.post("/release")
    async def release():
        e = engine()
        e.release()
        return e.state()

    @api.post("/resume")
    async def resume():
        e = engine()
        e.resume()
        return e.state()

    # Presets

    @api.get("/presets")
    async def list_presets():
        return presets.list()

    @api.post("/presets", status_code=201)
    async def save_preset(body: PresetSave):
        apply(presets.save, body.name, engine().snapshot())
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
            presets.delete(name)
        except KeyError:
            raise HTTPException(404, detail={"code": "not_found", "message": f"no preset named {name!r}"}) from None
        return presets.list()

    # Schedules

    @api.get("/schedules")
    async def list_schedules():
        return {"schedules": schedules.list(), "actions": list(ACTIONS), "history": scheduler.history}

    @api.post("/schedules", status_code=201)
    async def add_schedule(schedule: dict[str, Any] = Body(...)):
        return apply(schedules.add, schedule).to_dict()

    @api.patch("/schedules/{schedule_id}")
    async def update_schedule(schedule_id: str, changes: dict[str, Any] = Body(...)):
        return apply(schedules.update, schedule_id, changes).to_dict()

    @api.delete("/schedules/{schedule_id}")
    async def delete_schedule(schedule_id: str):
        apply(schedules.delete, schedule_id)
        return schedules.list()

    @api.post("/schedules/{schedule_id}/run")
    async def run_schedule(schedule_id: str):
        scheduler.run(apply(schedules.get, schedule_id))
        return scheduler.history[0]

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
        if token and ws.query_params.get("token") != token:
            await ws.close(code=4401)
            return
        await ws.accept()
        mailbox = sessions.listen()
        mailbox.put({"type": "session", "session": sessions.info()})
        if sessions.engine is not None:
            mailbox.put(sessions.engine.state_message())
            mailbox.put(sessions.engine.frame_message())

        async def sender() -> None:
            while True:
                await ws.send_json(await mailbox.get())

        send_task = asyncio.create_task(sender())
        try:
            while True:
                msg = await ws.receive_json()
                if not isinstance(msg, dict):
                    mailbox.put({"type": "error", "code": "invalid", "message": "messages must be objects"})
                    continue
                current = sessions.engine
                if current is None and msg.get("type") != "ping":
                    mailbox.put({"type": "error", "id": msg.get("id"), "code": "not_logged_in",
                                         "message": "log in to a switch first"})
                    continue
                try:
                    reply = handle_ws_command(current, msg) if current else {"type": "pong", "id": msg.get("id")}
                except NotFound as exc:
                    reply = {"type": "error", "id": msg.get("id"), "code": "not_found", "message": str(exc)}
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
        app.mount("/", StaticFiles(directory=static_dir, html=True), name="ui")
    else:
        @app.get("/", include_in_schema=False)
        async def root():
            return {"name": PROJECT_NAME, "tagline": TAGLINE, "disclaimer": DISCLAIMER, "api": "/api",
                    "docs": "/docs", "ui": f"no web UI found; put the UI build (index.html) in {static_dir}"}

    return app
