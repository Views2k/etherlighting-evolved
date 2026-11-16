"""What the route modules share: the services, the request checks and the mapping of errors to responses."""

from __future__ import annotations

import hmac
from contextvars import ContextVar
from dataclasses import dataclass, field

from fastapi import Header, HTTPException

from ..config import Config
from ..controller_api import ControllerIntegration
from ..engine import Engine, NotFound
from ..presets import PresetStore
from ..scene_history import scene_revision
from ..schedules import Scheduler, ScheduleStore
from ..session import SessionManager
from ..transport import SimulatedTransport, TransportError

# Every rule is evaluated for every port on every frame. The engine itself accepts longer lists, so a saved scene
# from before this limit still loads at login.
MAX_RULES = 256


def limit_rules(rules: object) -> None:
    if isinstance(rules, list) and len(rules) > MAX_RULES:
        raise HTTPException(400, detail={"code": "invalid", "message": f"Use at most {MAX_RULES} rules."})


def scene_envelope(scene: dict) -> dict:
    return {"scene": scene, "revision": scene_revision(scene)}


def token_matches(authorization: str | None, token: str) -> bool:
    # Header values arrive decoded as latin-1, and the configured token is printable ASCII.
    return hmac.compare_digest((authorization or "").encode("latin-1"), f"Bearer {token}".encode("ascii"))


def apply(fn, *args, **kwargs):
    """Run a change and report what it refuses as the API's error responses."""
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


@dataclass
class ApiContext:
    config: Config
    sessions: SessionManager
    controller: ControllerIntegration
    presets: PresetStore
    schedules: ScheduleStore
    scheduler: Scheduler
    # The X-EE-Workspace header of the request being handled, set by the app's middleware.
    expected_workspace: ContextVar[str | None] = field(
        default_factory=lambda: ContextVar("ee_expected_workspace", default=None))

    def require_token(self, authorization: str | None = Header(default=None)) -> None:
        token = self.config.api.token
        if token and not token_matches(authorization, token):
            raise HTTPException(401, detail={"code": "bad_token", "message": "missing or wrong API token"})

    def require_workspace(self) -> None:
        expected = self.expected_workspace.get()
        if expected is not None and expected != self.sessions.workspace_id():
            raise HTTPException(409, detail={"code": "workspace_changed", "message":
                                            "The connected switch changed. Review your changes before applying."})

    def engine(self) -> Engine:
        self.require_workspace()
        if self.sessions.handoff_pending:
            raise HTTPException(409, detail={"code": "handoff_unconfirmed", "message":
                                            "Finish the pending handoff before changing lighting."})
        if self.sessions.engine is None:
            raise HTTPException(409, detail={"code": "not_logged_in", "message": "log in to a switch first"})
        return self.sessions.engine

    def simulator(self) -> SimulatedTransport:
        transport = self.engine().switch.transport
        if not isinstance(transport, SimulatedTransport):
            raise HTTPException(409, detail={"code": "not_simulated", "message": "only available after a demo login"})
        return transport

    async def apply_port_change(self, e: Engine, fn, *args, **kwargs) -> dict:
        """Apply a change and wait until the SFP ports it changed acknowledge their output."""
        previous = e.sfp_revisions()
        apply(fn, *args, **kwargs)
        current = e.sfp_revisions()
        await await_output(e.confirm_sfp_output, {port: current[port] for port in e.switch.sfp_ports
                                                  if current[port] != previous[port]})
        return e.state()

    async def commit_scene(self, e: Engine, scene: dict, *, preserve_unchanged: bool, confirm_output: bool) -> dict:
        """Apply a scene, optionally wait for SFP output, and update the switch's offline copy."""
        limit_rules(scene.get("rules"))
        try:
            affected_sfp = apply(e.apply_snapshot, scene, preserve_unchanged=preserve_unchanged)
        except AttributeError as exc:
            raise HTTPException(400, detail={
                "code": "invalid", "message": "The lighting setup has invalid settings.",
            }) from exc
        accepted = scene_envelope(e.snapshot())
        if confirm_output:
            revisions = e.sfp_revisions()
            await await_output(e.confirm_sfp_output, {port: revisions[port] for port in affected_sfp})
        if getattr(e.switch.transport, "desktop_output", None):
            self.sessions.save_current_scene(e)
            try:
                await self.sessions.sync_offline_lighting(e)
            except (OSError, ValueError, TransportError) as exc:
                raise HTTPException(503, detail={"code": "offline_scene_unconfirmed",
                    "message": "Lighting applied in EE. The switch has not confirmed its offline copy."}) from exc
        return accepted
