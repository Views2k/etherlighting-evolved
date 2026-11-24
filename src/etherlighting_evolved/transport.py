"""How commands reach the switch.

`SSHTransport` keeps one SSH connection open with a long-lived `sh` per channel, so each LED update is a write
to an open shell instead of a new SSH login. `SimulatedTransport` emulates a USW-Pro-XG-8-PoE in memory, for UI
development, demos and tests.
"""

from __future__ import annotations

import asyncio
import contextlib
import json
import logging
import random
import re
import secrets
import time
from abc import ABC, abstractmethod

from .storage import NewerFormatError, atomic_write_json, check_version

log = logging.getLogger(__name__)

SSH_CLOSE_TIMEOUT = 3.0
HOST_KEY_FORMAT_VERSION = 1
READ_PORTS = "swctrl port show"
READ_POE = "swctrl poe show 2>/dev/null"
READ_CPU = 'read -r a < /proc/stat; echo "$a"; read -r b < /proc/loadavg; echo "$b"'
# Status polls refused in a row because an earlier status shell has not exited. At the limit the connection is
# dropped, so the engine reconnects and shows the error instead of driving lighting from frozen status values.
UNFINISHED_PORT_READ_LIMIT = 3
MAX_RESPONSE = 262144


class TransportError(RuntimeError):
    """The switch could not be reached or stopped answering."""


class PortSampleUnavailable(TransportError):
    """A switch status sample was missed while the SSH connection remained open."""


class NativeOutputUncertain(TransportError):
    """A native output transition needs a fresh connection before output can resume."""


class AuthenticationError(TransportError):
    """The switch rejected the SSH username or password / key."""


class UnreachableError(TransportError):
    """Nothing answered SSH at that address and port."""


class HostKeyMismatchError(TransportError):
    """The switch presented an SSH host key that differs from the saved key (a reset, a replacement or another device).

    `presented` and `presented_type` describe the key the switch presented, `pinned` the saved key. Either
    fingerprint is None when it is not known.
    """

    def __init__(self, message: str = "The switch's SSH host key differs from the saved key.", *,
                 presented: str | None = None, presented_type: str | None = None, pinned: str | None = None) -> None:
        super().__init__(message)
        self.presented = presented
        self.presented_type = presented_type
        self.pinned = pinned


class HostKeyStoreError(TransportError):
    """The saved SSH host keys could not be read or saved, or a newer EE saved them. The message names no host."""


KEY_TYPE = re.compile(r"[A-Za-z0-9@._+-]{1,64}")
# A fingerprint as `ssh-keygen -l` prints it. The API and eectl both take one back to accept a changed key, so both
# use this pattern; it is anchored for Pydantic, which searches rather than matches.
SSH_FINGERPRINT = r"^SHA256:[A-Za-z0-9+/]{43}$"
# Addresses whose changed key is kept for review. A client that probes many addresses cannot grow it further.
PRESENTED_LIMIT = 32
UNREADABLE_HOST_KEYS = "The saved SSH host keys in known_switches.json could not be read. Check the EE data folder."


def _key_type(key) -> str | None:
    """The key's algorithm name for display, or None when it is unavailable or not a plain name."""
    try:
        algorithm = key.get_algorithm()
    except Exception:
        # The type is shown next to the fingerprint only. Approval and comparison use the fingerprint alone.
        return None
    return algorithm if isinstance(algorithm, str) and KEY_TYPE.fullmatch(algorithm) else None


class HostKeyStore:
    """The saved SSH host key fingerprint of each switch address, kept in a small JSON file.

    The first key an address presents is saved after the login succeeds (trust on first use). A key that differs from
    the saved one ends the connection during the key exchange, before any username or password is sent, and is kept
    in memory. The user can then accept it with `replace`, and only a key that EE itself saw at that address can be
    accepted.
    """

    def __init__(self, path) -> None:
        from pathlib import Path

        self.path = Path(path)
        self._presented: dict[str, str] = {}

    def _load(self) -> dict[str, str]:
        try:
            data = json.loads(self.path.read_text("utf-8"))
        except FileNotFoundError:
            return {}
        except (OSError, json.JSONDecodeError) as exc:
            raise HostKeyStoreError(UNREADABLE_HOST_KEYS) from exc
        if not isinstance(data, dict) or any(not isinstance(k, str) or not isinstance(v, str)
                                             for k, v in data.items()):
            raise HostKeyStoreError(UNREADABLE_HOST_KEYS)
        # Releases up to 0.5.1 saved no version, which is format version 1. The version is a string, beside the
        # "host:port" keys, because those releases accept only string values in this file.
        version = data.pop("version", str(HOST_KEY_FORMAT_VERSION))
        try:
            check_version(int(version) if version.isdecimal() else None, HOST_KEY_FORMAT_VERSION, "SSH host keys")
        except (NewerFormatError, ValueError) as exc:
            raise HostKeyStoreError(str(exc)) from exc
        return data

    def _save(self, data: dict[str, str]) -> None:
        atomic_write_json(self.path, {**data, "version": str(HOST_KEY_FORMAT_VERSION)})

    @staticmethod
    def address_key(host: str, port: int) -> str:
        return f"{host.strip().lower()}:{port}"

    def get(self, host: str, port: int) -> str | None:
        return self._load().get(self.address_key(host, port))

    def accept(self, host: str, port: int, fingerprint: str) -> bool:
        """Save the first key of an address, returning whether it was written. A different saved key is never
        overwritten here."""
        data = self._load()
        key = self.address_key(host, port)
        pinned = data.get(key)
        if pinned == fingerprint:
            return False
        if pinned is not None:
            raise HostKeyMismatchError(f"{key} already has a different saved SSH key.", presented=fingerprint,
                                       pinned=pinned)
        data[key] = fingerprint
        self._save(data)
        return True

    def replace(self, host: str, port: int, previous: str, fingerprint: str) -> None:
        """Replace the saved key only while it is still `previous`."""
        data = self._load()
        key = self.address_key(host, port)
        pinned = data.get(key)
        if pinned == fingerprint:
            return
        if pinned != previous:
            raise HostKeyMismatchError(f"The saved SSH key of {key} is no longer the key to replace.",
                                       presented=fingerprint, pinned=pinned)
        data[key] = fingerprint
        self._save(data)

    def forget(self, host: str, port: int = 22) -> bool:
        data = self._load()
        removed = data.pop(self.address_key(host, port), None) is not None
        if removed:
            self._save(data)
        return removed

    def note_presented(self, host: str, port: int, fingerprint: str) -> None:
        key = self.address_key(host, port)
        self._presented.pop(key, None)
        self._presented[key] = fingerprint
        while len(self._presented) > PRESENTED_LIMIT:
            del self._presented[next(iter(self._presented))]

    def presented(self, host: str, port: int) -> str | None:
        """The changed key the switch at this address presented last, while it waits for review."""
        return self._presented.get(self.address_key(host, port))

    def clear_presented(self, host: str, port: int) -> None:
        self._presented.pop(self.address_key(host, port), None)


