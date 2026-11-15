"""Fan control: the shared control step, settings, the simulator, and the switch-side shell commands."""
from __future__ import annotations

import asyncio
import json
import os
import random
import shutil
import subprocess
import time
from pathlib import Path

import pytest

from etherlighting_evolved import fans
from etherlighting_evolved.fans import (
    CURVES,
    FanControl,
    FanSettings,
    FanStore,
    SimulatedFans,
    SwitchFans,
    parse_reading,
    step,
)
from etherlighting_evolved.transport import TransportError


def _posix_shell():
    if os.name != "nt":
        return shutil.which("sh")
    # On Windows use the shell from Git for Windows; a bare "bash" can be the WSL launcher.
    git = shutil.which("git")
    if git:
        # git.exe sits in Git\cmd, Git\bin or Git\mingw64\bin.
        for root in Path(git).resolve().parents[:3]:
            candidate = root / "usr" / "bin" / "sh.exe"
            if candidate.is_file():
                return str(candidate)
    return None


SHELL = _posix_shell()
needs_shell = pytest.mark.skipif(SHELL is None, reason="a POSIX shell is required")
pytestmark = pytest.mark.anyio


# The shell's own tools (nohup, awk, sleep) come first, as they would on the switch.
BASE_ENV = {**os.environ, "PATH": os.path.dirname(SHELL) + os.pathsep + os.environ.get("PATH", "")} if SHELL else {}


def shell(*args, env=None, cwd=None, timeout=20):
    return subprocess.run([SHELL, *args], capture_output=True, text=True, env=env or BASE_ENV, cwd=cwd, timeout=timeout)


def test_curves_rise_and_stay_in_range():
    for points in CURVES.values():
        temperatures = [t for t, _ in points]
        duties = [d for _, d in points]
        assert temperatures == sorted(set(temperatures))
        assert duties == sorted(duties)
        assert all(fans.MIN_DUTY <= d <= 100 for d in duties)
        assert points[-1][1] == 100


@pytest.mark.parametrize("name,cpu,expected", [
    ("low", 50_000, 30), ("low", 75_000, 50), ("low", 95_000, 100),
    ("medium", 65_000, 62), ("high", 60_000, 75), ("max", 40_000, 76),
])
def test_curve_interpolates_cpu_temperature(name, cpu, expected):
    line = FanSettings(mode="curve", curve=name).config_line().split()
    assert step(cpu, 40_000, 70_000, 0, 0, line)[0] == expected


def test_curve_slows_down_two_percent_per_step():
    line = FanSettings(mode="curve", curve="low").config_line().split()
    assert step(50_000, 40_000, 70_000, 80, 0, line)[0] == 78


def test_guard_raises_any_mode_near_the_switch_limits():
    manual = ["manual", "30"]
    assert step(50_000, 40_000, 70_000, 30, 0, manual) == (30, 0, "manual")
    assert step(90_000, 40_000, 70_000, 30, 0, manual) == (65, 0, "guard")
    assert step(50_000, 75_000, 70_000, 30, 0, manual) == (100, 0, "guard")
    assert step(50_000, 40_000, 105_000, 30, 0, manual) == (65, 0, "guard")


def test_target_from_a_hot_start_runs_the_fans_at_full_speed():
    integral = fans.start_integral(74_000, 42, 55)
    assert integral == 0
    assert step(74_000, 60_000, 80_000, 42, integral, ["target", "55"])[0] == 100
    # Already at the target: the current speed is kept.
    assert step(55_000, 50_000, 80_000, 70, fans.start_integral(55_000, 70, 55), ["target", "55"])[0] == 70


def test_target_settles_without_windup():
    sim = SimulatedFans(ambient=15, clock=lambda: 0)
    sim.cpu, sim.duty = 71.0, 42
    sim.line = "target 55"
    sim.integral = fans.start_integral(71_000, 42, 55)
    history = []
    for _ in range(720):  # one hour
        sim.updated -= fans.POLL_INTERVAL
        sim._advance()
        history.append((sim.cpu, sim.duty))
    late = history[360:]
    assert all(abs(cpu - 55) < 1.0 for cpu, _ in late)
    assert min(d for _, d in late) >= 60
    assert max(d for _, d in late) <= 80
    assert min(cpu for cpu, _ in history) > 53


