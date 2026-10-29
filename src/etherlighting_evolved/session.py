"""Login sessions: connect to a switch from the login screen, remember it, reconnect at start-up.

One EE server controls one switch at a time. `SessionManager.connect()` logs in over SSH, checks that the
device has Etherlighting, and then starts the render engine. Logging in with the host "demo" uses the built-in
simulator.

The current setup is saved when it changes (scene.json, or scene-demo.json for the simulator) and restored on the
next login.
"""

from __future__ import annotations

import asyncio
import contextlib
import json
import logging
import os
from pathlib import Path

from .config import Config
from .engine import Engine
from .mailbox import Mailbox
from .presets import SceneStore
from .switch import EtherlightSwitch
from .transport import (
    AuthenticationError,
    HostKeyMismatchError,
    HostKeyStore,
    SimulatedTransport,
    SSHTransport,
    Transport,
    TransportError,
    UnreachableError,
)

log = logging.getLogger(__name__)

DEMO_HOSTS = {"demo", "simulator", "sim"}


class LoginError(Exception):
    """A login attempt failed. `code` tells the UI what to show; `status` is the HTTP status to use."""

    def __init__(self, code: str, message: str, status: int) -> None:
        super().__init__(message)
        self.code = code
        self.message = message
        self.status = status


class SavedLogin:
    """The remembered login (only written when the user ticks "remember me").

    Stored as plain JSON in the EE data directory, readable only by the current user where the OS allows it.
    """

    def __init__(self, path: Path) -> None:
        self.path = Path(path)

    def load(self) -> dict | None:
        try:
            data = json.loads(self.path.read_text("utf-8"))
        except (OSError, json.JSONDecodeError):
            return None
        return data if isinstance(data, dict) and data.get("host") else None

    def save(self, host: str, port: int, username: str, password: str) -> None:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.path.write_text(
            json.dumps({"host": host, "port": port, "username": username, "password": password}, indent=2), "utf-8"
        )
        with contextlib.suppress(OSError):
            os.chmod(self.path, 0o600)

    def clear(self) -> None:
        with contextlib.suppress(FileNotFoundError):
            self.path.unlink()