class Transport(ABC):
    # Set on SSH connections: the authenticated host key and its algorithm, and the lease that lets desktop output
    # override the switch-resident player while that player is installed.
    host_key_fingerprint: str | None = None
    host_key_type: str | None = None
    desktop_output = None
    # The set method's arguments and blobmsg types as the lighting socket reported them, when one is open.
    lighting_set_fields: dict[str, int] | None = None

    def revoke_lighting_profile(self) -> None:
        return None

    def configure_lighting_profile(self, profile) -> None:
        profile.require_output()

    @property
    def direct_rgb(self) -> bool:
        return False

    async def prepare_rgb(self) -> bool:
        """Negotiate an optional color channel after the switch schema is verified."""
        return False

    async def write_port_rgb(self, port: int, rgb: str, level: int) -> None:
        raise TransportError("direct lighting is unavailable")

    @property
    def precise_rgb_supported(self) -> bool:
        return False

    @property
    def lighting_generation(self) -> object | None:
        return None

    async def write_precise_rgb(self, port: int, channels: tuple[int, int, int]) -> None:
        raise TransportError("precise lighting is unavailable")

    async def write_sfp_precise(self, port: int, channels: tuple[int, int, int]) -> None:
        raise TransportError("precise SFP lighting is unavailable")

    @property
    def behavior_supported(self) -> bool:
        return False

    @property
    def sfp_mode_supported(self) -> bool:
        return False

    async def write_behavior(self, behavior: str) -> None:
        raise TransportError("native behavior is unavailable")

    async def write_sfp_mode(self, port: int, direct: bool) -> None:
        raise TransportError("scoped SFP mode is unavailable")

    @property
    @abstractmethod
    def connected(self) -> bool: ...

    @abstractmethod
    async def connect(self) -> None: ...

    @abstractmethod
    async def close(self) -> None: ...

    @abstractmethod
    async def run(self, command: str, *, channel: str = "main", timeout: float = 10.0) -> str:
        """Run shell command(s) on the switch and return their combined output."""

    async def read_health(self) -> str:
        from .health import HEALTH_COMMAND, HEALTH_TIMEOUT
        return await self.run(HEALTH_COMMAND, channel="health", timeout=HEALTH_TIMEOUT)


# SSH

class _Channel:
    def __init__(self, process) -> None:
        self.process = process
        self.lock = asyncio.Lock()
        self.sample_failed = False


def _process_exited(process) -> bool:
    status = process.exit_status
    return isinstance(status, int) and status >= 0 or process.exit_signal is not None


