"""Login sessions: connect to a switch from the login screen, remember it, reconnect at start-up.

One EE server controls one switch at a time. Login verifies the authenticated switch and lighting profile.
A previously bound device restores its scene. A new device waits for an explicit scene choice before output.
Logging in with the host "demo" uses the built-in simulator.

Real scenes are bound to the authenticated device and qualified layout. The legacy scene remains unchanged.
The simulator keeps its separate scene-demo.json.
"""

from __future__ import annotations

import asyncio
import contextlib
import hashlib
import json
import logging
import secrets
from dataclasses import dataclass, field

from . import device_playback as playback
from .config import Config
from .credentials import CredentialStoreError, SavedLogin
from .engine import Engine
from .handoff import HandoffIntent
from .hardware import PRO_XG8
from .mailbox import Mailbox
from .ownership import OutputJournal, OutputOwnershipError, device_identity
from .presets import DeviceSceneStore, SceneStore
from .scene_history import SceneHistory
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
SCENE_CHOICE_TIMEOUT = 300.0


@dataclass(repr=False)
class PendingSceneChoice:
    engine: Engine
    store: SceneStore
    host: str
    port: int
    username: str
    password: str = field(repr=False)
    remember: bool | None
    simulated: bool
    generation: object
    fingerprint: str | None
    expires_at: float = 0.0
    connection_id: str = field(default_factory=lambda: secrets.token_urlsafe(24))
    legacy_revision: str = ""
    legacy_available: bool = False
    initial_scene: dict = field(default_factory=dict)

    def public(self) -> dict:
        return {"connection_id": self.connection_id, "legacy_revision": self.legacy_revision,
                "legacy_available": self.legacy_available}


class LoginError(Exception):
    """A login attempt failed. `code` tells the UI what to show; `status` is the HTTP status to use."""

    def __init__(self, code: str, message: str, status: int) -> None:
        super().__init__(message)
        self.code = code
        self.message = message
        self.status = status