class SessionManager:
    def __init__(self, config: Config, *, transport_factory=None) -> None:
        self.config = config
        self.host_keys = HostKeyStore(config.data_dir / "known_switches.json")
        self.saved = SavedLogin(config.data_dir / "login.json")
        self.scenes = {
            False: SceneStore(config.data_dir / "scene.json"),
            True: SceneStore(config.data_dir / "scene-demo.json"),
        }
        self.engine: Engine | None = None
        self.host: str | None = None
        self.port: int = 22
        self.username: str | None = None
        self.simulated = False
        self.last_error: dict | None = None
        self._lock = asyncio.Lock()
        self._listeners: set[Mailbox] = set()
        self._transport_factory = transport_factory  # tests can inject a transport
        self._autosave_task: asyncio.Task | None = None
        self._saved_revision = -1

    # Info

    def info(self) -> dict:
        saved = self.saved.load()
        engine = self.engine
        return {
            "logged_in": engine is not None,
            "connected": bool(engine and engine.connected),
            "host": self.host,
            "port": self.port,
            "username": self.username,
            "simulated": self.simulated,
            "switch": engine.switch_info if engine else None,
            "error": (engine.error if engine and engine.error else None) or self.last_error,
            "remembered": {"host": saved["host"], "port": saved.get("port", 22), "username": saved.get("username")}
            if saved
            else None,
        }

    # Notifications to websocket clients

    def listen(self) -> Mailbox:
        """A mailbox that receives session messages plus the current (and any future) engine's state/frames."""
        mailbox = Mailbox()
        self._listeners.add(mailbox)
        if self.engine is not None:
            self.engine.attach(mailbox)
        return mailbox

    def unlisten(self, mailbox: Mailbox) -> None:
        self._listeners.discard(mailbox)
        if self.engine is not None:
            self.engine.unsubscribe(mailbox)

    def _notify(self) -> None:
        message = {"type": "session", "session": self.info()}
        for mailbox in list(self._listeners):
            mailbox.put(message)

    # Login and logout

    def _make_transport(self, host: str, port: int, username: str, password: str, key_file: str) -> Transport:
        if self._transport_factory is not None:
            return self._transport_factory(host=host, port=port, username=username, password=password)
        if host.strip().lower() in DEMO_HOSTS:
            return SimulatedTransport(lit_ports=self.config.switch.num_ports)
        return SSHTransport(
            host.strip(),
            port=port,
            username=username or None,
            password=password or None,
            key_file=key_file or None,
            host_keys=self.host_keys,
        )

    async def connect(
        self,
        host: str,
        username: str = "",
        password: str = "",
        *,
        port: int = 22,
        remember: bool | None = None,
        key_file: str = "",
    ) -> dict:
        """Log in. remember=True saves the login, False forgets any saved one, None leaves it as it is.

        The current session (if any) keeps running until the new login has succeeded.
        """
        host = (host or "").strip()
        if not host:
            raise LoginError("missing_host", "Enter the switch's IP address", 400)
        demo = host.lower() in DEMO_HOSTS
        if not demo and not (username or key_file):
            raise LoginError("missing_username", "Enter the switch's SSH username", 400)
        if not 1 <= int(port) <= 65535:
            raise LoginError("bad_port", "SSH port must be 1-65535", 400)
        async with self._lock:
            transport = self._make_transport(host, port, username, password, key_file)
            switch = EtherlightSwitch(
                transport, num_ports=self.config.switch.num_ports, keepalive=self.config.engine.keepalive
            )
            try:
                await switch.connect()
                info = await switch.probe()
            except AuthenticationError as exc:
                raise self._fail("auth_failed", str(exc), 401) from exc
            except UnreachableError as exc:
                raise self._fail("unreachable", str(exc), 502) from exc
            except HostKeyMismatchError as exc:
                raise self._fail("host_key_changed", str(exc), 409) from exc
            except TransportError as exc:
                raise self._fail("ssh_error", str(exc), 502) from exc
            if not info.get("port_rgb"):
                with contextlib.suppress(Exception):
                    await switch.close()
                raise self._fail(
                    "no_etherlighting",
                    f"Logged in to {info.get('hostname') or host}, but it has no Etherlighting (etherlight.mcu "
                    "port_rgb not found). EE needs a UniFi switch with Etherlighting.",
                    422,
                )
            e = self.config.engine
            engine = Engine(
                switch,
                fps=e.fps,
                link_poll=e.link_poll,
                brightness=e.brightness,
                dimming=e.dimming,
                effect=e.effect,
                effect_params=e.effect_params,
            )
            engine.switch_info, engine.connected = info, True  # already logged in and probed above
            simulated = isinstance(transport, SimulatedTransport)
            self._restore_scene(engine, simulated)
            await self._disconnect_locked()  # hand over from the previous session only now that the new one works
            for mailbox in self._listeners:
                engine.attach(mailbox)
            await engine.start()
            self.engine = engine
            self.host, self.port, self.username = host, port, (username or None)
            self.simulated = simulated
            self.last_error = None
            self._saved_revision = engine.revision
            self._autosave_task = asyncio.create_task(self._autosave(engine, simulated), name="ee-autosave")
            if remember is True and not demo:
                self.saved.save(host, port, username, password)
            elif remember is False:
                self.saved.clear()
            log.info("logged in to %s (%s)", host, info.get("hostname"))
            self._notify()
            return self.info()

    def _fail(self, code: str, message: str, status: int) -> LoginError:
        self.last_error = {"code": code, "message": message}
        self._notify()
        return LoginError(code, message, status)

    async def disconnect(self, *, forget: bool = False) -> dict:
        async with self._lock:
            await self._disconnect_locked()
            if forget:
                self.saved.clear()
            self._notify()
            return self.info()

    async def _disconnect_locked(self) -> None:
        engine, self.engine = self.engine, None
        if self._autosave_task is not None:
            self._autosave_task.cancel()
            with contextlib.suppress(asyncio.CancelledError):
                await self._autosave_task
            self._autosave_task = None
        if engine is not None:
            self._save_scene(engine, self.simulated)
            await engine.stop()  # stops writing; the switch's own Etherlighting resumes within ~2 s
        self.host, self.username, self.simulated = None, None, False

    # The live scene

    def _restore_scene(self, engine: Engine, simulated: bool) -> None:
        scene = self.scenes[simulated].load()
        if not scene:
            return
        try:
            engine.apply_snapshot(scene)
        except (ValueError, KeyError, TypeError) as exc:
            log.warning("the saved scene no longer loads (%s); starting from the defaults", exc)

    def _save_scene(self, engine: Engine, simulated: bool) -> None:
        if engine.revision == self._saved_revision:
            return
        try:
            self.scenes[simulated].save(engine.snapshot())
            self._saved_revision = engine.revision
        except OSError as exc:
            log.warning("could not save the scene: %s", exc)

    async def _autosave(self, engine: Engine, simulated: bool, interval: float = 2.0) -> None:
        while True:
            await asyncio.sleep(interval)
            self._save_scene(engine, simulated)

    async def auto_connect(self) -> None:
        """At start-up: use the [switch] config / EE_* env vars, else the remembered login, if any."""
        sw = self.config.switch
        if sw.host:
            target = {"host": sw.host, "port": sw.port, "username": sw.username, "password": sw.password,
                      "key_file": sw.key_file}
        else:
            saved = self.saved.load()
            if not saved:
                return
            target = {"host": saved["host"], "port": int(saved.get("port", 22)), "username": saved.get("username", ""),
                      "password": saved.get("password", ""), "key_file": ""}
        try:
            await self.connect(target["host"], target["username"], target["password"], port=target["port"],
                               key_file=target["key_file"], remember=None)
        except LoginError as exc:
            log.warning("automatic login to %s failed: %s", target["host"], exc.message)

    def forget_host_key(self, host: str, port: int = 22) -> bool:
        return self.host_keys.forget(host, port)

    async def close(self) -> None:
        await self.disconnect()