class SSHTransport(Transport):
    """Persistent SSH session(s) to the switch.

    Each named channel is its own `sh` process on one SSH connection, so LED writes ("leds") and link polling
    ("links") never wait for each other. Output of a command is collected up to a unique end marker.
    """

    def __init__(
        self,
        host: str,
        *,
        port: int = 22,
        username: str | None = None,
        password: str | None = None,
        key_file: str | None = None,
        host_keys: HostKeyStore | None = None,
        connect_timeout: float = 15.0,
    ) -> None:
        self.host = host
        self.port = port
        self.username = username or None
        self.password = password or None
        self.key_file = key_file or None
        self.host_keys = host_keys
        self.connect_timeout = connect_timeout
        self._conn = None
        self._channels: dict[str, _Channel] = {}
        self._prior_port_reader: _Channel | None = None
        self._unfinished_port_reads = 0
        self._channel_lock = asyncio.Lock()
        self._rgb_lock = asyncio.Lock()
        self._health_lock = asyncio.Lock()
        self._isolated_locks: dict[str, asyncio.Lock] = {}
        self._rgb_client = None
        self._rgb_checked = False
        self._rgb_failed = False
        self._seq = 0
        self._close_task: asyncio.Task | None = None
        self._cleanup = None
        self._retired_processes: list[tuple[object, object]] = []
        self._opening: dict[int, int] = {}
        self._opening_done: dict[int, asyncio.Event] = {}
        self._connecting = False
        self._connect_done = asyncio.Event()
        self._connect_done.set()
        self._late_connections: list[object] = []
        self._process_tags: dict[int, str] = {}

    def _opening_started(self, connection) -> None:
        key = id(connection)
        self._opening[key] = self._opening.get(key, 0) + 1
        self._opening_done.setdefault(key, asyncio.Event()).clear()

    def _opening_finished(self, connection) -> None:
        key = id(connection)
        self._opening[key] -= 1
        if not self._opening[key]:
            self._opening_done[key].set()

    def _retire_process(self, connection, process) -> None:
        self._retired_processes.append((connection, process))
        with contextlib.suppress(Exception):
            process.stdin.write_eof()

    @property
    def connected(self) -> bool:
        return self._conn is not None

    @property
    def direct_rgb(self) -> bool:
        # A failed direct write cannot silently fall back and replay through the shell.
        return self._rgb_client is not None or self._rgb_failed

    async def prepare_rgb(self) -> bool:
        from .ubus import EtherlightUbus

        async with self._rgb_lock:
            if self._rgb_client is not None:
                return True
            if self._rgb_checked and not self._rgb_failed:
                return False
            conn = self._conn
            if conn is None:
                raise TransportError("not connected")
            try:
                client = await EtherlightUbus.open(conn)
            except asyncio.CancelledError:
                raise
            except Exception as exc:
                if self._conn is not conn:
                    raise TransportError("the SSH session closed while opening lighting") from exc
                self._rgb_checked = True
                if self._rgb_failed:
                    raise TransportError("the lighting socket could not be restored") from exc
                return False
            if self._conn is not conn:
                await client.close()
                raise TransportError("the SSH session closed while opening lighting")
            self._rgb_client = client
            self._rgb_checked, self._rgb_failed = True, False
            return True

    @property
    def precise_rgb_supported(self) -> bool:
        return bool(self._rgb_client is not None and self._rgb_client.precise_rgb_supported)

    @property
    def lighting_set_fields(self) -> dict[str, int] | None:
        return self._rgb_client.set_fields if self._rgb_client is not None else None

    def configure_lighting_profile(self, profile) -> None:
        profile.require_output()
        if self._rgb_client is None:
            raise TransportError("The lighting connection changed before profile binding")
        self._rgb_client.bind_profile(profile)

    def revoke_lighting_profile(self) -> None:
        if self._rgb_client is not None:
            self._rgb_client.revoke_profile()

    @property
    def lighting_generation(self) -> object | None:
        return self._rgb_client

    @property
    def behavior_supported(self) -> bool:
        return bool(self._rgb_client is not None and self._rgb_client.behavior_supported)

    @property
    def sfp_mode_supported(self) -> bool:
        return bool(self._rgb_client is not None and self._rgb_client.sfp_mode_supported)

    async def write_port_rgb(self, port: int, rgb: str, level: int) -> None:
        await self._write_color(port, rgb=rgb, level=level)

    async def write_precise_rgb(self, port: int, channels: tuple[int, int, int]) -> None:
        await self._write_color(port, channels=channels)

    async def write_sfp_precise(self, port: int, channels: tuple[int, int, int]) -> None:
        await self._write_color(port, sfp_channels=channels)

    async def write_behavior(self, behavior: str) -> None:
        await self._write_color(0, behavior=behavior)

    async def write_sfp_mode(self, port: int, direct: bool) -> None:
        await self._write_color(port, sfp_direct=direct)

    async def _write_color(self, port: int, *, rgb: str | None = None, level: int = 100,
                           channels: tuple[int, int, int] | None = None, behavior: str | None = None,
                           sfp_direct: bool | None = None,
                           sfp_channels: tuple[int, int, int] | None = None) -> None:
        client = self._rgb_client
        if client is None:
            raise TransportError("the lighting socket needs to reconnect")
        try:
            if self.desktop_output is not None:
                await self.desktop_output.renew()
                if self._rgb_client is not client:
                    raise TransportError("The switch connection changed before lighting was applied.")
            if behavior is not None:
                await client.set_behavior(behavior)
            elif sfp_direct is not None:
                await client.set_sfp_mode(port, sfp_direct)
            elif sfp_channels is not None:
                await client.set_sfp_precise_port(port, sfp_channels)
            elif channels is None:
                await client.set_port(port, rgb, level)
            else:
                await client.set_precise_port(port, channels)
        except BaseException as exc:
            if self._rgb_client is client:
                self._rgb_client = None
                self._rgb_failed = True
            with contextlib.suppress(Exception):
                await client.close()
            if isinstance(exc, (asyncio.CancelledError, TransportError, ValueError)):
                raise
            if not isinstance(exc, Exception):
                raise
            raise TransportError("the lighting socket stopped answering") from exc

    async def connect(self) -> None:
        if (self._cleanup is not None or self._late_connections
                or self._close_task is not None and not self._close_task.done()):
            raise TransportError("the previous SSH connection has not confirmed cleanup")
        if self._connecting:
            raise TransportError("an SSH connection is already opening")
        self._connecting = True
        self._connect_done.clear()
        try:
            await self._connect()
        finally:
            self._connecting = False
            self._connect_done.set()

    async def _connect(self) -> None:
        try:
            import asyncssh
        except ImportError as exc:  # pragma: no cover
            raise TransportError("asyncssh is not installed: pip install asyncssh") from exc
        transport = self
        store = self.host_keys
        # Read before any network I/O, so an unreadable store ends the attempt before the switch is contacted.
        pinned = store.get(self.host, self.port) if store is not None else None
        rejection: TransportError | None = None

        class PinnedHostClient(asyncssh.SSHClient):
            def validate_host_public_key(self, host, addr, port, key):
                nonlocal rejection
                fingerprint = key.get_fingerprint("sha256")
                if store is None:
                    # Without a store the key could be neither checked nor saved.
                    rejection = TransportError("EE has no place to save SSH host keys, so it did not connect.")
                    return False
                if pinned is None or pinned == fingerprint:
                    store.clear_presented(transport.host, transport.port)
                    return True
                rejection = transport._changed_key(fingerprint, _key_type(key), pinned)
                return False

        # An empty trusted-key set makes asyncssh call the validation callback during the key exchange, before the
        # userauth request that carries the username and password. An address without a saved key passes it and
        # its key is saved after the login; any other key must be the saved one. GSS key exchange is off because
        # it would authenticate the server without this callback.
        kwargs: dict = {"host": self.host, "port": self.port, "known_hosts": ([], [], []),
                        "client_factory": PinnedHostClient, "config": None, "gss_kex": False, "gss_auth": False}
        if self.username:
            kwargs["username"] = self.username
        if self.password:
            kwargs["password"] = self.password
        if self.key_file:
            kwargs["client_keys"] = [self.key_file]
        else:
            # Password login only: don't offer local or agent keys first (each counts as a failed attempt).
            kwargs["client_keys"] = None
            kwargs["agent_path"] = None
        try:
            conn = await asyncio.wait_for(asyncssh.connect(**kwargs), self.connect_timeout)
        except asyncssh.HostKeyNotVerifiable as exc:
            if rejection is not None:
                raise rejection from exc
            # asyncssh refused the key without asking EE, for example a host certificate.
            message = "The switch's SSH host key could not be verified."
            if pinned is None:
                raise TransportError(message) from exc
            raise HostKeyMismatchError(message, pinned=pinned) from exc
        except asyncssh.PermissionDenied as exc:
            raise AuthenticationError("SSH login failed: wrong username or password") from exc
        except (TimeoutError, OSError) as exc:
            reason = "timed out" if isinstance(exc, asyncio.TimeoutError) else (exc.strerror or str(exc))
            raise UnreachableError(f"Can't reach {self.host}:{self.port} over SSH ({reason})") from exc
        except Exception as exc:
            raise TransportError(f"SSH connection to {self.host} failed: {exc}") from exc
        if self._cleanup is not None:
            self._late_connections.append(conn)
            raise TransportError("the SSH session closed while connecting")
        try:
            key = conn.get_server_host_key()
            if key is None or store is None:
                raise TransportError("The switch's SSH host key could not be read after the login.")
            fingerprint, key_type = key.get_fingerprint("sha256"), _key_type(key)
            # Saves the key of an address that has none. The saved key can also change during the handshake (another
            # EE window, eectl): a different key saved meanwhile is kept, and this connection is refused.
            try:
                saved_now = store.accept(self.host, self.port, fingerprint)
            except HostKeyMismatchError as exc:
                raise self._changed_key(fingerprint, key_type, exc.pinned, f"The saved SSH key for {self.host}:"
                                        f"{self.port} changed while EE was connecting. Connect again.") from exc
            except OSError as exc:
                raise HostKeyStoreError("The switch's SSH key could not be saved in known_switches.json. Check the EE "
                                        "data folder and try again.") from exc
            if saved_now:
                log.info("saved the %s SSH key %s of %s:%s at the first connection", key_type or "unknown",
                         fingerprint, self.host, self.port)
            self.host_key_fingerprint, self.host_key_type = fingerprint, key_type
        except Exception:
            conn.close()
            try:
                await asyncio.wait_for(conn.wait_closed(), SSH_CLOSE_TIMEOUT)
            except asyncio.CancelledError:
                self._late_connections.append(conn)
                raise
            except Exception:
                self._late_connections.append(conn)
            raise
        self._conn = conn
        self._unfinished_port_reads = 0
        self._rgb_checked = self._rgb_failed = False

    def _changed_key(self, presented: str, presented_type: str | None, pinned: str | None,
                     message: str | None = None) -> HostKeyMismatchError:
        """The error for a key that is not the saved one. The presented key is kept so the user can accept it."""
        if self.host_keys is not None:
            self.host_keys.note_presented(self.host, self.port, presented)
        return HostKeyMismatchError(
            message or f"The SSH key of {self.host}:{self.port} changed. Saved key: {pinned}. Presented key: "
            f"{presented}. If you did not reset or replace the switch, another device may be using this address.",
            presented=presented, presented_type=presented_type, pinned=pinned)

    async def _open_shell(self, conn, name: str) -> _Channel:
        """Start the `sh` for a channel. Its tag lets helper recovery find and stop exactly this shell."""
        import asyncssh

        self._opening_started(conn)
        try:
            tag = 'ee_' + secrets.token_hex(16)
            process = await conn.create_process('sh -s ' + tag, stderr=asyncssh.STDOUT)
            self._process_tags[id(process)] = tag
            if self._conn is not conn:
                self._retire_process(conn, process)
                raise TransportError("the SSH session closed while opening a channel")
            channel = self._channels[name] = _Channel(process)
            return channel
        finally:
            self._opening_finished(conn)

    async def _exchange(self, channel: _Channel, command: str, *, prefix: str = "__EE_DONE_",
                        limit: int = MAX_RESPONSE, own_line: bool = False) -> str:
        """Send a command and a unique end marker, and return the output written before the marker."""
        self._seq += 1
        marker = f"{prefix}{self._seq}__"
        # With own_line the marker starts on its own line even when the output does not end with one.
        end = f"printf '\\n%s\\n' {marker}" if own_line else f"echo {marker}"
        channel.process.stdin.write(f"{command}\n{end}\n")
        lines, size = [], 0
        while True:
            line = await channel.process.stdout.readline()
            if line == "":
                raise TransportError("the switch closed the shell")
            if line.strip() == marker:
                output = "".join(lines)
                return output[:-1] if own_line and output.endswith("\n") else output
            size += len(line)
            if size > limit:
                raise TransportError("the switch returned an oversized response")
            lines.append(line)

    async def _channel(self, name: str, *, connection=None) -> _Channel:
        async with self._channel_lock:
            conn = self._conn if connection is None else connection
            if conn is None or self._conn is not conn:
                raise TransportError("the SSH session closed before opening a channel")
            return self._channels.get(name) or await self._open_shell(conn, name)

    async def _port_channel(self, conn) -> _Channel:
        previous = self._channels.get("links")
        if previous is not None and previous.sample_failed:
            async with previous.lock:
                if self._conn is not conn or conn.is_closed():
                    raise TransportError("the SSH connection closed during switch status sampling")
                if self._channels.get("links") is previous:
                    # One spare reader lets sampling recover when firmware omits the first shell's exit status.
                    finished = previous if _process_exited(previous.process) else self._prior_port_reader
                    if finished is not None:
                        if not _process_exited(finished.process):
                            self._unfinished_port_reads += 1
                            if self._unfinished_port_reads >= UNFINISHED_PORT_READ_LIMIT:
                                raise TransportError("the switch status shell did not finish")
                            raise PortSampleUnavailable("the previous switch status read is finishing")
                        await finished.process.wait_closed()
                        if self._conn is not conn:
                            raise TransportError("the SSH connection changed during switch status sampling")
                        self._process_tags.pop(id(finished.process), None)
                    if finished is not previous:
                        self._prior_port_reader = previous
                    self._channels.pop("links")
        return await self._channel("links", connection=conn)

    async def _command_failed(self, conn, ch, port_sample: bool, exc: BaseException) -> None:
        if (port_sample and self._conn is conn and not conn.is_closed()
                and isinstance(exc, (TimeoutError, TransportError, BrokenPipeError, asyncio.CancelledError))):
            if ch is not None and self._channels.get("links") is ch and not ch.sample_failed:
                ch.sample_failed = True
                with contextlib.suppress(Exception):
                    ch.process.stdin.write_eof()
            if isinstance(exc, asyncio.CancelledError):
                return
            raise PortSampleUnavailable("switch status could not be read") from exc
        await self._close_failed_connection(conn)

    async def run(self, command: str, *, channel: str = "main", timeout: float = 10.0) -> str:
        conn = self._conn
        if conn is None:
            raise TransportError("not connected")
        port_sample = channel == "links" and command in (READ_PORTS, READ_POE, READ_CPU)
        opening = self._port_channel(conn) if port_sample else self._channel(channel, connection=conn)
        try:
            ch = await asyncio.wait_for(opening, timeout)
        except PortSampleUnavailable:
            raise
        except Exception as exc:
            # A shell open that fails or is cancelled part way can leave the shell running on the switch with no
            # handle to end it. Closing the connection is the only way the switch reaps it.
            await self._close_failed_connection(conn)
            raise TransportError(f"could not open a shell on the switch: {exc}") from exc
        async with ch.lock:
            try:
                async with asyncio.timeout(timeout):
                    if self._conn is not conn:
                        raise TransportError("the SSH session closed before sending a command")
                    if ch.sample_failed:
                        raise TransportError("the switch status shell is finishing")
                    output = await self._exchange(ch, command)
                if port_sample:
                    self._unfinished_port_reads = 0
                return output
            except (asyncio.CancelledError, TransportError) as exc:
                await self._command_failed(conn, ch, port_sample, exc)
                raise
            except TimeoutError as exc:
                await self._command_failed(conn, ch, port_sample, exc)
                raise TransportError(f"the switch did not answer within {timeout:g}s") from exc
            except Exception as exc:
                await self._command_failed(conn, ch, port_sample, exc)
                raise TransportError(f"SSH error: {exc}") from exc

    async def _close_failed_connection(self, connection) -> None:
        if self._conn is connection or self._cleanup is not None and self._cleanup["connection"] is connection:
            await self.close()
        else:
            connection.close()
            with contextlib.suppress(Exception):
                await asyncio.wait_for(connection.wait_closed(), 3)

    async def run_isolated(self, command: str, *, channel: str, timeout: float = 10.0) -> str:
        """Run a command on its own channel. A failure or timeout closes that channel's shell, not LED output."""
        conn = self._conn
        if conn is None:
            raise TransportError("not connected")
        lock = self._isolated_locks.setdefault(channel, asyncio.Lock())
        shell = None
        async with lock:
            try:
                async with asyncio.timeout(timeout):
                    if self._conn is not conn:
                        raise TransportError("the SSH session changed before the command")
                    shell = self._channels.get(channel) or await self._open_shell(conn, channel)
                    return await self._exchange(shell, command, own_line=True)
            except (Exception, asyncio.CancelledError) as exc:
                if shell is not None:
                    if self._channels.get(channel) is shell:
                        self._channels.pop(channel)
                    self._retire_process(conn, shell.process)
                if isinstance(exc, asyncio.CancelledError | TransportError):
                    raise
                if isinstance(exc, TimeoutError):
                    raise TransportError(f"the switch did not answer within {timeout:g}s") from exc
                raise TransportError(f"SSH error: {exc}") from exc

    async def read_health(self) -> str:
        """Run only the fixed health probe; a failed probe closes its shell, not LED output."""
        from .health import HEALTH_COMMAND, HEALTH_TIMEOUT, MAX_HEALTH_RESPONSE
        conn = self._conn
        if conn is None:
            raise TransportError("health is unavailable while disconnected")
        channel = None
        try:
            async with asyncio.timeout(HEALTH_TIMEOUT):
                async with self._health_lock:
                    if self._conn is not conn:
                        raise TransportError("the connection changed before the health probe")
                    channel = self._channels.get("health") or await self._open_shell(conn, "health")
                    # The probe prints __EE_HEALTH_DONE__ itself; the numbered marker ends the exchange.
                    return await self._exchange(channel, HEALTH_COMMAND, prefix="__EE_HEALTH_DONE_",
                                                limit=MAX_HEALTH_RESPONSE)
        except (Exception, asyncio.CancelledError) as exc:
            if channel is not None:
                if self._channels.get("health") is channel:
                    self._channels.pop("health")
                self._retire_process(conn, channel.process)
            if isinstance(exc, asyncio.CancelledError):
                raise
            raise TransportError("switch health could not be read") from exc

    async def close(self) -> None:
        task = self._close_task
        if task is None or task.done():
            if self._cleanup is None:
                conn, self._conn = self._conn, None
                client, self._rgb_client = self._rgb_client, None
                channels, self._channels = self._channels, {}
                processes = [channel.process for channel in channels.values()]
                if self._prior_port_reader is not None:
                    processes.append(self._prior_port_reader.process)
                    self._prior_port_reader = None
                self._rgb_checked = self._rgb_failed = False
                self._cleanup = {"connection": conn, "client": client,
                                 "processes": processes,
                                 "eof": set(), "exited": set(), "late_connections": []}
            task = self._close_task = asyncio.create_task(self._finish_close(), name="ee-ssh-close")
        try:
            await asyncio.shield(task)
        except asyncio.CancelledError:
            # The caller cannot abandon cleanup after detaching its handles.
            with contextlib.suppress(Exception):
                await task
            raise
        finally:
            if task.done() and self._close_task is task:
                self._close_task = None

    async def _finish_close(self) -> None:
        cleanup = self._cleanup
        conn = cleanup["connection"]
        try:
            if self._connecting:
                await asyncio.wait_for(self._connect_done.wait(), SSH_CLOSE_TIMEOUT)
            cleanup["late_connections"].extend(self._late_connections)
            self._late_connections = []
            if cleanup["client"] is not None:
                # The owning SSH connection's confirmed close also closes its direct streams.
                with contextlib.suppress(Exception):
                    await asyncio.wait_for(cleanup["client"].close(), SSH_CLOSE_TIMEOUT)
                cleanup["client"] = None
            if self._opening.get(id(conn), 0):
                await asyncio.wait_for(self._opening_done[id(conn)].wait(), SSH_CLOSE_TIMEOUT)
            retired = [(owner, process) for owner, process in self._retired_processes if owner is conn]
            self._retired_processes = [
                (owner, process) for owner, process in self._retired_processes if owner is not conn]
            cleanup["processes"].extend(process for _, process in retired if process not in cleanup["processes"])

            async def finish_process(process):
                key = id(process)
                if key in cleanup["exited"]:
                    return
                if key not in cleanup["eof"] and not _process_exited(process):
                    process.stdin.write_eof()
                    cleanup["eof"].add(key)
                await asyncio.wait_for(process.wait_closed(), SSH_CLOSE_TIMEOUT)
                if not _process_exited(process):
                    raise TransportError("a switch helper did not acknowledge exit")
                cleanup["exited"].add(key)

            results = await asyncio.gather(*(finish_process(process) for process in cleanup["processes"]),
                                           return_exceptions=True)
            if any(isinstance(result, BaseException) for result in results):
                raise TransportError("EE switch helper cleanup has not been confirmed; retry handoff")
            if conn is not None:
                conn.close()
                await asyncio.wait_for(conn.wait_closed(), SSH_CLOSE_TIMEOUT)
            for late in cleanup["late_connections"]:
                late.close()
                await asyncio.wait_for(late.wait_closed(), SSH_CLOSE_TIMEOUT)
        except Exception as exc:
            raise TransportError("EE SSH cleanup has not been confirmed; retry handoff") from exc
        for process in cleanup['processes']:
            self._process_tags.pop(id(process), None)
        self._cleanup = None

    async def recover_disconnected_helpers(self) -> None:
        """Verify and stop only our tagged helpers after an acknowledged local disconnect."""
        from .helper_recovery import CONFIRMED, cleanup_command

        cleanup = self._cleanup
        if cleanup is None:
            return
        connection = cleanup['connection']
        if (connection is None or not connection.is_closed() or self._connecting
                or self._opening.get(id(connection), 0) or self._late_connections
                or cleanup['late_connections'] or self._retired_processes):
            raise TransportError('The previous SSH connection is not ready for recovery')
        if (self.host_keys is None or not self.host_key_fingerprint
                or self.host_keys.get(self.host, self.port) != self.host_key_fingerprint):
            raise TransportError('A verified switch identity is required for recovery')
        tags = [self._process_tags.get(id(process)) for process in cleanup['processes']]
        command = cleanup_command(tags)
        await asyncio.wait_for(connection.wait_closed(), SSH_CLOSE_TIMEOUT)
        probe = SSHTransport(self.host, port=self.port, username=self.username,
                             password=self.password, key_file=self.key_file, host_keys=self.host_keys,
                             connect_timeout=self.connect_timeout)
        try:
            await probe.connect()
            if probe.host_key_fingerprint != self.host_key_fingerprint:
                raise HostKeyMismatchError('The switch identity changed during recovery')
            reply = await probe._conn.run(command, check=True, timeout=10)
            if reply.stdout.strip() != CONFIRMED:
                raise TransportError('The previous switch helpers did not confirm shutdown')
        finally:
            await probe.close()
        for process in cleanup['processes']:
            self._process_tags.pop(id(process), None)
        self._cleanup = None


