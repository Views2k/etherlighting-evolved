"""Fan control for the USW-Pro-XG-8-PoE.

EE can leave the fans to the switch, follow one of four temperature curves, hold one fixed speed, or adjust the
speed to keep the switch CPU at a chosen temperature. The control loop runs on the switch from RAM
(`resident/fans.sh`), so it keeps working while EE is closed. A switch restart returns the fans to the switch's
own control until EE connects again and restores the saved choice.

The Python step function below mirrors the shell controller exactly. Tests compare the two, and the simulator
uses the Python version.
"""
from __future__ import annotations

import asyncio
import contextlib
import hashlib
import json
import math
import os
import secrets
import shlex
import time
from dataclasses import asdict, dataclass, replace
from pathlib import Path
from typing import Any

from .transport import TransportError

MODES = ("switch", "curve", "manual", "target")
# CPU temperature in degrees C and fan speed in percent. Speeds between points are interpolated.
CURVES: dict[str, tuple[tuple[int, int], ...]] = {
    "low": ((55, 30), (70, 40), (80, 60), (90, 100)),
    "medium": ((45, 30), (60, 50), (70, 75), (80, 100)),
    "high": ((40, 40), (55, 65), (65, 85), (75, 100)),
    "max": ((35, 70), (50, 90), (55, 100)),
}
CURVE_NAMES = {"low": "Low", "medium": "Medium", "high": "High", "max": "Max"}
MIN_DUTY = 30
MANUAL_RANGE = (MIN_DUTY, 100)
TARGET_RANGE = (45, 85)
# These match fans.sh.
KP = 4
KI = 150
BOOST = 5
BAND = 2
HEAT = 39_830_000
GUARDS = {"cpu": (85, 95), "sfp": (65, 75), "port": (100, 110)}

LOCATION = "/tmp/ee-fan-control-v1"
SCRIPT = Path(__file__).with_name("resident") / "fans.sh"
POLL_INTERVAL = 5.0
# Readings refresh this often for a while after a change, so the fan speed can be seen following it.
FAST_POLL_INTERVAL = 1.5
FAST_POLL_SECONDS = 30.0
RETRY_INTERVAL = 60.0
_CHANNEL = "ee-fans"
_CHECK_DIRECTORY = '[ ! -L "$d" ] && [ -d "$d" ] && [ "$(stat -c %u:%a "$d")" = "$(id -u):700" ]'
_TMP_CHECK = 'cd /tmp && [ "$(pwd -P)" = /tmp ] || exit 1'
_HW = "/sys/class/hwmon/hwmon0/device"


def _trunc(a: int, b: int) -> int:
    """Integer division toward zero, as the switch shell does it."""
    q = abs(a) // abs(b)
    return q if (a >= 0) == (b >= 0) else -q


def _ramp(value: int, start: int, full: int) -> int:
    if value <= start * 1000:
        return 0
    if value >= full * 1000:
        return 100
    return MIN_DUTY + _trunc((100 - MIN_DUTY) * (value - start * 1000), (full - start) * 1000)


def step(cpu: int, sfp: int, port: int, last: int, integral: int, words: list[str]) -> tuple[int, int, str]:
    """One control step with readings in millidegrees. Returns duty percent, integral and the reason."""
    mode, values = words[0], [int(w) for w in words[1:]]
    why = mode
    if mode == "manual":
        duty = values[0]
    elif mode == "target":
        error = cpu - values[0] * 1000
        p = min(max(_trunc(KP * error, 1000), -BOOST), BOOST)
        rate = KI if -BAND * 1000 <= error <= BAND * 1000 else _trunc(KI, 16)
        following = min(max(integral + _trunc(rate * error, 1000), 0), (100 - MIN_DUTY) * 1000)
        trial = MIN_DUTY + p + _trunc(following, 1000)
        if not (trial > 100 and error > 0) and not (trial < MIN_DUTY and error < 0):
            integral = following
        duty = MIN_DUTY + p + _trunc(integral, 1000)
    elif mode == "curve":
        points = list(zip(values[::2], values[1::2], strict=True))
        duty = points[0][1]
        if cpu > points[0][0] * 1000:
            duty = points[-1][1]
            for (t0, d0), (t1, d1) in zip(points[:-1], points[1:], strict=True):
                if cpu <= t1 * 1000:
                    duty = d0 + _trunc((d1 - d0) * (cpu - t0 * 1000), (t1 - t0) * 1000)
                    break
        duty = max(duty, last - 2)
    else:
        raise ValueError(mode)
    duty = min(max(duty, MIN_DUTY), 100)
    guard = max(_ramp(cpu, *GUARDS["cpu"]), _ramp(sfp, *GUARDS["sfp"]), _ramp(port, *GUARDS["port"]))
    if guard > duty:
        duty, why = guard, "guard"
    return duty, integral, why


