"""Controller setup and cached topology for the local lighting workspace."""

from __future__ import annotations

import asyncio
import contextlib
import time
from pathlib import Path
from typing import Literal

from fastapi import APIRouter
from pydantic import BaseModel, ConfigDict, Field, StrictBool

from .controller import ControllerError
from .controller_credentials import ControllerLoginStore
from .controller_manager import ControllerManager
from .credentials import CredentialStoreError
from .port_names import PortNameStore


class ControllerLogin(BaseModel):
    controller_kind: Literal["self_hosted", "unifi_os", "cloud_connector"] = "self_hosted"
    auth_method: Literal["password", "api_key"] = "password"
    api_key: str = Field(default="", max_length=4096, repr=False)
    console_id: str | None = Field(default=None, max_length=160)
    url: str = Field(min_length=1, max_length=2048)
    username: str = Field(default="", max_length=256)
    password: str = Field(default="", max_length=4096, repr=False)
    site: str | None = Field(default=None, max_length=64)
    certificate_sha256: str | None = Field(default=None, max_length=128)
    verification_code: str | None = Field(default=None, max_length=8, repr=False)
    remember: StrictBool = False


class ControllerPreferences(BaseModel):
    model_config = ConfigDict(extra="forbid")
    auto_reconnect: StrictBool


class ControllerConsoleDiscovery(BaseModel):
    model_config = ConfigDict(extra="forbid")
    api_key: str = Field(min_length=1, max_length=4096, repr=False)