class SessionManager:
    def __init__(self, config: Config, *, transport_factory=None) -> None:
        self.config = config
        self.host_keys = HostKeyStore(config.data_dir / "known_switches.json")
        self.saved = SavedLogin(config.data_dir / "login.json")
        self.output_journal = OutputJournal()
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
        self._auto_connecting = False
        self._saved_restore_task: asyncio.Task | None = None
        self._pending: PendingSceneChoice | None = None
        self._pending_expiry: asyncio.Task | None = None
        self._scene_store: SceneStore | None = None
        self._handoff = HandoffIntent(config.data_dir / "switch-connection-intent.json")
        self._playback = playback.PlaybackRecord(config.data_dir / "switch-playback.json")
        self._playback_status = "last_confirmed" if self._playback.data else None
        self._playback_retry: asyncio.Task | None = None
        self._closing = False
        self._historic_handoff_unknown = self._handoff.state == "unconfirmed" and not self._playback.data
        self._handoff_task: asyncio.Task | None = None
        self._handoff_generation = 0
        self._cleanup_engines: list[Engine] = []
        self._owned_switches: set[EtherlightSwitch] = set()
        self._unsaved_scenes: dict[object, tuple[SceneStore, dict]] = {}

    # Info

    def info(self) -> dict:
        saved, saved_error = None, None
        try:
            saved = self.saved.load()
        except CredentialStoreError as exc:
            saved_error = str(exc)
        engine = self.engine
        return {
            "logged_in": engine is not None,
            "connected": bool(engine and engine.connected),
            "connecting": self._auto_connecting,
            "host": self.host,
            "port": self.port,
            "username": self.username,
            "simulated": self.simulated,
            "workspace_id": self.workspace_id(),
            "switch": engine.switch_info if engine else None,
            "error": (engine.error if engine and engine.error else None) or self.last_error,
            "remembered": {"host": saved["host"], "port": saved.get("port", 22), "username": saved.get("username")}
            if saved
            else None,
            "has_saved_login": saved is not None or saved_error is not None,
            "saved_login_error": saved_error,
            "pending_scene_choice": self._pending.public() if self._pending and not self.handoff_pending else None,
            "automatic_connect_paused": self._handoff.paused,
            "handoff_state": self._handoff.state,
            "switch_playback": self._playback_status if self._playback.data else None,
            "can_save_to_switch": bool(engine and not self.simulated and not self._playback.data
                                       and playback.supported(engine.switch) and saved),
            "handed_off": self._handoff.state == "complete" and engine is None and not self._owned_switches,
        }

    def workspace_id(self) -> str | None:
        """A stable browser-storage scope without exposing the connection identity."""
        if self.host is None:
            return None
        identity = (["demo"] if self.simulated else
                    ["switch", getattr(self._scene_store, "identity", None),
                     self.host.strip().casefold(), self.port, self.username or ""])
        encoded = json.dumps(identity, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
        return hashlib.sha256(encoded).hexdigest()

    def schedule_identity(self) -> str | None:
        if self.engine is None or self.handoff_pending:
            return None
        return "demo" if self.simulated else getattr(self._scene_store, "identity", None)

    @property
    def handoff_pending(self) -> bool:
        return self._handoff.state in {"pending", "unconfirmed"}

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

    async def restore_saved(self, *, resume: bool = False) -> dict:
        """Reconnect with the protected saved login; simultaneous retries share one attempt."""
        if self._playback.data:
            return self.info()
        if self._handoff.paused and not resume:
            return self.info()
        if self._pending is not None or self.engine is not None and not self.simulated:
            return self.info()
        task = self._saved_restore_task
        if task is None or task.done():
            task = asyncio.create_task(self._restore_saved(resume=resume), name="ee-restore-login")
            self._saved_restore_task = task
        try:
            return await asyncio.shield(task)
        finally:
            if task.done() and self._saved_restore_task is task:
                self._saved_restore_task = None

    async def _restore_saved(self, *, resume: bool = False) -> dict:
        self._auto_connecting = True
        self._notify()
        try:
            try:
                saved = self.saved.load()
            except CredentialStoreError as exc:
                raise self._fail("saved_login_unavailable", str(exc), 409) from exc
            if not saved:
                raise self._fail("no_saved_login", "Enter your SSH credentials to connect to the switch.", 409)
            await self.connect(saved["host"], saved.get("username", ""), saved.get("password", ""),
                               port=saved.get("port", 22), remember=None, automatic=not resume)
        finally:
            self._auto_connecting = False
            self._notify()
        return self.info()

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
        automatic: bool = False,
    ) -> dict:
        """Log in. remember=True saves the login, False forgets any saved one, None leaves it as it is.

        The current session (if any) keeps running until the new login has succeeded.
        """
        generation = self._handoff_generation
        if self.handoff_pending and not automatic:
            raise self._fail("handoff_unconfirmed", "Finish the pending handoff before reconnecting.", 409)
        host = (host or "").strip()
        if not host:
            raise LoginError("missing_host", "Enter the switch's IP address", 400)
        demo = host.lower() in DEMO_HOSTS
        if not demo and not (username or key_file):
            raise LoginError("missing_username", "Enter the switch's SSH username", 400)
        if not 1 <= int(port) <= 65535:
            raise LoginError("bad_port", "SSH port must be 1-65535", 400)
        async with self._lock:
            if self._playback.data:
                raise self._fail("switch_playback_reserved", "Use Handoff to UniFi before connecting again.", 409)
            if automatic and self._handoff.paused:
                return self.info()
            if generation != self._handoff_generation:
                raise self._fail("handoff_unconfirmed", "The connection request was stopped for handoff. "
                                 "Connect again after it completes.", 409)
            if self._handoff.state in {"pending", "unconfirmed"}:
                raise self._fail("handoff_unconfirmed", "Finish the pending handoff before reconnecting.", 409)
            if self._cleanup_engines:
                raise self._fail("output_recovery_required", "The previous lighting session has no confirmed "
                                 "shutdown. Complete its recovery review before reconnecting.", 409)
            known = {self.engine.switch} if self.engine is not None else set()
            if self._pending is not None:
                known.add(self._pending.engine.switch)
            if self._owned_switches - known:
                raise self._fail("handoff_unconfirmed", "Finish the pending handoff before reconnecting.", 409)
            await self._clear_pending_locked()
            transport = self._make_transport(host, port, username, password, key_file)
            switch = EtherlightSwitch(
                transport, num_ports=self.config.switch.num_ports, keepalive=self.config.engine.keepalive,
                auto_detect=not isinstance(transport, SimulatedTransport),
            )
            self._owned_switches.add(switch)
            try:
                await switch.connect()
                info = await switch.probe()
            except AuthenticationError as exc:
                await self._close_switch(switch)
                raise self._fail("auth_failed", str(exc), 401) from exc
            except UnreachableError as exc:
                await self._close_switch(switch)
                raise self._fail("unreachable", str(exc), 502) from exc
            except HostKeyMismatchError as exc:
                await self._close_switch(switch)
                raise self._fail("host_key_changed", str(exc), 409) from exc
            except TransportError as exc:
                await self._close_switch(switch)
                raise self._fail("ssh_error", str(exc), 502) from exc
            except BaseException:
                with contextlib.suppress(Exception):
                    await self._close_switch(switch)
                raise
            if switch.hardware_profile is not None and not switch.hardware_profile.output_verified:
                with contextlib.suppress(Exception):
                    await self._close_switch(switch)
                raise self._fail("compatibility_verification_required",
                                 "This switch was identified, but its lighting interface still needs "
                                 "compatibility verification before EE can control it.", 422)
            if not info.get("port_rgb"):
                with contextlib.suppress(Exception):
                    await self._close_switch(switch)
                raise self._fail(
                    "no_etherlighting",
                    f"Logged in to {info.get('hostname') or host}, but it has no Etherlighting (etherlight.mcu "
                    "port_rgb not found). EE needs a UniFi switch with Etherlighting.",
                    422,
                )
            try:
                switch.require_output_profile()
            except ValueError as exc:
                with contextlib.suppress(Exception):
                    await self._close_switch(switch)
                raise self._fail("compatibility_verification_required", str(exc), 422) from exc
            e = self.config.engine
            try:
                engine = Engine(
                    switch,
                    fps=e.fps,
                    max_commands=e.max_commands,
                    transition=e.transition,
                    gamma=e.gamma,
                    link_poll=e.link_poll,
                    brightness=e.brightness,
                    dimming=e.dimming,
                    effect=e.effect,
                    effect_params=e.effect_params,
                )
            except (ValueError, TypeError) as exc:
                await self._close_switch(switch)
                raise self._fail("invalid_settings", str(exc), 400) from exc
            engine.switch_info, engine.connected = info, True  # already logged in and probed above
            simulated = isinstance(transport, SimulatedTransport)
            if not simulated:
                try:
                    if (isinstance(transport, SSHTransport) and playback.supported(switch)
                            and not await playback.available(transport)):
                        raise self._fail("switch_playback_reserved",
                                         "Return switch playback to UniFi from the PC that saved it.", 409)
                    await self._recover_retained_output(switch, host, port, transport.host_key_fingerprint)
                except OutputOwnershipError as exc:
                    await self._close_switch(switch)
                    raise self._fail(exc.code, str(exc), 409) from exc
                except BaseException:
                    with contextlib.suppress(Exception):
                        await self._close_switch(switch)
                    raise
            try:
                store = self.scenes[True] if simulated else self._device_store(switch)
                scene = self._load_scene(store)
                candidate = PendingSceneChoice(
                    engine, store, host, port, username, password, remember, simulated,
                    transport.lighting_generation, getattr(transport, "host_key_fingerprint", None))
                candidate.initial_scene = engine.snapshot()
                if not simulated and scene is None:
                    _, candidate.legacy_revision, candidate.legacy_available = self._legacy_scene()
                    candidate.expires_at = asyncio.get_running_loop().time() + SCENE_CHOICE_TIMEOUT
                    self._pending = candidate
                    self._pending_expiry = asyncio.create_task(self._expire_pending(candidate),
                                                               name="ee-scene-choice-expiry")
                    self.last_error = None
                    self._notify()
                    return self.info()
                if scene:
                    engine.apply_snapshot(scene)
            except (ValueError, TypeError, KeyError) as exc:
                await self._close_switch(switch)
                raise self._fail("invalid_saved_scene", str(exc), 422) from exc
            except BaseException:
                with contextlib.suppress(Exception):
                    await self._close_switch(switch)
                raise
            return await self._activate(candidate)

    async def _recover_retained_output(self, switch: EtherlightSwitch, host: str, port: int,
                                       fingerprint: str) -> None:
        """Resolve ownership records left by a session whose shutdown was never confirmed.

        The same authenticated switch: take the record over, return its lighting to the defaults with
        acknowledged commands, then remove the record. A different switch at a known address: drop only the
        endpoint record. Anything unconfirmed keeps the record and refuses the login as before.
        """
        current = self.engine.switch.output_lease if self.engine else None
        retained = self.output_journal.retained(host, port, fingerprint, current=current)
        if retained["endpoint"] is None and retained["device"] is None:
            return
        if retained["device"] is None:
            self.output_journal.release_endpoint(host, port, fingerprint)
            log.info("removed the stale lighting ownership record for %s:%s; the switch there is not the one it "
                     "described", host, port)
            return
        lease = self.output_journal.adopt(host, port, fingerprint)
        switch.output_lease = lease
        try:
            await switch.restore_defaults()
            lease.uncertain = False
            lease.finish()
        except OutputOwnershipError:
            raise
        except Exception as exc:
            raise OutputOwnershipError(
                "An earlier lighting session has no confirmed shutdown and its recovery did not finish "
                f"({exc}). Connect again to retry. The record is kept in {self.output_journal.directory}."
            ) from exc
        finally:
            switch.output_lease = None
        log.info("recovered the unconfirmed earlier lighting session on %s:%s; the switch lighting is back at "
                 "its defaults", host, port)

    def _device_store(self, switch: EtherlightSwitch) -> DeviceSceneStore:
        hardware = switch.hardware_profile or PRO_XG8
        profile = [device_identity(switch.transport.host_key_fingerprint), hardware.key, switch.num_ports,
                   list(switch.sfp_ports), hardware.daemon_sha256, hardware.mcu_sha256]
        identity = hashlib.sha256(json.dumps(profile, separators=(",", ":")).encode("utf-8")).hexdigest()
        return DeviceSceneStore(self.config.data_dir / "device-scenes" / f"{identity}.json", identity)

    def _legacy_scene(self) -> tuple[dict | None, str, bool]:
        path = self.scenes[False].path
        try:
            content = path.read_bytes()
        except FileNotFoundError:
            return None, "absent", False
        revision = hashlib.sha256(content).hexdigest()
        try:
            scene = json.loads(content)
        except (ValueError, UnicodeError):
            return None, revision, False
        valid = isinstance(scene, dict) and bool(scene)
        return scene if valid else None, revision, valid

    async def _clear_pending_locked(self) -> None:
        pending = self._pending
        expiry, self._pending_expiry = self._pending_expiry, None
        if expiry is not None and expiry is not asyncio.current_task():
            expiry.cancel()
            with contextlib.suppress(asyncio.CancelledError):
                await expiry
        if pending is not None:
            await self._close_switch(pending.engine.switch)
            self._pending = None

    async def _close_switch(self, switch: EtherlightSwitch) -> None:
        await switch.close()
        self._owned_switches.discard(switch)

    async def _expire_pending(self, pending: PendingSceneChoice) -> None:
        await asyncio.sleep(SCENE_CHOICE_TIMEOUT)
        async with self._lock:
            if self._pending is pending:
                await self._clear_pending_locked()
                self._fail("scene_choice_expired", "The lighting setup choice expired. Connect your switch again.", 409)

    async def choose_scene(self, connection_id: str, choice: str, legacy_revision: str) -> dict:
        async with self._lock:
            pending = self._pending
            if pending is None or not secrets.compare_digest(connection_id, pending.connection_id):
                raise self._fail("scene_choice_expired", "This connection is no longer waiting for lighting setup. "
                                 "Connect your switch again.", 409)
            if asyncio.get_running_loop().time() >= pending.expires_at:
                await self._clear_pending_locked()
                raise self._fail("scene_choice_expired", "The lighting setup choice expired. Connect again.", 409)
            if choice == "cancel":
                await self._clear_pending_locked()
                self.last_error = None
                self._notify()
                return self.info()
            if choice not in {"use_saved", "start_new"}:
                raise self._fail("invalid_scene_choice", "Choose saved lighting or a new setup.", 400)
            switch = pending.engine.switch
            if (not switch.connected or switch.transport.lighting_generation is not pending.generation
                    or switch.transport.host_key_fingerprint != pending.fingerprint):
                await self._clear_pending_locked()
                raise self._fail("scene_choice_expired", "The verified switch connection changed. Connect again.", 409)
            switch.require_output_profile()
            scene, revision, available = self._legacy_scene()
            if choice == "use_saved":
                if revision != legacy_revision or revision != pending.legacy_revision:
                    pending.legacy_revision, pending.legacy_available = revision, available
                    raise self._fail("scene_choice_stale", "Saved lighting changed. Review the setup choice again.",
                                     409)
                if not available:
                    raise self._fail("saved_scene_unavailable", "Choose a new setup for this switch.", 409)
                try:
                    pending.engine.apply_snapshot(scene)
                except (ValueError, TypeError, KeyError) as exc:
                    raise self._fail("invalid_saved_scene", "The saved lighting could not be loaded. "
                                     "Choose a new setup.", 422) from exc
            else:
                pending.engine.apply_snapshot(pending.initial_scene)
            try:
                pending.store.save(pending.engine.snapshot())
            except OSError as exc:
                raise self._fail("scene_save_failed", "The lighting setup could not be saved. Check the data "
                                 "folder and try again.", 500) from exc
            self._pending = None
            expiry, self._pending_expiry = self._pending_expiry, None
            if expiry is not None:
                expiry.cancel()
                with contextlib.suppress(asyncio.CancelledError):
                    await expiry
            return await self._activate(pending)

    async def _activate(self, candidate: PendingSceneChoice) -> dict:
        engine, switch = candidate.engine, candidate.engine.switch
        try:
            if self._handoff.state in {"pending", "unconfirmed"}:
                raise self._fail("handoff_unconfirmed", "Finish the pending handoff before reconnecting.", 409)
            if candidate.remember is True and candidate.host.lower() not in DEMO_HOSTS:
                self.saved.save(candidate.host, candidate.port, candidate.username, candidate.password)
            elif candidate.remember is False and candidate.host.lower() not in DEMO_HOSTS:
                self.saved.clear()
            if not candidate.simulated:
                self.output_journal.check(candidate.host, candidate.port, candidate.fingerprint,
                                          current=self.engine.switch.output_lease if self.engine else None)
            await self._disconnect_locked()
            scene = self._load_scene(candidate.store)
            if scene:
                engine.apply_snapshot(scene)
            if not candidate.simulated:
                switch.output_lease = self.output_journal.claim(candidate.host, candidate.port, candidate.fingerprint)
            for mailbox in self._listeners:
                engine.attach(mailbox)
            await engine.start()
        except CredentialStoreError as exc:
            await self._close_switch(switch)
            raise self._fail("saved_login_failed", str(exc), 500) from exc
        except OutputOwnershipError as exc:
            with contextlib.suppress(Exception):
                await self._close_switch(switch)
            raise self._fail(exc.code, str(exc), 409) from exc
        except TransportError as exc:
            with contextlib.suppress(Exception):
                await self._close_switch(switch)
            raise self._fail("output_unconfirmed", str(exc), 409) from exc
        except (ValueError, TypeError) as exc:
            with contextlib.suppress(Exception):
                await self._close_switch(switch)
            raise self._fail("invalid_settings", str(exc), 400) from exc
        except BaseException:
            with contextlib.suppress(Exception):
                await self._close_switch(switch)
            raise
        self.engine = engine
        self.host, self.port, self.username = candidate.host, candidate.port, (candidate.username or None)
        self.simulated = candidate.simulated
        self._scene_store = candidate.store
        engine.attach_history(SceneHistory(self.config.data_dir / "scene-history.json"), self.schedule_identity())
        if self._handoff.state in {"pending", "unconfirmed"}:
            await self._disconnect_locked()
            raise self._fail("handoff_unconfirmed", "The switch connection was stopped for handoff.", 409)
        if self._handoff.paused and not candidate.simulated:
            try:
                self._handoff.save("none")
            except OSError as exc:
                await self._disconnect_locked()
                raise self._fail("handoff_save_failed", "The connection preference could not be saved. "
                                 "Check the data folder and try again.", 409) from exc
        self.last_error = None
        self._saved_revision = engine.revision
        self._autosave_task = asyncio.create_task(self._autosave(engine, candidate.simulated), name="ee-autosave")
        self._notify()
        return self.info()

    def _fail(self, code: str, message: str, status: int) -> LoginError:
        self.last_error = {"code": code, "message": message}
        self._notify()
        return LoginError(code, message, status)

    async def disconnect(self, *, forget: bool = False) -> dict:
        async with self._lock:
            await self._clear_pending_locked()
            try:
                await self._disconnect_locked()
            except TransportError as exc:
                raise self._fail("output_unconfirmed", str(exc), 409) from exc
            if forget:
                if self._playback.data:
                    raise self._fail("switch_playback_reserved", "Finish playback before removing its login.", 409)
                try:
                    self.saved.clear()
                except CredentialStoreError as exc:
                    raise self._fail("saved_login_forget_failed", str(exc), 500) from exc
            self._notify()
            return self.info()

    def _playback_switch(self, identity):
        saved = self.saved.load()
        if not saved:
            raise ValueError("Save the switch login before using switch playback.")
        host, port = saved["host"], saved.get("port", 22)
        if device_identity(self.host_keys.get(host, port)) != identity:
            raise ValueError("The saved login belongs to a different switch.")
        transport = self._make_transport(host, port, saved["username"], saved["password"], "")
        return EtherlightSwitch(transport, auto_detect=True)

    async def _open_player(self, switch, identity):
        await switch.connect()
        if device_identity(switch.transport.host_key_fingerprint) != identity:
            raise ValueError("The connected switch does not match the saved scene.")
        await switch.probe()
        if not playback.supported(switch):
            raise ValueError("The switch firmware needs playback verification.")

    async def save_to_switch(self) -> dict:
        async with self._lock:
            engine = self.engine
            if self._playback.data or self.handoff_pending or self.simulated or not engine or not engine.connected:
                raise self._fail("playback_unavailable", "Connect to the switch before saving its scene.", 409)
            try:
                config, palette = playback.compile_scene(engine)
                identity = device_identity(engine.switch.transport.host_key_fingerprint)
                switch = self._playback_switch(identity)
                owner = secrets.token_hex(24)
                revision = engine.revision
                try:
                    await self._open_player(switch, identity)
                    if not await playback.available(switch.transport):
                        raise ValueError("Another switch player already holds this switch.")
                    lease = engine.switch.output_lease
                    if lease is None or lease.closed or lease.uncertain or not all(lease.owns(p) for p in lease.paths):
                        raise OutputOwnershipError("The desktop no longer owns the current scene.")
                    self._playback.save({"state": "preparing", "device": identity, "owner": owner,
                                         "desktop_owner": lease.owner})
                    self._playback_status = "preparing"
                    await playback.stage(switch.transport, owner, config, palette)
                    if self.handoff_pending or engine.revision != revision or engine.mode != "running":
                        raise ValueError("The scene changed during transfer. Use Handoff to UniFi before retrying.")
                    # Desktop shutdown drains in-flight writes and releases its optical ports first.
                    await self._disconnect_locked()
                    if self._unsaved_scenes:
                        raise ValueError("Your scene could not be saved on this PC. Retry handoff before closing EE.")
                    self._reserve_player_output(switch, owner)
                    await playback.start(switch.transport, owner)
                    self._playback.save({**self._playback.data, "state": "last_playing"})
                    self._playback_status = "playing"
                    self.last_error = None
                finally:
                    await switch.close()
            except (OSError, TransportError, ValueError, CredentialStoreError) as exc:
                raise self._fail("playback_unavailable", str(exc), 409) from exc
            self._notify()
            return self.info()

    def _reserve_player_output(self, switch, owner):
        transport = switch.transport
        switch.output_lease = self.output_journal.player_lease(transport.host, transport.port,
                                                               transport.host_key_fingerprint, owner)

    async def playback_effects(self, change=None):
        """Edit the verified player's group effects without acquiring desktop output."""
        async with self._lock:
            record = self._playback.data
            if self.handoff_pending or not record or record.get("state") != "last_playing":
                raise self._fail("playback_unavailable", "Check switch playback before editing effects.", 409)
            try:
                switch = self._playback_switch(record["device"])
                try:
                    await self._open_player(switch, record["device"])
                    return await self._read_or_edit_effects(switch, record, change)
                finally:
                    await switch.close()
            except (OSError, TransportError, ValueError, KeyError, CredentialStoreError) as exc:
                raise self._fail("playback_edit_failed", "Could not confirm the switch scene. "
                                 "Reopen the editor to check its current settings.", 409) from exc

    async def _read_or_edit_effects(self, switch, record, change):
        transport, owner = switch.transport, record["owner"]
        if await playback.status(transport, owner) != "playing":
            raise ValueError("player is not running")
        current = await playback.edit_revision(transport, owner)
        store = self._device_store(switch)
        pending = record.get("pending_edit")
        if pending:
            self._reconcile_effect(store, pending, current)
            record = {k: v for k, v in record.items() if k != "pending_edit"}
            record["applied_edit"] = current
            self._playback.save(record)
        if current != record.get("applied_edit", "original"):
            raise ValueError("switch scene changed")
        snapshot = store.load()
        if not snapshot:
            raise ValueError("missing scene")
        revision = hashlib.sha256((owner + current).encode("ascii")).hexdigest()
        if change is None:
            return {"revision": revision, "zones": snapshot["zones"]}
        if change.expected_revision != revision:
            raise self._fail("scene_changed", "The effect changed. Reopen the editor before applying.", 409)
        # Construct a model for validation and compilation, without starting its output loop.
        engine = Engine(switch, gamma=self.config.engine.gamma, max_commands=self.config.engine.max_commands)
        engine.apply_snapshot(snapshot)
        engine.set_effect(change.id, change.params, change.zone, clear_overrides=False)
        config, palette = playback.compile_scene(engine)
        content = f"{len(palette.splitlines())}\n{config}{palette}"
        digest = hashlib.sha256(content.encode("ascii")).hexdigest()
        updated = engine.snapshot()
        self._playback.save({**record, "pending_edit": {"from": current, "to": digest, "scene": updated}})
        await playback.edit_scene(transport, owner, content, current)
        if await playback.edit_revision(transport, owner) != digest:
            raise ValueError("update unconfirmed")
        store.save(updated)
        self._playback.save({**record, "applied_edit": digest})
        return {"revision": hashlib.sha256((owner + digest).encode("ascii")).hexdigest(), "zones": updated["zones"]}

    @staticmethod
    def _reconcile_effect(store, pending, current):
        if current == pending["to"]:
            store.save(pending["scene"])
        elif current != pending["from"]:
            raise ValueError("unknown scene revision")

    async def _stop_player(self):
        record = self._playback.data
        if record.get("state") == "unreadable":
            raise ValueError("The switch playback recovery record could not be read.")
        self._playback.save({**record, "state": "handoff"})
        self._playback_status = "handoff"
        switch = self._playback_switch(record["device"])
        try:
            await self._open_player(switch, record["device"])
            if record.get("pending_edit") and not await playback.available(switch.transport):
                current = await playback.edit_revision(switch.transport, record["owner"])
                self._reconcile_effect(self._device_store(switch), record["pending_edit"], current)
            staged = await playback.drain(switch.transport, record["owner"])
            t = switch.transport
            paths = self.output_journal.paths(t.host, t.port, t.host_key_fingerprint)
            if staged or any(p.exists() for p in paths):
                # A crash during upload can leave the original desktop lease in place.
                try:
                    owners = {json.loads(p.read_text("utf-8"))["owner"] for p in paths if p.exists()}
                except (KeyError, TypeError, ValueError) as exc:
                    raise OutputOwnershipError("The saved lighting ownership could not be verified.") from exc
                if owners and owners == {record.get("desktop_owner")}:
                    await self._recover_retained_output(switch, t.host, t.port, t.host_key_fingerprint)
                self._reserve_player_output(switch, record["owner"])
                await switch.restore_defaults()
                if staged:
                    await playback.remove(t, record["owner"])
                switch.output_lease.finish()
        finally:
            await switch.close()

    def _retry_playback_handoff(self):
        if not self._closing and (self._playback_retry is None or self._playback_retry.done()):
            self._playback_retry = asyncio.create_task(self._retry_until_released())

    async def _retry_until_released(self):
        while self._playback.data and self._playback.data.get("state") == "handoff":
            await asyncio.sleep(30)
            with contextlib.suppress(LoginError):
                await self.handoff()

    async def handoff(self) -> dict:
        """Release EE output and close its owned connections without forgetting saved data."""
        task = self._handoff_task
        if task is None or task.done():
            self._handoff_generation += 1
            self._handoff.state = "pending"
            self._notify()
            task = asyncio.create_task(self._perform_handoff(), name="ee-handoff")
            self._handoff_task = task
        return await asyncio.shield(task)

    async def _perform_handoff(self) -> dict:
        save_error = None
        try:
            self._handoff.save("pending")
        except OSError as exc:
            save_error = exc
        restoring = self._saved_restore_task
        if restoring is not None and not restoring.done():
            restoring.cancel()
            with contextlib.suppress(asyncio.CancelledError, Exception):
                await restoring
        cleanup_error = None
        async with self._lock:
            try:
                await self._clear_pending_locked()
            except Exception as exc:
                cleanup_error = exc
            try:
                await self._disconnect_locked()
            except Exception as exc:
                cleanup_error = cleanup_error or exc
            for path, (store, scene) in list(self._unsaved_scenes.items()):
                try:
                    store.save(scene)
                    del self._unsaved_scenes[path]
                except OSError as exc:
                    save_error = save_error or exc
            retained = {engine.switch for engine in self._cleanup_engines}
            if self._pending is not None:
                retained.add(self._pending.engine.switch)
            for switch in list(self._owned_switches - retained):
                try:
                    await self._close_switch(switch)
                except Exception as exc:
                    cleanup_error = cleanup_error or exc
            if self._playback.data and not cleanup_error:
                try:
                    await self._stop_player()
                except (OSError, TransportError, ValueError, CredentialStoreError) as exc:
                    cleanup_error = exc
                    self._retry_playback_handoff()
            if self._historic_handoff_unknown:
                cleanup_error = cleanup_error or TransportError("The previous process did not confirm cleanup")
            if self._unsaved_scenes:
                save_error = save_error or OSError("The current lighting scene could not be saved")
            state = "unconfirmed" if cleanup_error or save_error else "complete"
            self._handoff.state = state
            try:
                self._handoff.save(state)
                if state == "complete" and self._playback.data:
                    self._playback.clear()
                    self._playback_status = None
            except OSError as exc:
                save_error = save_error or exc
                self._handoff.state = "unconfirmed"
            if save_error is not None:
                raise self._fail("handoff_save_failed", "The handoff preference could not be saved. "
                                 "Check the data folder and retry handoff before closing EE.", 409) from save_error
            if cleanup_error is not None:
                if self._historic_handoff_unknown:
                    raise self._fail("handoff_recovery_required", "The previous handoff has no confirmed "
                                     "shutdown. Keep EE disconnected until its recovery review is complete.", 409)
                raise self._fail("handoff_unconfirmed", "EE could not confirm all lighting and SSH activity "
                                 "stopped. Retry handoff; automatic reconnect remains paused.", 409) from cleanup_error
            self.last_error = None
            self._notify()
            return self.info()

    async def forget_saved_login(self) -> dict:
        """Remove the saved login while keeping the current controller session connected."""
        async with self._lock:
            if self._playback.data:
                raise self._fail("switch_playback_reserved", "Finish switch playback before removing its login.", 409)
            try:
                self.saved.clear()
            except CredentialStoreError as exc:
                raise self._fail("saved_login_forget_failed", str(exc), 500) from exc
            if self.last_error and self.last_error.get("code", "").startswith("saved_login_"):
                self.last_error = None
            self._notify()
            return self.info()

    async def _disconnect_locked(self) -> None:
        engine, self.engine = self.engine, None
        if engine is not None:
            self._save_scene(engine, self.simulated, retain_failure=True)
            self._cleanup_engines.append(engine)
            for mailbox in self._listeners:
                engine.unsubscribe(mailbox)
        if self._autosave_task is not None:
            self._autosave_task.cancel()
            with contextlib.suppress(asyncio.CancelledError):
                await self._autosave_task
            self._autosave_task = None
        self.host, self.username, self.simulated = None, None, False
        self.port = 22
        self._scene_store = None
        for cleanup in list(self._cleanup_engines):
            stopping = asyncio.create_task(cleanup.stop())
            try:
                await asyncio.shield(stopping)
            except asyncio.CancelledError:
                with contextlib.suppress(Exception):
                    await stopping
                raise
            self._cleanup_engines.remove(cleanup)
            self._owned_switches.discard(cleanup.switch)

    # The live scene

    def _load_scene(self, store: SceneStore) -> dict | None:
        if self.engine is not None and self._scene_store is not None and self._scene_store.path == store.path:
            return self.engine.snapshot()
        pending = self._unsaved_scenes.get(store.path)
        if pending is not None:
            previous_store, snapshot = pending
            try:
                previous_store.save(snapshot)
            except OSError as exc:
                raise self._fail("scene_save_failed", "The previous lighting setup could not be saved. "
                                 "Check the data folder and reconnect to retry.", 500) from exc
            del self._unsaved_scenes[store.path]
        return store.load()

    def _save_scene(self, engine: Engine, simulated: bool, *, retain_failure: bool = False) -> None:
        store = self._scene_store or (self.scenes[True] if simulated else self._device_store(engine.switch))
        if engine.revision == self._saved_revision and store.path not in self._unsaved_scenes:
            return
        snapshot = engine.snapshot()
        try:
            store.save(snapshot)
            self._unsaved_scenes.pop(store.path, None)
            self._saved_revision = engine.revision
        except OSError as exc:
            if retain_failure:
                self._unsaved_scenes[store.path] = (store, snapshot)
            log.warning("could not save the scene: %s", exc)

    async def _autosave(self, engine: Engine, simulated: bool, interval: float = 2.0) -> None:
        while True:
            await asyncio.sleep(interval)
            self._save_scene(engine, simulated)

    async def auto_connect(self) -> None:
        """At start-up: use the [switch] config / EE_* env vars, else the remembered login, if any."""
        if self._playback.data:
            if self._playback.data.get("state") == "handoff" or self.handoff_pending:
                with contextlib.suppress(LoginError):
                    await self.handoff()
            else:
                await self.check_playback()
            return
        if self._handoff.paused:
            self._notify()
            return
        self._auto_connecting = True
        self._notify()
        try:
            await self._auto_connect()
        finally:
            self._auto_connecting = False
            self._notify()

    async def _auto_connect(self) -> None:
        if self._handoff.paused:
            return
        sw = self.config.switch
        if sw.host:
            target = {"host": sw.host, "port": sw.port, "username": sw.username, "password": sw.password,
                      "key_file": sw.key_file}
        else:
            try:
                saved = self.saved.load()
            except CredentialStoreError as exc:
                self._fail("saved_login_unavailable", str(exc), 409)
                log.warning("automatic login stopped because the saved login could not be unlocked")
                return
            if not saved:
                return
            target = {"host": saved["host"], "port": int(saved.get("port", 22)), "username": saved.get("username", ""),
                      "password": saved.get("password", ""), "key_file": ""}
        try:
            await self.connect(target["host"], target["username"], target["password"], port=target["port"],
                               key_file=target["key_file"], remember=None, automatic=True)
        except LoginError as exc:
            log.warning("automatic login to %s failed: %s", target["host"], exc.message)

    async def check_playback(self):
        async with self._lock:
            record = self._playback.data
            if record and record.get("state") not in {"handoff", "unreadable"}:
                try:
                    switch = self._playback_switch(record["device"])
                    try:
                        await self._open_player(switch, record["device"])
                        self._playback_status = await playback.status(switch.transport, record["owner"])
                    finally:
                        await switch.close()
                except (OSError, TransportError, ValueError, CredentialStoreError):
                    self._playback_status = "unreachable"
            self._notify()
            return self.info()

    def forget_host_key(self, host: str, port: int = 22) -> bool:
        if self._playback.data:
            return False
        return self.host_keys.forget(host, port)

    async def close(self) -> None:
        self._closing = True
        if self._playback_retry:
            self._playback_retry.cancel()
            with contextlib.suppress(asyncio.CancelledError):
                await self._playback_retry
        handoff = self._handoff_task
        if handoff is not None and not handoff.done():
            with contextlib.suppress(LoginError):
                await asyncio.shield(handoff)
        task = self._saved_restore_task
        if task is not None:
            task.cancel()
            with contextlib.suppress(asyncio.CancelledError, Exception):
                await task
            self._saved_restore_task = None
        await self.disconnect()