def estimate(cpu: int, average: int, target: int) -> int:
    """The fan speed that would hold the CPU at the target, from the CPU reading in millidegrees and the
    average fan speed of the last couple of minutes in tenths of a percent."""
    room = cpu - _trunc(HEAT, 291 + average)
    need = target * 1000 - room
    speed = 100 if need <= 0 else _trunc(_trunc(HEAT, need) - 291, 10)
    return min(max(speed, MIN_DUTY), 100)


def start_integral(cpu: int, average: int, target: int) -> int:
    """The integral a new thermal target starts with: the estimated holding speed."""
    return (estimate(cpu, average, target) - MIN_DUTY) * 1000


def next_average(average: int, duty: int) -> int:
    return average + _trunc(duty * 10 - average, 24)


@dataclass(frozen=True)
class FanSettings:
    mode: str = "switch"
    curve: str = "medium"
    manual_percent: int = 60
    target_celsius: int = 55

    def __post_init__(self) -> None:
        if self.mode not in MODES:
            raise ValueError("Choose Switch, Curve, Manual or Target for the fans.")
        if self.curve not in CURVES:
            raise ValueError("Choose the Low, Medium, High or Max fan curve.")
        for value, (low, high), name in ((self.manual_percent, MANUAL_RANGE, "Fan speed"),
                                         (self.target_celsius, TARGET_RANGE, "Target temperature")):
            if isinstance(value, bool) or not isinstance(value, int) or not low <= value <= high:
                raise ValueError(f"{name} must be a whole number from {low} to {high}.")

    @classmethod
    def from_dict(cls, data: dict[str, Any], base: FanSettings | None = None) -> FanSettings:
        known = {key: data[key] for key in ("mode", "curve", "manual_percent", "target_celsius") if key in data}
        return replace(base or cls(), **known)

    def config_line(self) -> str | None:
        if self.mode == "manual":
            return f"manual {self.manual_percent}"
        if self.mode == "target":
            return f"target {self.target_celsius}"
        if self.mode == "curve":
            return "curve " + " ".join(f"{t} {d}" for t, d in CURVES[self.curve])
        return None


class FanStore:
    def __init__(self, path: Path) -> None:
        self.path = path

    def load(self) -> FanSettings:
        try:
            return FanSettings.from_dict(json.loads(self.path.read_text("utf-8")))
        except (OSError, UnicodeError, ValueError, TypeError, AttributeError):
            return FanSettings()

    def save(self, settings: FanSettings) -> None:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        temp = self.path.with_suffix("." + secrets.token_hex(8) + ".tmp")
        try:
            with temp.open("x", encoding="utf-8") as stream:
                json.dump(asdict(settings), stream)
                stream.flush()
                os.fsync(stream.fileno())
            temp.replace(self.path)
        finally:
            temp.unlink(missing_ok=True)


def _number(value: str) -> int | None:
    return int(value) if value.isdigit() and len(value) <= 9 else None


def _celsius(value: int | None) -> float | None:
    return None if value is None or value > 200_000 else round(value / 1000, 1)


def parse_reading(output: str) -> dict[str, Any]:
    """Parse the read command below into readings, daemon state and EE controller state."""
    lines = {line[:1]: line[2:].split() for line in output.splitlines() if line[1:2] == " "}
    raw = lines.get("R", [])
    if len(raw) != 8:
        raise TransportError("The switch did not return its fan readings.")
    pwm1, pwm2, rpm1, rpm2, cpu, board, sfp, port = (_number(v) for v in raw)
    daemon = {"R": "running", "S": "running", "D": "running", "T": "paused", "t": "paused"}.get(
        (lines.get("D") or ["none"])[0], "absent")
    controller = lines.get("C", ["absent"])
    status = lines.get("S", [])
    active = controller[0] == "1"
    result = {
        "cpu_celsius": _celsius(cpu),
        "board_celsius": _celsius(board),
        "sfp_celsius": _celsius(sfp),
        "port_celsius": _celsius(port) if port else None,
        "fans": [{"rpm": rpm, "percent": None if pwm is None else round(pwm * 100 / 255)}
                 for rpm, pwm in ((rpm1, pwm1), (rpm2, pwm2))],
        "daemon": daemon,
        "controller": "absent" if controller[0] == "absent" else "running" if active else "stopped",
        "script": controller[1] if len(controller) > 1 else None,
        "reason": status[1] if active and len(status) >= 10 else None,
        "config": " ".join(lines.get("F", [])) or None,
    }
    if active and len(status) >= 10 and _number(status[2]) is not None:
        result["duty"] = _number(status[2])
    return result


