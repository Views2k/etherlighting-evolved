"""HTTP and WebSocket API for the EE web UI. docs/API.md is the full contract.

create_app builds the services, the app and its middleware. The routes are in the routes package, one module per
area, and share an ApiContext.
"""

from __future__ import annotations

import asyncio
import contextlib
import html
from contextlib import asynccontextmanager

from fastapi import APIRouter, Depends, FastAPI
from fastapi.exception_handlers import request_validation_exception_handler
from fastapi.exceptions import RequestValidationError
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import HTMLResponse, JSONResponse

from . import DISCLAIMER, PROJECT_NAME, TAGLINE, __version__
from .config import Config
from .controller import ControllerError
from .controller_api import ControllerIntegration
from .guard import LOOPBACK_BINDS, RequestGuard
from .lighting_library import attach_library_routes
from .port_names import attach_port_name_routes
from .presets import PresetStore, ProfileStoreError
from .routes import about, fans, lighting, profiles, rules, simulator, ws
from .routes import schedules as schedule_routes
from .routes import session as session_routes
from .routes.context import ApiContext
from .schedules import Scheduler, ScheduleStore, ScheduleStoreError
from .session import LoginError, SessionManager


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

    api = APIRouter(prefix="/api", dependencies=[Depends(ctx.require_token)])
    controller.routes(api)
    attach_library_routes(api, ctx.engine, sessions, config)
    attach_port_name_routes(api, ctx.engine, sessions, controller.port_names)
    for area in (about, session_routes, lighting, rules, fans, profiles, schedule_routes, simulator):
        area.attach(api, ctx)
    app.include_router(api)
    ws.attach(app, ctx)

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