def test_settings_validation_and_lines():
    assert FanSettings().config_line() is None
    assert FanSettings(mode="manual", manual_percent=45).config_line() == "manual 45"
    assert FanSettings(mode="target", target_celsius=60).config_line() == "target 60"
    assert FanSettings(mode="curve", curve="max").config_line() == "curve 35 70 50 90 55 100"
    for bad in ({"mode": "turbo"}, {"curve": "silent"}, {"manual_percent": 20}, {"manual_percent": 101},
                {"target_celsius": 40}, {"target_celsius": 90}, {"manual_percent": True}, {"target_celsius": 55.5}):
        with pytest.raises(ValueError):
            FanSettings.from_dict(bad)


def test_store_round_trip_and_unreadable_file(tmp_path):
    store = FanStore(tmp_path / "fans.json")
    assert store.load() == FanSettings()
    store.save(FanSettings(mode="target", target_celsius=58))
    assert store.load() == FanSettings(mode="target", target_celsius=58)
    (tmp_path / "fans.json").write_text("{not json", "utf-8")
    assert store.load() == FanSettings()
    (tmp_path / "fans.json").write_text(json.dumps({"mode": "manual", "manual_percent": 5}), "utf-8")
    assert store.load() == FanSettings()


def test_parse_reading():
    output = ("R 179 179 4300 4410 55250 49000 50750 73125\nD T\nC 1 abc\n"
              "S 1234 target 70 4300 4410 55250 49000 50750 73125 target\nF target 55\n")
    reading = parse_reading(output)
    assert reading["cpu_celsius"] == 55.2 and reading["port_celsius"] == 73.1
    assert reading["fans"] == [{"rpm": 4300, "percent": 70}, {"rpm": 4410, "percent": 70}]
    assert reading["daemon"] == "paused" and reading["controller"] == "running"
    assert reading["duty"] == 70 and reading["reason"] == "target" and reading["config"] == "target 55"
    stock = parse_reading("R 107 107 2044 2072 71750 65750 66000 92732\nD S\nC absent\n")
    assert stock["controller"] == "absent" and stock["daemon"] == "running" and "duty" not in stock
    assert parse_reading("R NA 107 0 0 NA 0 0 0\nD none\nC absent\n")["cpu_celsius"] is None
    with pytest.raises(TransportError):
        parse_reading("R 1 2\n")


async def test_control_applies_saves_and_restores(tmp_path):
    store = FanStore(tmp_path / "fans.json")
    control = FanControl(SimulatedFans(), store, supported=True)
    await control.update({"mode": "curve", "curve": "high"})
    assert control.reading["controller"] == "running"
    assert store.load() == FanSettings(mode="curve", curve="high")
    # A switch restart drops the controller; the next poll restores it.
    await control.backend.stop()
    await control.poll()
    assert control.reading["config"] == FanSettings(mode="curve", curve="high").config_line()
    await control.update({"mode": "switch"})
    assert control.reading["controller"] == "absent"
    with pytest.raises(ValueError):
        await control.update({"mode": "manual", "manual_percent": 10})
    assert control.settings.mode == "switch"


async def test_unsupported_switch_rejects_changes():
    control = FanControl(SimulatedFans(), None, supported=False)
    with pytest.raises(ValueError, match="USW-Pro-XG-8-PoE"):
        await control.update({"mode": "manual"})
    await control.poll()
    assert control.reading is None and control.to_dict()["supported"] is False


class FailingBackend(SimulatedFans):
    async def apply(self, line):
        raise TransportError("did not confirm")


async def test_failed_apply_keeps_previous_setting_and_backs_off(tmp_path):
    control = FanControl(FailingBackend(), FanStore(tmp_path / "fans.json"), supported=True)
    with pytest.raises(TransportError):
        await control.update({"mode": "manual", "manual_percent": 80})
    assert control.settings.mode == "switch" and control.error == "did not confirm"
    control.settings = FanSettings(mode="manual")
    await control.poll()
    assert control._retry_at > time.monotonic()


