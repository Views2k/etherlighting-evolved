"""Fan control: the shared control step, settings, the simulator, and the switch-side shell commands."""
from __future__ import annotations

import asyncio
import contextlib
import json
import os
import random
import re
import shutil
import subprocess
import time
from dataclasses import asdict
from pathlib import Path

import pytest

from etherlighting_evolved import fans
from etherlighting_evolved.fans import (
    CURVES,
    OWNER,
    SAVE_FAILED,
    STOPPING,
    DeviceFanStore,
    FanControl,
    FanSettings,
    FanSettingsError,
    FanSettingsSaveError,
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


def test_a_new_target_starts_near_its_holding_speed_not_full_speed():
    # 74 C at an average of 42%: holding 55 C takes about 78%, and the boost adds at most 5%.
    integral = fans.start_integral(74_000, 420, 55)
    duty = step(74_000, 60_000, 80_000, 42, integral, ["target", "55"])[0]
    assert 75 <= duty <= 90
    # Already at the target: about the current speed.
    assert 67 <= step(55_000, 50_000, 80_000, 70, fans.start_integral(55_000, 700, 55), ["target", "55"])[0] <= 72
    # A target the fans cannot reach runs them at full speed.
    assert fans.estimate(74_000, 1000, 40) == 100


def test_a_new_curve_or_speed_takes_effect_at_once():
    low = FanSettings(mode="curve", curve="low").config_line().split()
    # Within one setting the speed eases down 2% per step as the switch cools...
    assert step(60_000, 40_000, 70_000, 100, 0, low)[0] == 98
    # ...but a new setting is passed with last 0 and goes straight to the curve.
    assert step(60_000, 40_000, 70_000, 0, 0, low)[0] == 33


def _hour(sim):
    history = []
    for _ in range(720):
        sim.updated -= fans.POLL_INTERVAL
        sim._advance()
        history.append((sim.cpu, sim.duty))
    return history


def test_target_settles_without_a_trip_to_full_speed():
    sim = SimulatedFans(ambient=15, clock=lambda: 0)
    sim.cpu, sim.duty, sim.average = 71.0, 42, 420
    sim.line = "target 55"
    sim.integral = fans.start_integral(71_000, 420, 55)
    history = _hour(sim)
    late = history[360:]
    assert all(abs(cpu - 55) < 1.0 for cpu, _ in late)
    assert min(d for _, d in late) >= 60
    assert max(d for _, d in late) <= 80
    assert max(d for _, d in history) <= 82, "the fans should not run far above their holding speed"
    assert min(cpu for cpu, _ in history) > 53


async def test_target_after_full_speed_starts_from_the_holding_speed():
    """Choosing a target right after Manual 100% does not start the target mode at 100%."""
    now = [0.0]
    sim = SimulatedFans(ambient=15, clock=lambda: now[0])
    await sim.apply("manual 60")
    for _ in range(60):  # settle at 60% for five minutes
        now[0] += fans.POLL_INTERVAL
        sim._advance()
    await sim.apply("manual 100")
    reading = await sim.apply("target 60")
    assert reading["duty"] < 75
    history = _hour(sim)
    assert max(d for _, d in history) < 80
    assert all(abs(cpu - 60) < 1.0 for cpu, _ in history[360:])
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


def test_store_round_trip(tmp_path):
    store = FanStore(tmp_path / "fans.json")
    assert store.load() == FanSettings()
    store.save(FanSettings(mode="target", target_celsius=58))
    assert store.load() == FanSettings(mode="target", target_celsius=58)


@pytest.mark.parametrize("content", ["{not json", "[]", ""])
def test_unreadable_settings_are_moved_aside(tmp_path, caplog, content):
    path = tmp_path / "fans.json"
    path.write_text(content, "utf-8")
    with pytest.raises(FanSettingsError, match="could not be read"):
        FanStore(path).load()
    assert not path.exists()
    [moved] = tmp_path.glob("fans.json.corrupt-*")
    assert moved.read_text("utf-8") == content
    assert str(moved) in caplog.text


@pytest.mark.parametrize("content", [{"mode": "manual", "manual_percent": 5}, {"curve": []}, {"mode": "auto"}])
def test_settings_this_release_cannot_use_stay_in_place(tmp_path, caplog, content):
    # A newer release may save a mode or value this one does not know; that release can still read it later.
    path = tmp_path / "fans.json"
    path.write_text(json.dumps(content), "utf-8")
    with pytest.raises(FanSettingsError):
        FanStore(path).load()
    assert json.loads(path.read_text("utf-8")) == content
    assert not list(tmp_path.glob("fans.json.corrupt-*"))
    assert str(path) in caplog.text


async def test_lost_fan_choice_stays_reported_until_a_new_one_is_saved(tmp_path):
    path = tmp_path / "fans.json"
    path.write_text("{not json", "utf-8")
    backend = SimulatedFans()
    await backend.apply("manual 40")
    control = FanControl(backend, FanStore(path), supported=True)
    assert control.settings == FanSettings()
    message = "Saved fan settings could not be read. The switch controls the fans until you choose a mode."
    assert control.to_dict()["settings_error"] == message
    # The switch takes the fans back, which clears error but not the report of the lost choice.
    await control.poll()
    assert control.reading["controller"] == "absent"
    assert control.to_dict()["error"] is None and control.to_dict()["settings_error"] == message
    await control.update({"mode": "manual", "manual_percent": 50})
    assert control.to_dict()["settings_error"] is None
    assert FanStore(path).load() == FanSettings(mode="manual", manual_percent=50)


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
    async def apply(self, line, owner=None):
        raise TransportError("did not confirm")


async def test_failed_apply_keeps_previous_setting_and_backs_off(tmp_path):
    control = FanControl(FailingBackend(), FanStore(tmp_path / "fans.json"), supported=True)
    with pytest.raises(TransportError):
        await control.update({"mode": "manual", "manual_percent": 80})
    assert control.settings.mode == "switch" and control.error == "did not confirm"
    control.settings = FanSettings(mode="manual")
    await control.poll()
    assert control._retry_at > time.monotonic()


# Fan settings saved per switch, the owner token on the switch, and the hold.

A, B = "a" * 64, "b" * 64
CURVE_LOW = FanSettings(mode="curve", curve="low")
CURVE_HIGH = FanSettings(mode="curve", curve="high")


class CountingFans(SimulatedFans):
    """Counts the reads and the commands that would change fan control on the switch."""

    def __init__(self, **kwargs):
        super().__init__(**kwargs)
        self.reads = self.applied = self.stopped = 0

    async def read(self):
        self.reads += 1
        return await super().read()

    async def apply(self, line, owner=None):
        self.applied += 1
        return await super().apply(line, owner)

    async def stop(self):
        self.stopped += 1
        await super().stop()


class StoppedFans(CountingFans):
    """Another installation's controller that stopped, leaving its directory and the paused daemon behind."""

    def __init__(self, **kwargs):
        super().__init__(**kwargs)
        self.left = True

    async def read(self):
        reading = await super().read()
        if self.left:
            reading.update(controller="stopped", daemon="paused", owner="c" * 48)
        return reading

    async def stop(self):
        self.left = False
        await super().stop()


def fan_file(root, identity=A):
    return root / "device-fans" / f"{identity}.json"


def envelope(**changes):
    return {"version": 1, "identity": A, "owner": "d" * 48, "settings": asdict(CURVE_LOW), **changes}


def write(path, content):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(content if isinstance(content, str) else json.dumps(content), "utf-8")
    return path


def write_legacy(root, settings):
    return write(root / "fans.json", asdict(settings))


def test_device_store_round_trip_keeps_owner(tmp_path):
    with pytest.raises(ValueError):
        DeviceFanStore(fan_file(tmp_path), "A" * 64)
    path = fan_file(tmp_path)
    store = DeviceFanStore(path, A)
    assert store.load() == FanSettings() and store.owner is None and store.hold is False
    store.save(CURVE_HIGH)
    saved = json.loads(path.read_text("utf-8"))
    assert saved["version"] == 1 and saved["identity"] == A and saved["settings"] == asdict(CURVE_HIGH)
    assert OWNER.fullmatch(saved["owner"])
    again = DeviceFanStore(path, A)
    assert again.load() == CURVE_HIGH and again.owner == saved["owner"]
    again.save(FanSettings(mode="manual", manual_percent=45))
    assert json.loads(path.read_text("utf-8"))["owner"] == saved["owner"]


BAD_FILES = {
    "invalid JSON": "{not json",
    "version 0": envelope(version=0),
    "version true": envelope(version=True),
    "another identity": envelope(identity=B),
    "47-character owner": envelope(owner="d" * 47),
    "uppercase owner": envelope(owner="D" * 48),
    "missing owner": {key: value for key, value in envelope().items() if key != "owner"},
    "manual_percent 5": envelope(settings={"mode": "manual", "manual_percent": 5}),
    "settings as a list": envelope(settings=[]),
}


@pytest.mark.parametrize("content", BAD_FILES.values(), ids=BAD_FILES.keys())
async def test_device_store_holds_on_bad_files(tmp_path, content):
    for name, owner in (("ownerless", None), ("owned", "b" * 48)):
        path = write(fan_file(tmp_path / name), content)
        data = path.read_text("utf-8")
        backend = CountingFans()
        await backend.apply(CURVE_HIGH.config_line(), owner)
        backend.applied = 0
        store = DeviceFanStore(path, A)
        control = FanControl(backend, store, supported=True)
        assert control.settings == FanSettings() and store.owner is None and store.hold is True
        await control.poll()
        assert (backend.applied, backend.stopped) == (0, 0)
        assert control.to_dict()["hold"] == "settings_unreadable" and control.to_dict()["settings_error"]
        if isinstance(content, str):
            # A file that holds no JSON object is set aside with its content.
            [moved] = path.parent.glob(f"{A}.json.corrupt-*")
            assert not path.exists() and moved.read_text("utf-8") == data
        else:
            assert path.read_text("utf-8") == data


async def test_a_newer_fan_file_is_never_overwritten(tmp_path):
    path = write(fan_file(tmp_path), envelope(version=2, added_by_a_newer_release=True))
    data = path.read_text("utf-8")
    backend = CountingFans()
    await backend.apply(CURVE_HIGH.config_line())
    backend.applied = 0
    control = FanControl(backend, DeviceFanStore(path, A), supported=True)
    await control.poll()
    state = control.to_dict()
    assert state["hold"] == "settings_newer" and "newer version of EE" in state["settings_error"]
    # Switch control too: it would have to be saved over the newer file.
    for change in ({"mode": "manual", "manual_percent": 50}, {"mode": "switch"}):
        with pytest.raises(ValueError, match="newer version of EE"):
            await control.update(change)
    assert (backend.applied, backend.stopped) == (0, 0) and backend.line == CURVE_HIGH.config_line()
    assert path.read_text("utf-8") == data and control.to_dict()["supported"]
    assert await control.release(connected=True) == "returned" and backend.line is None
    assert path.read_text("utf-8") == data


async def test_hold_still_cleans_a_stopped_controller(tmp_path):
    backend = StoppedFans()
    control = FanControl(backend, DeviceFanStore(write(fan_file(tmp_path), envelope(identity=B)), A), supported=True)
    await control.poll()
    assert backend.stopped == 1
    reading = await backend.read()
    assert reading["controller"] == "absent" and reading["daemon"] != "paused"


async def test_switch_control_cleans_a_stopped_foreign_directory(tmp_path):
    DeviceFanStore(fan_file(tmp_path), A).save(FanSettings())
    backend = StoppedFans()
    control = FanControl(backend, DeviceFanStore(fan_file(tmp_path), A), supported=True)
    assert control.to_dict()["hold"] is None
    await control.poll()
    assert backend.stopped == 1 and control.to_dict()["hold"] is None


def test_legacy_fans_json_moves_to_the_first_switch_only(tmp_path):
    legacy = write_legacy(tmp_path, CURVE_HIGH)
    original = legacy.read_bytes()
    first = DeviceFanStore(fan_file(tmp_path, A), A)
    first.adopt_legacy(legacy)
    saved = json.loads(first.path.read_text("utf-8"))
    assert saved["settings"] == asdict(CURVE_HIGH) and OWNER.fullmatch(saved["owner"]) and not first.hold
    assert legacy.read_bytes() == original
    second = DeviceFanStore(fan_file(tmp_path, B), B)
    second.adopt_legacy(legacy)
    assert not second.path.exists() and second.load() == FanSettings()
    write_legacy(tmp_path, FanSettings(mode="manual", manual_percent=90))
    for identity in (A, B):
        DeviceFanStore(fan_file(tmp_path, identity), identity).adopt_legacy(legacy)
    assert DeviceFanStore(first.path, A).load() == CURVE_HIGH and not second.path.exists()


def test_a_fan_file_set_aside_still_ends_the_migration(tmp_path):
    legacy = write_legacy(tmp_path, CURVE_HIGH)
    # The temporary file of an interrupted save does not count.
    write(fan_file(tmp_path).with_name(f".{A}.json.x1y2.tmp"), "{}")
    first = DeviceFanStore(fan_file(tmp_path, A), A)
    first.adopt_legacy(legacy)
    assert first.load() == CURVE_HIGH
    write(first.path, "{not json")
    with pytest.raises(FanSettingsError):
        DeviceFanStore(first.path, A).load()
    second = DeviceFanStore(fan_file(tmp_path, B), B)
    second.adopt_legacy(legacy)
    assert not first.path.exists() and not second.path.exists()


def test_legacy_invalid_file_migrates_as_switch_control(tmp_path):
    legacy = write(tmp_path / "fans.json", "{not json")
    store = DeviceFanStore(fan_file(tmp_path), A)
    store.adopt_legacy(legacy)
    assert json.loads(store.path.read_text("utf-8"))["settings"]["mode"] == "switch"
    assert legacy.read_text("utf-8") == "{not json"


async def test_legacy_read_error_does_not_migrate_or_touch_the_switch(tmp_path, monkeypatch):
    legacy = write_legacy(tmp_path, CURVE_HIGH)
    read_bytes = Path.read_bytes

    def refuse(self):
        if self == legacy:
            raise PermissionError("fixture: no access")
        return read_bytes(self)

    with monkeypatch.context() as patch:
        patch.setattr(Path, "read_bytes", refuse)
        store = DeviceFanStore(fan_file(tmp_path), A)
        store.adopt_legacy(legacy)
        assert store.hold and not store.path.exists() and legacy.exists()
        backend = CountingFans()
        await backend.apply(CURVE_HIGH.config_line())
        backend.applied = 0
        control = FanControl(backend, store, supported=True)
        await control.poll()
        assert (backend.applied, backend.stopped) == (0, 0)
        assert control.to_dict()["hold"] == "settings_unreadable"
    retry = DeviceFanStore(fan_file(tmp_path), A)
    retry.adopt_legacy(legacy)
    assert retry.load() == CURVE_HIGH and not retry.hold


def test_legacy_listing_error_does_not_migrate(tmp_path, monkeypatch):
    legacy = write_legacy(tmp_path, CURVE_HIGH)
    folder = fan_file(tmp_path).parent
    folder.mkdir()
    iterdir, read_bytes, reads = Path.iterdir, Path.read_bytes, []

    def refuse(self):
        if self == folder:
            raise PermissionError("fixture: no listing")
        return iterdir(self)

    def counting(self):
        reads.append(self)
        return read_bytes(self)

    store = DeviceFanStore(fan_file(tmp_path), A)
    with monkeypatch.context() as patch:
        patch.setattr(Path, "iterdir", refuse)
        patch.setattr(Path, "read_bytes", counting)
        store.adopt_legacy(legacy)
    assert store.hold and legacy not in reads and not list(folder.iterdir())


def test_legacy_save_failure_holds_and_retries(tmp_path, monkeypatch):
    legacy = write_legacy(tmp_path, CURVE_HIGH)

    def fail(self, settings):
        raise OSError("fixture: the disk is full")

    store = DeviceFanStore(fan_file(tmp_path), A)
    with monkeypatch.context() as patch:
        patch.setattr(DeviceFanStore, "save", fail)
        store.adopt_legacy(legacy)
    assert store.hold and not store.path.exists() and legacy.exists()
    retry = DeviceFanStore(fan_file(tmp_path), A)
    retry.adopt_legacy(legacy)
    assert retry.load() == CURVE_HIGH


def test_parse_reading_reads_the_owner_line():
    reading = ("R 179 179 4300 4410 55250 49000 50750 73125\nD T\nC 1 abc\n"
               "S 1234 manual 70 4300 4410 55250 49000 50750 73125 manual\nF manual 70\n")
    assert parse_reading(reading + f"O {'a' * 48}\n")["owner"] == "a" * 48
    for line in ("O \n", "O junk\n", f"O {'a' * 49}\n", ""):
        assert parse_reading(reading + line)["owner"] is None, line


async def test_check_leaves_another_installations_controller(tmp_path):
    for settings in (FanSettings(), CURVE_LOW):
        backend = CountingFans()
        await backend.apply("manual 50", owner="a" * 48)
        backend.applied = 0
        path = fan_file(tmp_path / settings.mode)
        DeviceFanStore(path, A).save(settings)
        control = FanControl(backend, DeviceFanStore(path, A), supported=True)
        await control.poll()
        assert backend.line == "manual 50" and (backend.applied, backend.stopped) == (0, 0)
        state = control.to_dict()
        assert state["hold"] == "other_installation" and "owner" not in state["reading"]


async def test_check_adopts_a_controller_without_owner(tmp_path):
    medium = FanSettings(mode="curve", curve="medium")
    backend = CountingFans()
    await backend.apply(medium.config_line())
    backend.applied = 0
    DeviceFanStore(fan_file(tmp_path, A), A).save(medium)
    control = FanControl(backend, DeviceFanStore(fan_file(tmp_path, A), A), supported=True)
    await control.poll()
    assert backend.applied == 0 and backend.line == medium.config_line() and control.to_dict()["hold"] is None
    DeviceFanStore(fan_file(tmp_path, B), B).save(FanSettings())
    control = FanControl(backend, DeviceFanStore(fan_file(tmp_path, B), B), supported=True)
    await control.poll()
    assert backend.stopped == 1 and backend.line is None


async def test_explicit_change_takes_over_and_saves_owner(tmp_path):
    backend = CountingFans()
    await backend.apply("manual 50", owner="a" * 48)
    store = DeviceFanStore(fan_file(tmp_path), A)
    control = FanControl(backend, store, supported=True)
    await control.poll()
    assert control.to_dict()["hold"] == "other_installation"
    await control.update({"mode": "curve", "curve": "low"})
    assert backend.line == CURVE_LOW.config_line() and backend.owner == store.owner and OWNER.fullmatch(store.owner)
    saved = json.loads(store.path.read_text("utf-8"))
    assert saved["owner"] == store.owner and saved["settings"] == asdict(CURVE_LOW)
    assert control.to_dict()["hold"] is None
    await backend.apply("manual 60", owner="b" * 48)
    await control.update({"mode": "switch"})
    assert backend.line is None


async def test_failed_change_in_a_hold_keeps_the_file_and_the_hold(tmp_path):
    path = write(fan_file(tmp_path), envelope(identity=B))
    data = path.read_bytes()
    control = FanControl(FailingBackend(), DeviceFanStore(path, A), supported=True)
    with pytest.raises(TransportError):
        await control.update({"mode": "curve", "curve": "low"})
    assert path.read_bytes() == data and control.to_dict()["hold"] == "settings_unreadable"


async def test_save_failure_after_confirm_keeps_new_setting(tmp_path, monkeypatch):
    backend = CountingFans()
    store = DeviceFanStore(fan_file(tmp_path), A)
    control = FanControl(backend, store, supported=True)

    def fail(settings):
        raise OSError("fixture: the disk is full")

    with monkeypatch.context() as patch:
        patch.setattr(store, "save", fail)
        with pytest.raises(FanSettingsSaveError):
            await control.update({"mode": "manual", "manual_percent": 50})
        assert control.settings == FanSettings(mode="manual", manual_percent=50) and backend.line == "manual 50"
        assert control.to_dict()["error"] == SAVE_FAILED
        backend.applied = 0
        await control.poll()
        assert backend.applied == 0 and control.to_dict()["error"] == SAVE_FAILED
    await control.update({"manual_percent": 55})
    assert control.to_dict()["error"] is None


async def test_release_returns_fans_and_keeps_saved_mode(tmp_path):
    backend = CountingFans()
    store = DeviceFanStore(fan_file(tmp_path), A)
    control = FanControl(backend, store, supported=True)
    await control.update({"mode": "curve", "curve": "medium"})
    data = store.path.read_bytes()
    assert await control.release(connected=True) == "returned" and backend.line is None
    assert DeviceFanStore(store.path, A).load() == FanSettings(mode="curve", curve="medium")
    assert store.path.read_bytes() == data
    backend.reads = backend.applied = backend.stopped = 0
    await control.poll()
    assert (backend.reads, backend.applied, backend.stopped) == (0, 0, 0)
    with pytest.raises(TransportError, match=re.escape(STOPPING)):
        await control.update({"mode": "manual"})


class FailingStop(CountingFans):
    async def stop(self):
        raise TransportError("fixture: the switch did not answer")


class Unreadable(CountingFans):
    failing = False

    async def read(self):
        if self.failing:
            raise TransportError("fixture: the switch did not answer")
        return await super().read()


async def test_release_results():
    backend = CountingFans()
    assert await FanControl(backend, None, supported=True).release(connected=True) is None and backend.stopped == 0

    backend = CountingFans()
    await backend.apply("manual 50", owner="a" * 48)
    assert await FanControl(backend, None, supported=True).release(connected=True) == "returned"
    assert backend.line is None

    backend = FailingStop()
    await backend.apply("manual 50")
    assert await FanControl(backend, None, supported=True).release(connected=True) == "unconfirmed"

    backend = CountingFans()
    control = FanControl(backend, None, supported=True)
    await control.update({"mode": "curve"})
    backend.reads = backend.stopped = 0
    assert await control.release(connected=False) == "unconfirmed" and (backend.reads, backend.stopped) == (0, 0)

    control = FanControl(CountingFans(), None, supported=True)
    await control.poll()
    assert await control.release(connected=False) is None
    assert await FanControl(CountingFans(), None, supported=True).release(connected=False) == "unconfirmed"

    # A failed reading clears the reading, but not what the last one showed.
    backend = Unreadable()
    control = FanControl(backend, None, supported=True)
    await control.poll()
    backend.failing = True
    with pytest.raises(TransportError):
        await control.poll()
    assert control.reading is None
    assert await control.release(connected=True) is None

    assert await FanControl(CountingFans(), None, supported=False).release(connected=True) is None


async def test_release_waits_for_a_change_in_progress():
    class Blocking(SimulatedFans):
        def __init__(self):
            super().__init__()
            self.entered, self.proceed = asyncio.Event(), asyncio.Event()

        async def apply(self, line, owner=None):
            self.entered.set()
            await self.proceed.wait()
            return await super().apply(line, owner)

    backend = Blocking()
    control = FanControl(backend, None, supported=True)
    change = asyncio.create_task(control.update({"mode": "manual", "manual_percent": 70}))
    await backend.entered.wait()
    release = asyncio.create_task(control.release(connected=True))
    await asyncio.sleep(0.01)
    assert not release.done()
    backend.proceed.set()
    await change
    assert await release == "returned" and backend.line is None


async def test_release_times_out_as_unconfirmed(monkeypatch):
    class Slow(SimulatedFans):
        async def read(self):
            await asyncio.sleep(1)
            return await super().read()

    monkeypatch.setattr(fans, "RELEASE_TIMEOUT", 0.05)
    started = time.monotonic()
    assert await FanControl(Slow(), None, supported=True).release(connected=True) == "unconfirmed"
    assert time.monotonic() - started < 0.5


def test_release_budget_fits_client_timeouts():
    assert fans.RELEASE_TIMEOUT <= 15 and fans.RELEASE_READ_TIMEOUT < fans.RELEASE_TIMEOUT, (
        "The desktop app, the browser and the private gateway give up on a request after 30 seconds, and the rest "
        "of the handoff needs time too.")


def test_fans_script_digest_is_pinned():
    assert SwitchFans(None).digest == "28f041a1d7fee452bbdf9b828722787e44e4807c38ceaabcd7ec64b48457e2df", (
        "A change to fans.sh restarts fan control once on every switch at its next check. Name it in the changelog, "
        "then update this digest.")


async def test_apply_rejects_a_malformed_owner(tmp_path):
    class Untouchable:
        async def run_isolated(self, *args, **kwargs):
            pytest.fail("a fan command was sent")

    with pytest.raises(TransportError, match="could not be prepared"):
        await SwitchFans(Untouchable()).apply("manual 50", owner="x")
    control = FanControl(SwitchFans(Untouchable()), DeviceFanStore(fan_file(tmp_path), A), supported=True)
    control.store.owner = "x"
    with pytest.raises(TransportError):
        await control.update({"mode": "manual", "manual_percent": 50})
    assert control.to_dict()["supported"]


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
def test_shell_estimate_matches_python():
    script = fans.SCRIPT.as_posix()
    for cpu, average, target in ((74_000, 420, 55), (55_000, 700, 55), (64_000, 610, 50), (50_000, 1000, 45),
                                 (45_000, 300, 85), (90_000, 1000, 50), (60_250, 555, 58)):
        result = shell(script, "estimate", str(cpu), str(average), str(target))
        assert result.returncode == 0, result.stderr
        assert result.stdout.split() == [str(fans.estimate(cpu, average, target))], (cpu, average, target)


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
    # EE's controller folder, by its real path: the controller finds itself through /proc/<pid>/cwd, which reports
    # the real path, and /tmp is not a real folder on every test machine. On the switch EE checks that it is.
    real = subprocess.run([SHELL, "-c", f'cd "{tmp_path.as_posix()}" && pwd -P'], capture_output=True, text=True,
                          env=BASE_ENV).stdout.strip()
    yield {"hw": hw, "phy": phy, "env": env, "daemon": pid, "root": tmp_path, "location": f"{real}/ee-fan-control-v1"}
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


def local_command(command):
    """Adapt two switch checks that Windows shells cannot satisfy; lighting uses the same checks on the switch.

    Windows shells do not report folder permissions reliably, and /tmp can resolve to another real path.
    """
    return (command.replace(fans._CHECK_DIRECTORY, '[ ! -L "$d" ] && [ -d "$d" ]')
            .replace(fans._TMP_CHECK, "cd /tmp || exit 1"))


class LocalTransport:
    """Runs SwitchFans commands with a local shell against the mock switch."""

    def __init__(self, switch):
        self.switch = switch

    async def run(self, command, *, channel="main", timeout=10.0):
        command = local_command(command)
        # Commands go through standard input, like the SSH channel, which avoids Windows argument quoting.
        command = f'PATH="$EE_TEST_BIN:$PATH"\n{command}\n'
        # Bytes keep line endings as sent; text mode on Windows would add carriage returns.
        result = await asyncio.to_thread(subprocess.run, [SHELL, "-s"], input=command.encode(), capture_output=True,
                                         env=self.switch["env"], timeout=timeout + 10)
        return result.stdout.decode()

    run_isolated = run


class ChannelTransport:
    """One long-running shell fed each command and an end marker, the way SSHTransport uses a switch channel.

    A command that leaves something attached to the channel, or that reads the channel's input, stalls here
    exactly as it would on the switch.
    """

    def __init__(self, switch, program, isolated):
        import queue
        import threading
        self.process = subprocess.Popen([program, "-s", "ee_test"], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=subprocess.STDOUT, env=switch["env"])
        self.lines = queue.Queue()
        self.sequence = 0
        # SSHTransport.run_isolated starts the marker on a new line; a transport without isolated shells ends a
        # command with "echo marker".
        self.isolated = isolated

        def pump():
            for line in iter(self.process.stdout.readline, b""):
                self.lines.put(line.decode())
            self.lines.put(None)

        threading.Thread(target=pump, daemon=True).start()
        self._send('PATH="$EE_TEST_BIN:$PATH"; export PATH', 10)

    def _send(self, command, timeout):
        import queue
        command = local_command(command)
        self.sequence += 1
        marker = f"__EE_DONE_{self.sequence}__"
        end = f"printf '\\n%s\\n' {marker}" if self.isolated else f"echo {marker}"
        self.process.stdin.write(f"{command}\n{end}\n".encode())
        self.process.stdin.flush()
        deadline = time.monotonic() + timeout
        output = []
        while True:
            try:
                line = self.lines.get(timeout=max(0.01, deadline - time.monotonic()))
            except queue.Empty:
                raise TransportError(f"the switch did not answer within {timeout:g}s: {command[:120]!r}") from None
            if line is None:
                raise TransportError("the switch closed the shell")
            if line.strip() == marker:
                text = "".join(output)
                return text[:-1] if self.isolated and text.endswith("\n") else text
            output.append(line)

    async def run_isolated(self, command, *, channel, timeout=10.0):
        return await asyncio.to_thread(self._send, command, timeout)

    def close(self):
        with contextlib.suppress(Exception):
            self.process.stdin.close()
        with contextlib.suppress(Exception):
            self.process.wait(timeout=10)


def backend_for(switch, location):
    return SwitchFans(LocalTransport(switch), location=location, hw=switch["hw"].as_posix(),
                      phy=switch["phy"].as_posix())


def _shell_named(name):
    if SHELL is None:
        return None
    sibling = Path(SHELL).with_name(name + (".exe" if os.name == "nt" else ""))
    return str(sibling) if sibling.is_file() else shutil.which(name)


@needs_shell
@pytest.mark.parametrize("isolated", [True, False])
@pytest.mark.parametrize("program", ["dash", "sh"])
async def test_switch_fans_over_one_long_running_shell(mock_switch, program, isolated):
    """The switch keeps one shell per channel; every fan command must return its end marker in time."""
    path = _shell_named(program)
    if path is None:
        pytest.skip(f"{program} is not installed")
    location = mock_switch["location"]
    channel = ChannelTransport(mock_switch, path, isolated)
    backend = SwitchFans(channel, location=location, hw=mock_switch["hw"].as_posix(), phy=mock_switch["phy"].as_posix())
    try:
        assert (await backend.read())["controller"] == "absent"
        reading = await backend.apply("manual 70")
        assert reading["controller"] == "running" and reading["config"] == "manual 70"
        assert (mock_switch["hw"] / "pwm1").read_text().strip() == "179"
        reading = await backend.apply("curve 45 30 60 50 70 75 80 100")
        assert reading["config"].startswith("curve")
        await backend.stop()
        assert (await backend.read())["controller"] == "absent"
        assert _daemon_state(mock_switch["daemon"]) != "T"
    finally:
        with contextlib.suppress(Exception):
            await backend.stop()
        channel.close()


@needs_shell
async def test_switch_fans_end_to_end(mock_switch):
    location = mock_switch["location"]
    backend = backend_for(mock_switch, location)
    reading = await backend.read()
    assert reading["controller"] == "absent" and reading["fans"][0]["percent"] == 42
    reading = await backend.apply("manual 70")
    assert reading["controller"] == "running" and reading["config"] == "manual 70"
    assert (mock_switch["hw"] / "pwm1").read_text().strip() == "179"
    assert (mock_switch["hw"] / "pwm2").read_text().strip() == "179"
    assert _daemon_state(mock_switch["daemon"]) == "T"
    # A running controller picks up a new setting without restarting.
    # A target well below the current temperature starts near its estimated holding speed, not at full speed.
    put(mock_switch["hw"] / "temp1_input", "74000")
    reading = await backend.apply("target 55")
    assert reading["config"] == "target 55" and reading["reason"] == "target"
    assert 190 <= int((mock_switch["hw"] / "pwm1").read_text()) < 255
    # From full speed, a new curve goes straight to its own speed: Low at 60 C is 33%.
    await backend.apply("manual 100")
    put(mock_switch["hw"] / "temp1_input", "60000")
    await backend.apply("curve " + " ".join(f"{t} {d}" for t, d in CURVES["low"]))
    assert (mock_switch["hw"] / "pwm1").read_text().strip() == str((33 * 255 + 50) // 100)
    await backend.stop()
    reading = await backend.read()
    assert reading["controller"] == "absent"
    assert _daemon_state(mock_switch["daemon"]) != "T"


@needs_shell
async def test_switch_fans_writes_the_owner_beside_the_setting(mock_switch):
    location = mock_switch["location"]
    backend = backend_for(mock_switch, location)

    def config():
        return shell("-c", f'cat "{location}/config"', env=mock_switch["env"]).stdout.splitlines()

    try:
        reading = await backend.apply("manual 70", owner="a" * 48)
        assert config() == ["manual 70", "owner " + "a" * 48]
        assert reading["owner"] == "a" * 48 and reading["config"] == "manual 70"
        assert (mock_switch["hw"] / "pwm1").read_text().strip() == "179"
        # Releases up to 0.5.1 read the whole file and use only its first line.
        earlier = fans._READ.replace(fans._CONFIG_READ, 'echo "F $(cat "$d/config" 2>/dev/null)"')
        assert parse_reading(await backend._run(backend.paths + earlier))["config"] == "manual 70"
        reading = await backend.apply("manual 40")
        assert config() == ["manual 40"] and reading["owner"] is None
        await backend.stop()
        assert (await backend.read())["controller"] == "absent"
        assert _daemon_state(mock_switch["daemon"]) != "T"
    finally:
        with contextlib.suppress(Exception):
            await backend.stop()


@needs_shell
async def test_guard_returns_fans_when_controller_is_killed(mock_switch):
    location = mock_switch["location"]
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
    location = mock_switch["location"]
    backend = backend_for(mock_switch, location)
    try:
        await backend.apply("manual 40")
        put(mock_switch["hw"] / "temp1_input", "garbage")
        assert _wait(lambda: (mock_switch["hw"] / "pwm1").read_text().strip() == "255", 6)
        assert _wait(lambda: _daemon_state(mock_switch["daemon"]) != "T", 15)
    finally:
        await backend.stop()


@needs_shell
async def test_sensor_fault_returns_the_fans_to_the_switch_and_backs_off(mock_switch):
    control = FanControl(backend_for(mock_switch, mock_switch["location"]), None, supported=True)
    try:
        await control.update({"mode": "manual", "manual_percent": 40})
        put(mock_switch["hw"] / "temp1_input", "garbage")
        assert _wait(lambda: _daemon_state(mock_switch["daemon"]) != "T", 15)
        # The restarted controller faults too; its earlier ./active line must not confirm the setting.
        await control.poll()
        assert control.error == "The switch did not confirm the fan setting. The fans are back on switch control."
        assert control._retry_at > time.monotonic()
        await control.poll()
        assert _daemon_state(mock_switch["daemon"]) != "T"
    finally:
        await control.backend.stop()


@needs_shell
def test_guard_never_kills_a_process_that_reuses_the_controller_pid(mock_switch):
    controller, elsewhere = mock_switch["root"] / "controller", mock_switch["root"] / "elsewhere"
    controller.mkdir()
    elsewhere.mkdir()
    start = "nohup sh -c 'echo $$ > other.pid; exec sleep 60' </dev/null >/dev/null 2>&1 &\n"
    subprocess.run([SHELL, "-s"], input=start.encode(), cwd=elsewhere, capture_output=True, env=BASE_ENV, timeout=20)
    assert _wait(lambda: (elsewhere / "other.pid").exists() and (elsewhere / "other.pid").read_text().strip(), 5)
    other = (elsewhere / "other.pid").read_text().strip()
    try:
        # A heartbeat far past the limit: a controller this old would be killed as hung.
        put(controller / "status", "1 manual 40")
        result = shell(fans.SCRIPT.as_posix(), "guard", other, env=mock_switch["env"], cwd=controller)
        assert result.returncode == 0
        assert (controller / "status").read_text().strip() == "released"
        assert shell("-c", f"kill -0 {other}").returncode == 0
    finally:
        shell("-c", f"kill {other} 2>/dev/null")


async def test_missing_fan_program_does_not_stop_a_lighting_login(config, tmp_path, monkeypatch):
    from test_session_restore import LocalTransport

    from etherlighting_evolved.session import SessionManager

    monkeypatch.setattr(fans, "SCRIPT", tmp_path / "missing" / "fans.sh")
    sessions = SessionManager(config, transport_factory=lambda **kwargs: LocalTransport())
    try:
        pending = (await sessions.connect("switch.invalid", "fixture", "fixture-only"))["pending_scene_choice"]
        info = await sessions.choose_scene(pending["connection_id"], "start_new", pending["legacy_revision"])
        assert info["logged_in"]
        control = sessions.engine.fans
        assert control.supported
        await control.poll()
        state = control.to_dict()
        assert not state["supported"] and "Reinstall EE" in state["unsupported_reason"]
        with pytest.raises(ValueError, match="Reinstall EE"):
            await control.update({"mode": "manual", "manual_percent": 50})
    finally:
        await sessions.close()


@needs_shell
async def test_unsupported_model_is_refused(mock_switch):
    put(mock_switch["root"] / "system.info", "systemid=ed72")
    location = mock_switch["location"]
    backend = backend_for(mock_switch, location)
    with pytest.raises(ValueError, match="not supported"):
        await backend.apply("manual 40")
    await backend.stop()
    assert _daemon_state(mock_switch["daemon"]) != "T"


# The isolated command path on the real SSH transport, with a fake connection.

@pytest.mark.parametrize("fault", ["timeout", "cancel", "eof"])
async def test_isolated_fan_command_failure_keeps_the_lighting_connection(fault):
    from test_health_integration import Connection, Process

    from etherlighting_evolved.transport import SSHTransport

    transport = SSHTransport("fixture.invalid")
    connection = transport._conn = Connection()
    led_process = Process()
    transport._channels["leds"] = type("Channel", (), {"process": led_process})()
    pending = asyncio.create_task(transport.run_isolated("sleep 60", channel="ee-fans", timeout=0.05))
    await connection.process.started.wait()
    if fault == "cancel":
        pending.cancel()
    elif fault == "eof":
        connection.process.replies.put_nowait("")
    with pytest.raises(asyncio.CancelledError if fault == "cancel" else TransportError):
        await pending
    assert transport._conn is connection and not connection.closed
    assert not led_process.closed and "leds" in transport._channels
    assert connection.process.closed and "ee-fans" not in transport._channels


async def test_isolated_command_returns_output_without_a_trailing_newline():
    from test_health_integration import Connection

    from etherlighting_evolved.transport import SSHTransport

    transport = SSHTransport("fixture.invalid")
    connection = transport._conn = Connection()
    pending = asyncio.create_task(transport.run_isolated("printf done", channel="ee-fans", timeout=5))
    await connection.process.started.wait()
    sent = connection.process.writes[0]
    marker = sent.rsplit(" ", 1)[1].strip()
    assert sent.startswith(r"printf done" + "\n" + r"printf '\n%s\n' __EE_DONE_")
    # The command printed no newline, so the marker's own leading newline ends that line.
    for line in ("done\n", f"{marker}\n"):
        connection.process.replies.put_nowait(line)
    assert await pending == "done"
    assert not connection.closed and not connection.process.closed


@needs_shell
async def test_running_controller_takes_a_new_setting_within_seconds(mock_switch):
    """With the normal 5-second step, a change still reaches the fans in about a second."""
    path = _shell_named("dash") or SHELL
    switch = {**mock_switch, "env": {**mock_switch["env"], "EE_FAN_TICK": "5"}}
    location = mock_switch["location"]
    channel = ChannelTransport(switch, path, True)
    backend = SwitchFans(channel, location=location, hw=switch["hw"].as_posix(), phy=switch["phy"].as_posix())
    try:
        await backend.apply("manual 70")
        await asyncio.sleep(0.5)
        started = time.monotonic()
        reading = await backend.apply("manual 40")
        elapsed = time.monotonic() - started
        assert reading["config"] == "manual 40"
        assert (switch["hw"] / "pwm1").read_text().strip() == "102"
        assert elapsed < 3, f"the change took {elapsed:.1f} s"
        # Stopping is as quick: the controller notices the request within a second.
        started = time.monotonic()
        await backend.stop()
        assert time.monotonic() - started < 6
    finally:
        with contextlib.suppress(Exception):
            await backend.stop()
        channel.close()


async def test_readings_refresh_faster_after_a_change(tmp_path):
    control = FanControl(SimulatedFans(), FanStore(tmp_path / "fans.json"), supported=True)
    assert control.interval() == fans.POLL_INTERVAL
    await control.update({"mode": "manual", "manual_percent": 50})
    assert control.interval() == fans.FAST_POLL_INTERVAL