_READ = f"""
r() {{ v=; read -r v 2>/dev/null < "$1"; printf ' %s' "${{v:-NA}}"; }}
printf R; r $h/pwm1; r $h/pwm2; r $h/fan1_input; r $h/fan2_input; r $h/temp1_input; r $h/temp2_input; r $h/temp3_input
m=0; for i in 1 2 3 4 5 6 7 8; do v=; read -r v 2>/dev/null < "$q/$i"
case "$v" in ''|*[!0-9]*) ;; *) [ "$v" -le "$m" ] || m=$v;; esac; done; echo " $m"
p=$(pidof ubnt-fanctrl 2>/dev/null); s=
[ -z "$p" ] || s=$(awk '/^State:/{{print $2; exit}}' /proc/${{p%% *}}/status 2>/dev/null); echo "D ${{s:-none}}"
if {_CHECK_DIRECTORY}; then
  pid=$(cat "$d/pid" 2>/dev/null); alive=0
  case "$pid" in ''|*[!0-9]*) ;; *) [ "$(readlink /proc/$pid/cwd 2>/dev/null)" = "$d" ] && alive=1;; esac
  echo "C $alive $(sha256sum "$d/fans.sh" 2>/dev/null | cut -d' ' -f1)"
  echo "S $(cat "$d/status" 2>/dev/null)"; echo "F $(cat "$d/config" 2>/dev/null)"
else echo "C absent"; fi"""