# Simulator

ETHERLIGHT_MCU_SIGNATURE = (
    "'etherlight.mcu' @a8cdb60d\n"
    '\t"get":{"mcu":"String","etherlight":"String"}\n'
    '\t"set":{"mcu_mode":"String","led_stop":"String","board_id":"String","brightness":"Integer",'
    '"behavior":"String","port_pwm":"String","port_rgb":"String","port_map_rgb":"String","mode_color":"String",'
    '"led_mode":"String","link":"String","calibration":"Boolean","info_mode":"Boolean","info_brightness":"Integer",'
    '"info_behavior":"Integer","debug":"Boolean","reset":"String","upgrade":"String","calibrate":"String",'
    '"diag":"String"}\n'
    '\t"dump":{}\n'
)

_UBUS_SET_RE = re.compile(r"ubus call etherlight\.mcu set '(\{.*?\})'")
_DEFAULT_SPEEDS = [10000, 2500, 5000, 2500, 10000, 5000, 10000, 5000, 0, 0]


class SimulatedTransport(Transport):
    """In-memory USW-Pro-XG-8-PoE, controlling its eight RJ45 ports by default.

    `leds` holds the last color written to each port as (hex without '#', level 1-100).
    """

    def __init__(self, *, lit_ports: int = 8, total_ports: int = 10, latency: float = 0.003, seed: int = 7) -> None:
        self.lit_ports = lit_ports
        self.total_ports = max(total_ports, lit_ports)
        self.latency = latency
        self._rng = random.Random(seed)
        self._connected = False
        self._lighting_generation = object()
        self.behavior = "steady"
        self.sfp_modes: dict[int, bool] = {}
        self.leds: dict[int, tuple[str, int]] = {}
        self.speed: dict[int, int] = {}
        self.link_up: dict[int, bool] = {}
        for port in range(1, self.total_ports + 1):
            speed = _DEFAULT_SPEEDS[port - 1] if port <= len(_DEFAULT_SPEEDS) else 1000
            self.speed[port] = speed or 10000
            self.link_up[port] = speed > 0
        self._counters = {p: [0, 0, 0, 0] for p in range(1, self.total_ports + 1)}
        self._last_tick = time.monotonic()
        self.command_count = 0
        self.anomaly: dict[int, int] = {}
        # Powered devices stay within the simulated Pro XG 8 PoE's 60 W per-port limit.
        self.poe_class: dict[int, int | None] = {p: None for p in range(1, self.lit_ports + 1)}
        self.poe_class.update({2: 4, 4: 3, 6: 6, 7: 6})
        self.base_cpu = 0.08  # share of CPU busy with everything else
        self.load = 0.4
        self._cpu_clock = time.monotonic()
        self._cpu_commands = 0
        self._idle_ticks = self._busy_ticks = 0

    async def read_health(self) -> str:
        if not self.connected:
            raise TransportError("simulator is disconnected")
        return ("__EE_HEALTH_V1__\nM\tMemTotal:\t1048576\tkB\nM\tMemAvailable:\t655360\tkB\n"
                "M\tMemFree:\t524288\tkB\nT\tthermal_zone0\tSimulated sensor\t46200\n"
                "S\t/simulated\text4\nFilesystem 1024-blocks Used Available Capacity Mounted on\n"
                "/dev/simulated 262144 65536 196608 25% /simulated\nE\n__EE_HEALTH_DONE__\n")

    @property
    def connected(self) -> bool:
        return self._connected

    @property
    def lighting_generation(self) -> object | None:
        return self._lighting_generation if self.connected else None

    @property
    def sfp_mode_supported(self) -> bool:
        return self.connected

    @property
    def behavior_supported(self) -> bool:
        return self.connected

    async def write_behavior(self, behavior: str) -> None:
        from .ubus import _behavior_data
        _behavior_data(behavior)
        if not self.connected:
            raise TransportError("not connected")
        self.behavior = behavior
        self.command_count += 1

    async def write_sfp_mode(self, port: int, direct: bool) -> None:
        from .ubus import _sfp_mode_data
        _sfp_mode_data(port, direct)
        if not self.connected:
            raise TransportError("not connected")
        self.sfp_modes[port] = direct
        self.command_count += 1

    async def connect(self) -> None:
        if not self._connected:
            self._lighting_generation = object()
        self._connected = True

    async def close(self) -> None:
        self._connected = False

    def set_link(self, port: int, up: bool) -> None:
        if port not in self.link_up:
            raise ValueError(f"port {port} does not exist on the simulated switch")
        self.link_up[port] = bool(up)

    def set_anomaly(self, port: int, code: int) -> None:
        if port not in self.link_up:
            raise ValueError(f"port {port} does not exist on the simulated switch")
        self.anomaly[port] = int(code)

    async def run(self, command: str, *, channel: str = "main", timeout: float = 10.0) -> str:
        if not self._connected:
            raise TransportError("not connected")
        from .switch import (
            MCU_SCHEMA_COMMAND,
            MCU_SCHEMA_EXIT,
            PORT_FIRMWARE_HASH,
            PORT_FIRMWARE_HASH_COMMAND,
            PRECISE_DAEMON_HASH,
            RUNNING_DAEMON_HASH,
        )
        if command == MCU_SCHEMA_COMMAND:
            return ETHERLIGHT_MCU_SIGNATURE + f"\n{MCU_SCHEMA_EXIT}0\n"
        if command == RUNNING_DAEMON_HASH:
            return PRECISE_DAEMON_HASH + "  /proc/123/exe\n"
        if command == PORT_FIRMWARE_HASH_COMMAND:
            return PORT_FIRMWARE_HASH + "  /usr/share/firmware/port_led_fw.bin\n"
        if self.latency:
            await asyncio.sleep(self.latency)
        out: list[str] = []
        for line in command.splitlines():
            line = line.strip()
            if not line:
                continue
            for part in (p.strip() for p in line.split(";")):
                m = _UBUS_SET_RE.search(part)
                if m:
                    spec = json.loads(m.group(1)).get("port_rgb")
                    if spec:
                        port, hexcolor, level = spec.split()
                        if not 1 <= int(level) <= 100:
                            raise TransportError("port_rgb level must be between 1 and 100")
                        if 1 <= int(port) <= self.total_ports:
                            self.leds[int(port)] = (hexcolor.lower(), int(level))
                        self.command_count += 1
                        out.append('{"set":"ok"}\n')
                elif part.startswith("swctrl port show"):
                    out.append(self._port_table())
                elif part.startswith("swctrl poe show"):
                    out.append(self._poe_table())
                elif part.startswith("ubus -v list etherlight.mcu"):
                    out.append(ETHERLIGHT_MCU_SIGNATURE)
                elif part.startswith("ubus list"):
                    out.append("etherlight\netherlight.mcu\n")
                elif part.startswith("hostname"):
                    out.append("USWProXG8PoE-SIM\n")
                elif part.startswith("grep -E") and "/etc/board.info" in part:
                    out.append("board.name=USW-Pro-XG-8-PoE\nboard.shortname=USPXG8P\nboard.sysid=0xed76\n")
                elif part.startswith("read -r a < /proc/stat"):
                    out.append(self._cpu_line())
                elif part.startswith("read -r b < /proc/loadavg"):
                    out.append(f"{self.load:.2f} {self.load:.2f} {self.load:.2f} 1/120 4242\n")
        return "".join(out)

    def _cpu_line(self) -> str:
        """Each port command costs about 10 ms of CPU, roughly what a real switch shows."""
        now = time.monotonic()
        elapsed = max(1e-3, now - self._cpu_clock)
        busy = min(0.97, self.base_cpu + (self.command_count - self._cpu_commands) * 0.010 / elapsed)
        self._cpu_clock, self._cpu_commands = now, self.command_count
        total = round(elapsed * 100)
        self._idle_ticks += round(total * (1 - busy))
        self._busy_ticks += total - round(total * (1 - busy))
        return f"cpu  {self._busy_ticks} 0 0 {self._idle_ticks} 0 0 0 0 0 0\n"

    def _port_table(self) -> str:
        now = time.monotonic()
        dt = max(0.0, now - self._last_tick)
        self._last_tick = now
        rows = [
            "                         Packets                  Bytes",
            "Port  Link  Rate    Receive   Transmit     Receive     Transmit   state    STP     "
            "1x Ctrl Mode  1x Status    Anomaly      LAG",
            "----  ---- ------ ---------- ---------- ------------ ------------ ----- ---------- "
            "------------ ------------ ------------ --------",
        ]
        for port in range(1, self.total_ports + 1):
            c = self._counters[port]
            up = self.link_up[port]
            if up:
                load = self._rng.random() ** 3 * 0.3  # bursty, mostly quiet
                moved = int(self.speed[port] * 1e6 / 8 * load * dt)
                rx = int(moved * self._rng.uniform(0.2, 0.8))
                c[2] += rx
                c[3] += moved - rx
                c[0] += rx // 900
                c[1] += (moved - rx) // 900
            rate = f"{self.speed[port]}F" if up else "0H"
            link = "U/U" if up else "U/D"
            prefix = " U" if port == 5 else "  "
            state = "forwarding" if up else "disabled"
            anomaly = hex(self.anomaly.get(port, 0))
            rows.append(
                f"{prefix}{port:>2}  {link}  {rate:>6} {c[0]:>10} {c[1]:>10} {c[2]:>12} {c[3]:>12}"
                f"     J {state:<10} unknown      disabled     {anomaly}"
            )
        return "\n".join(rows) + "\n"

    def _poe_table(self) -> str:
        rows = [
            "Total Power Limit(mW): 155000",
            "",
            "Port  OpMode      HpMode    PwrLimit   Class   PoEPwr  PwrGood  Power(W)  Voltage(V)  Current(mA)",
            "                              (mW)",
            "----  ------  ------------  --------  -------  ------  -------  --------  ----------  -----------",
        ]
        draw = {3: 6.5, 4: 12.0, 6: 38.0, 8: 71.0}
        for port, pd_class in self.poe_class.items():
            on = pd_class is not None and self.link_up.get(port, False)
            watts = min(60.0, draw.get(pd_class, 3.0) * self._rng.uniform(0.9, 1.1)) if on else 0.0
            volts = 53.6 if on else 0.0
            amps = watts / volts * 1000 if on else 0.0
            label = f"Class {pd_class}" if on else "Unknown"
            rows.append(
                f"{port:>4}    Auto  Dot3bt-Type3     60000  {label:>7}  {'On' if on else 'Off':>6}  "
                f"{'Good' if on else 'Bad':>7}  {watts:>8.2f}  {volts:>10.2f}  {amps:>11.2f}"
            )
        return "\n".join(rows) + "\n"