class ControllerIntegration:
    RETRY_DELAYS = (5, 15, 60)
    TRANSIENT_ERRORS = {"unreachable", "rate_limited", "controller_error"}

    def __init__(self, sessions, manager=None, *, data_dir: Path, store=None, clock=time.monotonic):
        self.sessions = sessions
        self.port_names = PortNameStore(data_dir / "port-names")
        self.manager = manager or ControllerManager()
        self.store = store or ControllerLoginStore(data_dir / "controller-login.json")
        self._clock = clock
        self._lock = asyncio.Lock()
        self._wake = asyncio.Event()
        self._saved = None
        self._enabled = False
        self._saved_active = False
        self._saved_error = None
        self._paused = self._blocked = self._connecting = False
        self._target = None
        self._next_due = 0.0
        self._failures = 0
        try:
            self._enabled = self.store.exists()
            self._saved = self.store.load()
        except CredentialStoreError:
            self._blocked = True
            self._saved_error = {"code": "saved_controller_login_unavailable",
                                 "message": "Connect your controller and save its login again."}
        self._enabled |= self._saved is not None

    def switch_host(self):
        engine = self.sessions.engine
        return self.sessions.host if engine and engine.connected and not self.sessions.simulated else None

    def status(self):
        status = self.manager.status()
        can_save = self.manager.can_save_login(self.switch_host())
        phase = ("off" if not self._enabled else "attention" if self._blocked else "paused" if self._paused
                 else "connecting" if self._connecting else "retrying" if self._failures
                 else "attention" if self._saved_error
                 else "waiting" if not self.switch_host()
                 else "connected" if status.get("connected") else "waiting")
        saved_target = ({"url": self._saved["url"], "controller_kind": self._saved["controller_kind"],
                         "auth_method": self._saved.get("auth_method", "password"),
                         "console_id": self._saved.get("console_id")} if self._saved else None)
        return {**status, "preferences_supported": True, "remember_on_connect_supported": True,
                "saved_target": saved_target, "auto_reconnect": self._enabled,
                "saved_login_available": self._saved is not None,
                "can_save_login": can_save, "saved_login_matches_connection": bool(self._saved_active and can_save),
                "reconnect_state": phase, "saved_login_error": self._saved_error}

    def ports(self, ports):
        enriched = self.manager.enrich_ports(ports, self.switch_host())
        return self.port_names.decorate(enriched, self.sessions.schedule_identity())

    def state(self, state):
        return {**state, "sfp": self.ports(state.get("sfp", [])), "controller": self.status()}

    def message(self, message):
        if message.get("type") == "state":
            return {**message, "state": self.state(message["state"])}
        if message.get("type") == "frame":
            return {**message, "ports": self.ports(message.get("ports", [])),
                    "sfp": self.ports(message.get("sfp", [])), "controller": self.status()}
        return message

    async def _complete(self, operation):
        task = asyncio.create_task(operation)
        # asyncio.wait, unlike shield, does not report a failure that arrives after the caller was canceled.
        try:
            await asyncio.wait({task})
        except asyncio.CancelledError:
            # Drain the client's bounded HTTPS operation before shutdown clears its credentials.
            while not task.done():
                with contextlib.suppress(asyncio.CancelledError):
                    await asyncio.wait({task})
            with contextlib.suppress(asyncio.CancelledError, Exception):
                task.result()
            raise
        return task.result()

    def _failure(self, error):
        self._failures += 1
        self._saved_error = {"code": error["code"], "message": error["message"]}
        transient = error["code"] in self.TRANSIENT_ERRORS
        self._blocked = not transient or self._failures > len(self.RETRY_DELAYS)
        if not self._blocked:
            retry_after = error.get("retry_after_seconds", 0)
            if not isinstance(retry_after, int) or isinstance(retry_after, bool):
                retry_after = 0
            self._next_due = self._clock() + max(self.RETRY_DELAYS[self._failures - 1], min(3600, retry_after))

    async def tick(self):
        async with self._lock:
            host = self.switch_host()
            if host is None or self._paused or self._blocked:
                return
            target = (id(self.sessions.engine), host)
            if target != self._target:
                self._target, self._next_due, self._failures = target, 0, 0
            if self._clock() < self._next_due:
                return
            current = self.manager.status()
            if current.get("pending_site_choice") and not current.get("site"):
                return
            if not current.get("site") and not (self._enabled and self._saved):
                return
            self._connecting = True
            try:
                if current.get("site"):
                    result = await self._complete(self.manager.refresh(host))
                else:
                    result = await self._complete(self.manager.connect(**self._saved, switch_host=host))
                    self._saved_active = bool(result.get("connected"))
                if target != (id(self.sessions.engine), self.switch_host()):
                    await self._complete(self.manager.disconnect())
                    return
                if result.get("error"):
                    self._failure(result["error"])
                elif result.get("connected"):
                    self._failures = 0
                    if not self._saved_error or self._saved_error["code"] != "controller_login_save_failed":
                        self._saved_error = None
                    self._next_due = self._clock() + 15
                else:
                    self._failure({"code": "controller_login_required",
                                   "message": "Connect your controller to resume automatic reconnect."})
            except ControllerError as exc:
                self._failure(exc.detail())
            except Exception:
                self._blocked = True
                self._saved_error = {"code": "controller_reconnect_failed",
                                     "message": "Connect your controller to resume automatic reconnect."}
            finally:
                self._connecting = False

    async def run(self):
        while True:
            await self.tick()
            self._wake.clear()
            with contextlib.suppress(TimeoutError):
                await asyncio.wait_for(self._wake.wait(), 1)

    def _save_login(self, record):
        if self._saved != record:
            self.store.save(record)
            self._saved = record
        self._enabled = self._saved_active = True

    def _save_failed(self):
        self._saved_error = {"code": "controller_login_save_failed",
                             "message": "Could not save the controller login. Apply automatic reconnect to try again."}

    async def preferences(self, enabled: bool):
        async with self._lock:
            if enabled:
                record = await self.manager.login_for_saving(self.switch_host())
                try:
                    self._save_login(record)
                except CredentialStoreError as exc:
                    self._save_failed()
                    raise ControllerError(**self._saved_error, status=500) from exc
                self._paused = self._blocked = False
                self._failures = 0
                self._next_due = self._clock() + 15
            else:
                try:
                    self.store.clear()
                except CredentialStoreError as exc:
                    self._saved_error = {"code": "controller_login_remove_failed",
                                         "message": "Could not remove the saved controller login. Try again."}
                    raise ControllerError(**self._saved_error, status=500) from exc
                self._saved = None
                self._saved_active = False
                self._enabled = False
            self._saved_error = None
            self._wake.set()
            return self.status()

    async def connect(self, body: ControllerLogin):
        async with self._lock:
            return await self._complete(self._connect(body))

    async def _connect(self, body: ControllerLogin):
        host = self.switch_host()
        if not host:
            raise ControllerError("switch_required", "Connect your switch before adding its controller.", 409)
        target = (id(self.sessions.engine), host)
        try:
            result = await self.manager.connect(**body.model_dump(exclude={"remember"}), switch_host=host)
        except ControllerError as exc:
            if not self.manager.status().get("connected"):
                self._blocked = True
                self._saved_error = {"code": exc.code, "message": exc.message}
            raise
        record = None
        if (not result.get("pending_site_choice") and result.get("connected")
                and (body.remember or self._saved is not None)):
            record = await self.manager.login_for_saving(host)
        if target != (id(self.sessions.engine), self.switch_host()):
            await self.manager.disconnect()
            self._saved_active = False
            raise ControllerError("switch_required", "Connect your switch before adding its controller.", 409)
        if result.get("pending_site_choice"):
            return self.status()
        self._target = target
        self._paused = False
        self._blocked = bool(result.get("error"))
        self._failures = 0
        self._saved_error = None
        self._saved_active = record is not None and record == self._saved
        if body.remember and record is not None:
            try:
                self._save_login(record)
            except CredentialStoreError:
                self._save_failed()
        self._next_due = self._clock() + 15
        self._wake.set()
        return self.status()

    async def disconnect(self):
        async with self._lock:
            self._paused = True
            self._saved_error = None
            self._blocked = False
            await self._complete(self.manager.disconnect())
            self._saved_active = False
            return self.status()

    async def discover_consoles(self, body: ControllerConsoleDiscovery):
        async with self._lock:
            return await self._complete(self.manager.discover_consoles(body.api_key))

    async def cancel(self):
        async with self._lock:
            await self._complete(self.manager.cancel())
            return self.status()

    async def close(self):
        async with self._lock:
            self._paused = True
            try:
                await self._complete(self.manager.disconnect())
            finally:
                self._saved = None
                self._saved_active = False

    def routes(self, router: APIRouter):
        @router.get("/controller")
        async def status():
            return self.status()

        @router.post("/controller/connect")
        async def connect(body: ControllerLogin):
            return await self.connect(body)

        @router.post("/controller/consoles")
        async def discover_consoles(body: ControllerConsoleDiscovery):
            return await self.discover_consoles(body)

        @router.put("/controller/preferences")
        async def preferences(body: ControllerPreferences):
            return await self.preferences(body.auto_reconnect)

        @router.post("/controller/disconnect")
        async def disconnect():
            return await self.disconnect()

        @router.post("/controller/cancel")
        async def cancel():
            return await self.cancel()