class SwitchFans:
    """Runs fans.sh on the switch over the existing SSH connection."""

    def __init__(self, transport, *, location: str = LOCATION, hw: str = _HW, phy: str = "/proc/phy_temp") -> None:
        self.transport = transport
        # Shell variables for EE's directory, the fan controller and the port temperatures.
        self.paths = f"d={shlex.quote(location)}; h={shlex.quote(hw)}; q={shlex.quote(phy)}"
        self.script = SCRIPT.read_text("ascii")
        self.digest = hashlib.sha256(self.script.encode("ascii")).hexdigest()

    async def _run(self, command: str, timeout: float = 10) -> str:
        # Fan commands use their own shell: a failure there must not close the lighting connection.
        run = getattr(self.transport, "run_isolated", None) or self.transport.run
        return (await run("(\n" + command + "\n)", channel=_CHANNEL, timeout=timeout)).strip()

    async def read(self) -> dict[str, Any]:
        reading = parse_reading(await self._run(self.paths + _READ))
        reading["script_current"] = reading.pop("script") == self.digest
        return reading

    def _inside(self, command: str) -> str:
        return f'{self.paths}; {_CHECK_DIRECTORY} && cd "$d" || exit 1\n{command}'

    async def _install(self) -> None:
        reserve = (f'umask 077; {self.paths}; {_TMP_CHECK}\n'
                   f'if [ ! -e "$d" ] && [ ! -L "$d" ]; then mkdir -m 700 "$d" || exit 1; fi\n'
                   f'{_CHECK_DIRECTORY} && echo ready')
        if await self._run(reserve) != "ready":
            raise TransportError("The switch could not prepare its fan control directory.")
        for offset in range(0, len(self.script), 8192):
            redirect = ">" if offset == 0 else ">>"
            chunk = shlex.quote(self.script[offset:offset + 8192])
            if await self._run(self._inside(f"printf %s {chunk} {redirect} fans.next && echo saved")) != "saved":
                raise TransportError("The switch could not store its fan control.")
        verify = f'[ "$(sha256sum fans.next | cut -d" " -f1)" = {self.digest} ] && mv -f fans.next fans.sh && echo ok'
        if await self._run(self._inside(verify)) != "ok":
            raise TransportError("The fan control on the switch did not pass its integrity check.")
        check = await self._run(self._inside("sh fans.sh check"))
        if check != "ok":
            raise ValueError(check or "The switch could not confirm fan control support.")

    async def apply(self, line: str) -> dict[str, Any]:
        reading = await self.read()
        if reading["controller"] == "running" and not reading["script_current"]:
            await self.stop()
            reading = await self.read()
        if reading["controller"] != "running":
            await self._install()
        command = f"printf '%s\\n' {shlex.quote(line)} > config.next && mv -f config.next config && echo saved"
        if await self._run(self._inside(command)) != "saved":
            raise TransportError("The switch could not save the fan setting.")
        if reading["controller"] != "running":
            start = ('rm -f stop status\nnohup sh fans.sh run </dev/null >/dev/null 2>&1 &\n'
                     'echo $! > pid && echo started')
            if await self._run(self._inside(start)) != "started":
                raise TransportError("The switch did not start fan control.")
        confirm = 'printf "%s|%s\\n" "$(cat active 2>/dev/null)" "$(cat status 2>/dev/null)"'
        # The controller takes a new setting within a second; check often so the change shows quickly.
        deadline = time.monotonic() + 16
        while time.monotonic() < deadline:
            active, _, status = (await self._run(self._inside(confirm))).partition("|")
            if active == line:
                reading = await self.read()
                if reading["controller"] == "running":
                    return reading
            if status.split()[:1] in (["unsupported"], ["released"]):
                break
            await asyncio.sleep(0.25)
        with contextlib.suppress(TransportError, ValueError):
            await self.stop()
        raise TransportError("The switch did not confirm the fan setting. The fans are back on switch control.")

    async def stop(self) -> None:
        """Return the fans to the switch's own control and remove EE's files."""
        command = f"""{self.paths}
if {_CHECK_DIRECTORY}; then
  cd "$d" || exit 1
  : > stop
  for n in 1 2 3 4 5 6 7 8 9 10 11 12; do
    pid=$(cat pid 2>/dev/null)
    case "$pid" in ''|*[!0-9]*) break;; esac
    [ "$(readlink /proc/$pid/cwd 2>/dev/null)" = "$d" ] || break
    sleep 1
  done
  pid=$(cat pid 2>/dev/null)
  case "$pid" in ''|*[!0-9]*) ;;
    *) [ "$(readlink /proc/$pid/cwd 2>/dev/null)" != "$d" ] || kill "$pid" 2>/dev/null;; esac
  sleep 1
  cd /tmp && rm -f "$d"/fans.sh "$d"/fans.next "$d"/config "$d"/config.next "$d"/status "$d"/status.new \\
    "$d"/active "$d"/active.new "$d"/stop "$d"/pid &&
  rmdir "$d" 2>/dev/null
fi
for p in $(pidof ubnt-fanctrl 2>/dev/null); do kill -CONT "$p" 2>/dev/null; done
p=$(pidof ubnt-fanctrl 2>/dev/null)
s=; [ -z "$p" ] || s=$(awk '/^State:/{{print $2; exit}}' /proc/${{p%% *}}/status 2>/dev/null)
[ -e "$d" ] && echo remaining || echo "released ${{s:-none}}\""""
        result = await self._run(command, timeout=20)
        if not result.startswith("released") or result.endswith(" T"):
            raise TransportError("The switch did not confirm returning the fans to its own control.")


class SimulatedFans:
    """A Pro XG 8 PoE thermal model fitted to measured fan tests, for demo sessions and tests."""

    TAU = 200.0

    def __init__(self, *, ambient: float = 18.0, clock=time.monotonic) -> None:
        self.ambient = ambient
        self.clock = clock
        self.cpu = self._equilibrium(42)
        self.duty = 42
        self.average = 420
        self.integral = 0
        self.line: str | None = None
        self.daemon_integral = 12_000
        self.updated = clock()

    def _equilibrium(self, duty: float) -> float:
        return self.ambient + 3983 / (29.1 + duty)

    def _advance(self) -> None:
        now = self.clock()
        while now - self.updated >= POLL_INTERVAL:
            self.updated += POLL_INTERVAL
            cpu, _, sfp, port = self._millidegrees()
            if self.line is None:
                # The switch's own controller holds its SFP sensor near 65 C.
                self.duty, self.daemon_integral, _ = step(
                    sfp + 5000, sfp, port, self.duty, self.daemon_integral, ["target", "70"])
            else:
                self.duty, self.integral, _ = step(cpu, sfp, port, self.duty, self.integral, self.line.split())
            self.average = next_average(self.average, self.duty)
            self.cpu += (self._equilibrium(self.duty) - self.cpu) * POLL_INTERVAL / self.TAU
        if now - self.updated > 3600:
            self.updated = now

    def _millidegrees(self) -> tuple[int, int, int, int]:
        cpu = round(self.cpu * 4) * 250
        return cpu, cpu - 6000, cpu - 4500, cpu + 21000

    async def read(self) -> dict[str, Any]:
        self._advance()
        cpu, board, sfp, port = self._millidegrees()
        rpm = round(600 + self.duty * 50 + math.sin(self.updated / 7) * 20)
        return {"cpu_celsius": cpu / 1000, "board_celsius": board / 1000, "sfp_celsius": sfp / 1000,
                "port_celsius": port / 1000, "fans": [{"rpm": rpm, "percent": self.duty},
                                                     {"rpm": rpm + 30, "percent": self.duty}],
                "daemon": "paused" if self.line else "running",
                "controller": "running" if self.line else "absent", "script_current": True,
                "reason": None if self.line is None else step(cpu, sfp, port, self.duty, self.integral,
                                                              self.line.split())[2],
                "config": self.line, **({"duty": self.duty} if self.line else {})}

    async def apply(self, line: str) -> dict[str, Any]:
        self._advance()
        words = line.split()
        cpu, _, sfp, port = self._millidegrees()
        fresh = self.line != line
        if words[0] == "target" and fresh:
            self.integral = start_integral(cpu, self.average, int(words[1]))
        self.line = line
        # The switch confirms a setting after the controller's first step with it; a new setting skips the easing.
        self.duty, self.integral, _ = step(cpu, sfp, port, 0 if fresh else self.duty, self.integral, words)
        self.average = next_average(self.average, self.duty)
        return await self.read()

    async def stop(self) -> None:
        self._advance()
        self.line = None