# The shell controller must compute exactly what the Python step does.

def _cases():
    rng = random.Random(4)
    lines = [FanSettings(mode="curve", curve=name).config_line() for name in CURVES]
    lines += ["manual 30", "manual 64", "manual 100", "target 45", "target 55", "target 85"]
    cases = []
    for line in lines:
        for _ in range(5):
            cases.append((rng.randrange(30_000, 112_000, 125), rng.randrange(30_000, 80_000, 250),
                          rng.randrange(50_000, 115_000, 7), rng.randrange(30, 101),
                          rng.randrange(-60_000, 80_000), line))
    cases.append((55_000, 40_000, 0, 50, 70_000 * 2, "target 55"))
    return cases


@needs_shell
def test_shell_step_matches_python():
    script = fans.SCRIPT.as_posix()
    for cpu, sfp, port, last, integral, line in _cases():
        result = shell(script, "compute", str(cpu), str(sfp), str(port), str(last), str(integral), *line.split())
        assert result.returncode == 0, result.stderr
        duty, new_integral, why = step(cpu, sfp, port, last, integral, line.split())
        assert result.stdout.split() == [str(duty), str(new_integral), why], (cpu, sfp, port, last, integral, line)


@needs_shell
def test_shell_rejects_bad_config():
    assert shell(fans.SCRIPT.as_posix(), "compute", "50000", "40000", "70000", "50", "0", "fast").returncode == 1


# A mock of the switch's sensor files, fan outputs and fan daemon.

def put(path, value):
    path.write_text(value + "\n", newline="\n")


@pytest.fixture
def mock_switch(tmp_path):
    hw = tmp_path / "hw"
    phy = tmp_path / "phy"
    bin_dir = tmp_path / "bin"
    for folder in (hw, phy, bin_dir):
        folder.mkdir()
    values = {"name": "adt7475", "pwm1": "107", "pwm2": "107", "pwm1_enable": "1", "pwm2_enable": "1",
              "temp1_input": "60000", "temp2_input": "54000", "temp3_input": "55000",
              "fan1_input": "2000", "fan2_input": "2050"}
    for name, value in values.items():
        put(hw / name, value)
    for i in range(1, 9):
        put(phy / str(i), "80000")
    put(tmp_path / "system.info", "systemid=ed76")
    # Started from a shell: MSYS on Windows cannot pause a process whose parent is not an MSYS process.
    start = "nohup sh -c 'echo $$ > daemon.pid; exec sleep 300' </dev/null >/dev/null 2>&1 &\n"
    subprocess.run([SHELL, "-s"], input=start.encode(), cwd=tmp_path, capture_output=True, env=BASE_ENV, timeout=20)
    for _ in range(100):
        if (tmp_path / "daemon.pid").exists() and (tmp_path / "daemon.pid").read_text().strip():
            break
        time.sleep(0.05)
    pid = (tmp_path / "daemon.pid").read_text().strip()
    (bin_dir / "pidof").write_text(f"#!/bin/sh\n[ \"$1\" = ubnt-fanctrl ] && echo {pid}\n", newline="\n")
    os.chmod(bin_dir / "pidof", 0o755)
    env = {**BASE_ENV, "EE_FAN_HW": hw.as_posix(), "EE_FAN_PHY": phy.as_posix(), "EE_FAN_TICK": "1",
           "EE_FAN_SYSINFO": (tmp_path / "system.info").as_posix()}
    path = subprocess.run([SHELL, "-c", f'cd "{bin_dir.as_posix()}" && pwd'], capture_output=True, text=True)
    env["EE_TEST_BIN"] = path.stdout.strip()
    yield {"hw": hw, "phy": phy, "env": env, "daemon": pid, "root": tmp_path}
    shell("-c", f"kill -CONT {pid} 2>/dev/null; kill {pid} 2>/dev/null")


def _daemon_state(pid):
    return shell("-c", f"awk '/^State:/{{print $2}}' /proc/{pid}/status").stdout.strip()


