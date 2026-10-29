"""How commands reach the switch.

`SSHTransport` keeps one SSH connection open with a long-lived `sh` per channel, so each LED update is a write
to an open shell instead of a new SSH login. `SimulatedTransport` emulates a USW-Pro-XG-8-PoE in memory, for UI
development, demos and tests.
"""

from __future__ import annotations

import asyncio
import contextlib
import json
import random
import re
import time
from abc import ABC, abstractmethod


class TransportError(RuntimeError):
    """The switch could not be reached or stopped answering."""


class AuthenticationError(TransportError):
    """The switch rejected the SSH username or password / key."""


class UnreachableError(TransportError):
    """Nothing answered SSH at that address and port."""


class HostKeyMismatchError(TransportError):
    """The switch's SSH host key differs from the one pinned at first login (possible impersonation)."""


class HostKeyStore:
    """Trust-on-first-use pinning of switch SSH host keys, kept in a small JSON file.

    The first successful login to an address pins its key fingerprint; later logins must present the same key.
    """

    def __init__(self, path) -> None:
        from pathlib import Path

        self.path = Path(path)

    def _load(self) -> dict[str, str]:
        try:
            data = json.loads(self.path.read_text("utf-8"))
            return data if isinstance(data, dict) else {}
        except (OSError, json.JSONDecodeError):
            return {}

    def _save(self, data: dict[str, str]) -> None:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.path.write_text(json.dumps(data, indent=2, sort_keys=True), "utf-8")

    @staticmethod
    def _key(host: str, port: int) -> str:
        return f"{host.strip().lower()}:{port}"

    def get(self, host: str, port: int) -> str | None:
        return self._load().get(self._key(host, port))

    def verify_or_pin(self, host: str, port: int, fingerprint: str) -> bool:
        """Returns True when the key was pinned now (first use). Raises HostKeyMismatchError on a changed key."""
        data = self._load()
        key = self._key(host, port)
        pinned = data.get(key)
        if pinned is None:
            data[key] = fingerprint
            self._save(data)
            return True
        if pinned != fingerprint:
            raise HostKeyMismatchError(
                f"The SSH key of {host} changed (was {pinned}, now {fingerprint}). If you reset or replaced the "
                "switch, forget the old key and log in again; otherwise something may be impersonating it."
            )
        return False

    def forget(self, host: str, port: int = 22) -> bool:
        data = self._load()
        removed = data.pop(self._key(host, port), None) is not None
        if removed:
            self._save(data)
        return removed


class Transport(ABC):
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


# SSH

class _Channel:
    def __init__(self, process) -> None:
        self.process = process
        self.lock = asyncio.Lock()


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
        self.host_key_fingerprint: str | None = None
        self.host_key_pinned_now = False
        self._conn = None
        self._channels: dict[str, _Channel] = {}
        self._seq = 0

    @property
    def connected(self) -> bool:
        return self._conn is not None

    async def connect(self) -> None:
        try:
            import asyncssh
        except ImportError as exc:  # pragma: no cover
            raise TransportError("asyncssh is not installed: pip install asyncssh") from exc
        # Host keys are checked below against our own trust-on-first-use store instead of ~/.ssh/known_hosts,
        # so a first login from the UI never stalls on an interactive "trust this host?" prompt.
        kwargs: dict = {"host": self.host, "port": self.port, "known_hosts": None}
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
        except asyncssh.PermissionDenied as exc:
            raise AuthenticationError("SSH login failed: wrong username or password") from exc
        except (TimeoutError, OSError) as exc:
            reason = "timed out" if isinstance(exc, asyncio.TimeoutError) else (exc.strerror or str(exc))
            raise UnreachableError(f"Can't reach {self.host}:{self.port} over SSH ({reason})") from exc
        except Exception as exc:
            raise TransportError(f"SSH connection to {self.host} failed: {exc}") from exc
        try:
            key = conn.get_server_host_key()
            self.host_key_fingerprint = key.get_fingerprint("sha256") if key else None
            if self.host_keys is not None and self.host_key_fingerprint:
                self.host_key_pinned_now = self.host_keys.verify_or_pin(self.host, self.port, self.host_key_fingerprint)
        except Exception:
            conn.close()
            raise
        self._conn = conn

    async def _channel(self, name: str) -> _Channel:
        channel = self._channels.get(name)
        if channel is None:
            import asyncssh

            process = await self._conn.create_process("sh", stderr=asyncssh.STDOUT)
            channel = _Channel(process)
            self._channels[name] = channel
        return channel

    async def run(self, command: str, *, channel: str = "main", timeout: float = 10.0) -> str:
        if self._conn is None:
            raise TransportError("not connected")
        try:
            ch = await self._channel(channel)
        except Exception as exc:
            await self.close()
            raise TransportError(f"could not open a shell on the switch: {exc}") from exc
        async with ch.lock:
            self._seq += 1
            marker = f"__EE_DONE_{self._seq}__"
            try:
                ch.process.stdin.write(f"{command}\necho {marker}\n")
                lines: list[str] = []
                while True:
                    line = await asyncio.wait_for(ch.process.stdout.readline(), timeout)
                    if line == "":
                        raise TransportError("the switch closed the shell")
                    if line.strip() == marker:
                        return "".join(lines)
                    lines.append(line)
            except TransportError:
                await self.close()
                raise
            except TimeoutError as exc:
                await self.close()
                raise TransportError(f"the switch did not answer within {timeout:g}s") from exc
            except Exception as exc:
                await self.close()
                raise TransportError(f"SSH error: {exc}") from exc

    async def close(self) -> None:
        for channel in list(self._channels.values()):
            with contextlib.suppress(Exception):
                channel.process.stdin.write_eof()
                channel.process.close()
        self._channels.clear()
        conn, self._conn = self._conn, None
        if conn is not None:
            conn.close()
            with contextlib.suppress(Exception):
                await asyncio.wait_for(conn.wait_closed(), 3)


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
    """In-memory USW-Pro-XG-8-PoE: 8 Etherlighting RJ45 ports plus 2 SFP+ ports (no LEDs).

    `leds` holds the last color written to each port as (hex without '#', level 0-100).
    """

    def __init__(self, *, lit_ports: int = 8, total_ports: int = 10, latency: float = 0.003, seed: int = 7) -> None:
        self.lit_ports = lit_ports
        self.total_ports = max(total_ports, lit_ports)
        self.latency = latency
        self._rng = random.Random(seed)
        self._connected = False
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
        # PD class per powered port: an AP on 2, a camera on 4, a PoE++ AP on 6 and a 90 W display on 7.
        self.poe_class: dict[int, int | None] = {p: None for p in range(1, self.lit_ports + 1)}
        self.poe_class.update({2: 4, 4: 3, 6: 6, 7: 8})

    @property
    def connected(self) -> bool:
        return self._connected

    async def connect(self) -> None:
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
                        if 1 <= int(port) <= self.lit_ports:
                            self.leds[int(port)] = (hexcolor.lower(), int(level))
                        self.command_count += 1
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
        return "".join(out)

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
            watts = draw.get(pd_class, 3.0) * self._rng.uniform(0.9, 1.1) if on else 0.0
            volts = 53.6 if on else 0.0
            amps = watts / volts * 1000 if on else 0.0
            label = f"Class {pd_class}" if on else "Unknown"
            rows.append(
                f"{port:>4}    Auto  Dot3bt-Type4     99900  {label:>7}  {'On' if on else 'Off':>6}  "
                f"{'Good' if on else 'Bad':>7}  {watts:>8.2f}  {volts:>10.2f}  {amps:>11.2f}"
            )
        return "\n".join(rows) + "\n"