class FanControl:
    """Keeps the saved fan choice running on the connected switch and reports the readings."""

    def __init__(self, backend, store: FanStore | None, *, supported: bool) -> None:
        self.backend = backend
        self.store = store
        self.supported = supported
        self.settings = store.load() if store else FanSettings()
        self.reading: dict[str, Any] | None = None
        self.error: str | None = None
        self.unsupported: str | None = None if supported else "Fan control needs a USW-Pro-XG-8-PoE."
        self.lock = asyncio.Lock()
        self._retry_at = 0.0
        self._fast_until = 0.0

    def interval(self) -> float:
        """Seconds until the next reading: shorter for a while after a change."""
        return FAST_POLL_INTERVAL if time.monotonic() < self._fast_until else POLL_INTERVAL

    def to_dict(self) -> dict[str, Any]:
        return {
            "supported": self.supported and self.unsupported is None,
            "unsupported_reason": self.unsupported,
            "settings": asdict(self.settings),
            "reading": self.reading,
            "error": self.error,
            "curves": {name: {"name": CURVE_NAMES[name], "points": [list(p) for p in points]}
                       for name, points in CURVES.items()},
            "limits": {"manual_percent": list(MANUAL_RANGE), "target_celsius": list(TARGET_RANGE)},
        }

    async def update(self, change: dict[str, Any]) -> None:
        if not self.supported or self.unsupported:
            raise ValueError(self.unsupported or "Fan control is not available on this switch.")
        settings = FanSettings.from_dict(change, self.settings)
        self._fast_until = time.monotonic() + FAST_POLL_SECONDS
        async with self.lock:
            try:
                await self._enforce(settings)
            except ValueError as exc:
                self.unsupported = str(exc)
                raise
            finally:
                self._retry_at = 0.0
            self.settings = settings
            if self.store:
                self.store.save(settings)

    async def _enforce(self, settings: FanSettings) -> None:
        line = settings.config_line()
        try:
            if line is None:
                await self.backend.stop()
                self.reading = await self.backend.read()
            else:
                self.reading = await self.backend.apply(line)
        except (TransportError, ValueError) as exc:
            self.error = str(exc)
            raise
        self.error = None

    async def poll(self) -> None:
        """Read the fans; restore the saved choice after a switch restart or a stopped controller."""
        if not self.supported or self.unsupported:
            return
        async with self.lock:
            try:
                reading = await self.backend.read()
            except TransportError:
                self.reading = None
                raise
            self.reading = reading
            line = self.settings.config_line()
            running = reading["controller"] == "running"
            if line is None:
                # Only EE's own files count. A paused daemon without them belongs to someone else.
                stale = running or reading["controller"] == "stopped"
            else:
                stale = not running or not reading.get("script_current") or reading.get("config") != line
            if not stale or time.monotonic() < self._retry_at:
                return
            try:
                await self._enforce(self.settings)
            except ValueError as exc:
                self.unsupported = str(exc)
            except TransportError:
                self._retry_at = time.monotonic() + RETRY_INTERVAL