def _wait(predicate, seconds=15):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        if predicate():
            return True
        time.sleep(0.2)
    return False


class LocalTransport:
    """Runs SwitchFans commands with a local shell against the mock switch."""

    def __init__(self, switch):
        self.switch = switch

    async def run(self, command, *, channel="main", timeout=10.0):
        # Windows shells do not report folder permissions reliably; the switch check is shared with lighting.
        command = command.replace(fans._CHECK_DIRECTORY, '[ ! -L "$d" ] && [ -d "$d" ]')
        # Commands go through standard input, like the SSH channel, which avoids Windows argument quoting.
        command = f'PATH="$EE_TEST_BIN:$PATH"\n{command}\n'
        # Bytes keep line endings as sent; text mode on Windows would add carriage returns.
        result = await asyncio.to_thread(subprocess.run, [SHELL, "-s"], input=command.encode(), capture_output=True,
                                         env=self.switch["env"], timeout=timeout + 10)
        return result.stdout.decode()


def backend_for(switch, location):
    return SwitchFans(LocalTransport(switch), location=location, hw=switch["hw"].as_posix(),
                      phy=switch["phy"].as_posix())


@needs_shell
async def test_switch_fans_end_to_end(mock_switch):
    location = f"/tmp/ee-fan-test-{os.getpid()}-{random.randrange(1 << 30)}"
    backend = backend_for(mock_switch, location)
    reading = await backend.read()
    assert reading["controller"] == "absent" and reading["fans"][0]["percent"] == 42
    reading = await backend.apply("manual 70")
    assert reading["controller"] == "running" and reading["config"] == "manual 70"
    assert (mock_switch["hw"] / "pwm1").read_text().strip() == "179"
    assert (mock_switch["hw"] / "pwm2").read_text().strip() == "179"
    assert _daemon_state(mock_switch["daemon"]) == "T"
    # A running controller picks up a new setting without restarting.
    # A target well below the current temperature runs the fans at full speed right away.
    put(mock_switch["hw"] / "temp1_input", "74000")
    reading = await backend.apply("target 55")
    assert reading["config"] == "target 55" and reading["reason"] == "target"
    assert (mock_switch["hw"] / "pwm1").read_text().strip() == "255"
    await backend.stop()
    reading = await backend.read()
    assert reading["controller"] == "absent"
    assert _daemon_state(mock_switch["daemon"]) != "T"


@needs_shell
async def test_guard_returns_fans_when_controller_is_killed(mock_switch):
    location = f"/tmp/ee-fan-test-{os.getpid()}-{random.randrange(1 << 30)}"
    backend = backend_for(mock_switch, location)
    try:
        await backend.apply("manual 90")
        assert _daemon_state(mock_switch["daemon"]) == "T"
        shell("-c", f'kill -9 "$(cat {location}/pid)"', env=mock_switch["env"])
        assert _wait(lambda: _daemon_state(mock_switch["daemon"]) != "T")
        reading = await backend.read()
        assert reading["controller"] == "stopped"
    finally:
        await backend.stop()


@needs_shell
async def test_controller_releases_on_bad_sensor(mock_switch):
    location = f"/tmp/ee-fan-test-{os.getpid()}-{random.randrange(1 << 30)}"
    backend = backend_for(mock_switch, location)
    try:
        await backend.apply("manual 40")
        put(mock_switch["hw"] / "temp1_input", "garbage")
        assert _wait(lambda: (mock_switch["hw"] / "pwm1").read_text().strip() == "255", 6)
        assert _wait(lambda: _daemon_state(mock_switch["daemon"]) != "T", 15)
    finally:
        await backend.stop()


@needs_shell
async def test_unsupported_model_is_refused(mock_switch):
    put(mock_switch["root"] / "system.info", "systemid=ed72")
    location = f"/tmp/ee-fan-test-{os.getpid()}-{random.randrange(1 << 30)}"
    backend = backend_for(mock_switch, location)
    with pytest.raises(ValueError, match="not supported"):
        await backend.apply("manual 40")
    await backend.stop()
    assert _daemon_state(mock_switch["daemon"]) != "T"
