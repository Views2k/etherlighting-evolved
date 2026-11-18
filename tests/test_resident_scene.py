import hashlib
import json
import os
import shutil
import subprocess
from dataclasses import replace
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from etherlighting_evolved import device_playback as player
from etherlighting_evolved.color import Color
from etherlighting_evolved.effects import create_effect
from etherlighting_evolved.engine import Override, Zone
from etherlighting_evolved.hardware import PRO_XG8, PROFILES
from etherlighting_evolved.resident_scene import compile_scene_data
from etherlighting_evolved.sfp import SfpLight, SfpSync


def scene(profile, effect="static"):
    ports = profile.copper_ports + profile.optical_ports
    params = {"idle_color": "#000000", "active_color": "#ffffff"} if effect == "activity_glow" else {}
    return SimpleNamespace(
        switch=SimpleNamespace(hardware_profile=profile, native_breathing=True, sfp_precise=True),
        mode="running", brightness=100, gamma=1, max_commands=30, overrides={},
        sfp={port: SfpLight() for port in profile.optical_ports}, sfp_sync=SfpSync(),
        zones=[Zone("all", "All", ports, create_effect(effect, len(ports), params))],
    )


def renderer(tmp_path, profile, engine):
    awk = shutil.which("awk") or "C:/Program Files/Git/usr/bin/awk.exe"
    if not Path(awk).is_file():
        pytest.skip("awk is unavailable")
    config, palette = compile_scene_data(engine, profile)
    for name, data in {"config": config, "palette": palette, "previous": "", "ports": ""}.items():
        (tmp_path / name).write_text(data, encoding="ascii")
    shutil.copyfile(Path(player.__file__).with_name("resident") / "frames.awk", tmp_path / "frames.awk")
    ports = profile.copper_ports + profile.optical_ports

    def tick(stamp, total=0, *, count=None):
        data = "".join(f"{port} U/U 1000F 0 0 {total if port == ports[-1] else 0} 0\n" for port in ports)
        (tmp_path / "ports").write_text(data, encoding="ascii")
        result = subprocess.run(
            [awk, "-v", f"port_count={len(ports) if count is None else count}", "-v", f"stamp={stamp}",
             "-v", "frame=0", "-f", "frames.awk", "config", "palette", "previous", "ports"],
            cwd=tmp_path, capture_output=True, text=True, timeout=10,
        )
        if result.returncode == 0:
            (tmp_path / "next").replace(tmp_path / "previous")
        return result

    return tick


@pytest.mark.parametrize("profile", PROFILES.values(), ids=PROFILES.keys())
def test_every_canonical_layout_renders_all_connectors_without_stack_ports(tmp_path, profile):
    engine = scene(profile)
    ports = profile.copper_ports + profile.optical_ports
    engine.overrides = {p: Override(Color(p, 100 + p, 255 - p)) for p in ports}
    result = renderer(tmp_path, profile, engine)(1)
    assert result.returncode == 0, result.stderr
    rows = [list(map(int, row.split())) for row in result.stdout.splitlines()]
    assert rows == [[p, p * 256, (100 + p) * 256, (255 - p) * 256] for p in ports]
    assert not set(profile.stack_ports).intersection(row[0] for row in rows)


@pytest.mark.parametrize("profile", PROFILES.values(), ids=PROFILES.keys())
def test_activity_glow_and_atomic_scene_edit_reach_the_last_connector(tmp_path, profile):
    engine = scene(profile, "activity_glow")
    tick = renderer(tmp_path, profile, engine)
    assert tick(1).returncode == 0
    assert tick(2, 125000).returncode == 0
    result = tick(3, 250000)
    assert result.returncode == 0, result.stderr
    rows = [list(map(int, row.split())) for row in result.stdout.splitlines()]
    assert rows[0][1:] == [0, 0, 0] and rows[-1][1:] == [64084] * 3

    replacement = scene(profile)
    replacement.zones[0].effect = create_effect("static", len(rows), {"color": "#ff0000"})
    config, palette = compile_scene_data(replacement, profile)
    (tmp_path / "scene.edit").write_text("1\n" + config + palette, encoding="ascii")
    assert tick(4, 375000).stdout.splitlines()[0].split()[1:] == ["0", "0", "0"]
    (tmp_path / "scene.edit").replace(tmp_path / "scene.active")
    edited = tick(5)
    assert edited.returncode == 0, edited.stderr
    assert all(row.split()[1:] == ["65280", "0", "0"] for row in edited.stdout.splitlines())


@pytest.mark.parametrize("profile", PROFILES.values(), ids=PROFILES.keys())
@pytest.mark.parametrize("effect", ["traffic", "traffic_trails", "poe_usage", "rainbow"])
def test_palette_and_auxiliary_channels_scale_with_layout(profile, effect):
    config, palette = compile_scene_data(scene(profile, effect), profile)
    count = len(profile.copper_ports) + len(profile.optical_ports)
    assert [int(row.split()[0]) for row in config.splitlines()] == list(range(1, count + 1))
    expected = count * (6 if effect == "rainbow" else 12)
    assert all(len(row.split()) == expected for row in palette.splitlines())
    assert len(palette.splitlines()) == (120 if effect == "rainbow" else 1)


@pytest.mark.parametrize("profile", PROFILES.values(), ids=PROFILES.keys())
def test_layout_compilation_does_not_bypass_runtime_qualification(profile):
    engine = scene(profile)
    compile_scene_data(engine, profile)
    assert player.supported(engine.switch) == (profile is PRO_XG8)
    if profile is not PRO_XG8:
        with pytest.raises(ValueError, match="verified Pro XG"):
            player.compile_scene(engine)
    if profile.output_verified:
        script = player.player_script(profile)
        assert f"port_count={len(profile.copper_ports) + len(profile.optical_ports)}\n" in script
        assert '-v port_count="$port_count"' in script
        for port in profile.optical_ports:
            mask = int(profile.sfp_mask(port), 16)
            assert f"{port}) printf '%s' '{mask >> 32:x} {mask & 0xffffffff:x}';;" in script
    else:
        with pytest.raises(ValueError, match="not been qualified"):
            player.player_script(profile)


def test_default_player_layout_is_the_existing_resource():
    assert player.player_script() == (Path(player.__file__).with_name("resident") / "player.sh").read_text("ascii")


@pytest.mark.parametrize("fault", ["forged", "stack", "duplicate", "wrong_optical", "optical_sync_source"])
def test_bad_scene_topology_is_rejected(fault):
    profile = PROFILES["ECS24SP"]
    engine = scene(profile)
    if fault == "forged":
        profile = replace(profile)
    elif fault == "stack":
        engine.overrides[profile.stack_ports[0]] = Override(Color(1, 2, 3))
    elif fault == "duplicate":
        engine.zones.append(engine.zones[0])
    elif fault == "wrong_optical":
        engine.sfp[1] = SfpLight()
    else:
        engine.sfp_sync = SfpSync("ethernet", profile.optical_ports[0])
    with pytest.raises(ValueError):
        compile_scene_data(engine, profile)


@pytest.mark.parametrize("fault", ["missing_row", "duplicate_row", "short_palette", "bad_target", "wrong_count"])
def test_renderer_discards_incomplete_or_wrong_layout(tmp_path, fault):
    profile = PROFILES["ECSAGG"]
    tick = renderer(tmp_path, profile, scene(profile))
    config = tmp_path / "config"
    rows = config.read_text().splitlines()
    if fault == "missing_row":
        config.write_text("\n".join(rows[:-1]) + "\n")
    elif fault == "duplicate_row":
        config.write_text("\n".join([rows[0], *rows[:-1]]) + "\n")
    elif fault == "short_palette":
        palette = tmp_path / "palette"
        palette.write_text(" ".join(palette.read_text().split()[:-1]) + "\n")
    elif fault == "bad_target":
        parts = rows[-1].split()
        parts[2] = "55"
        config.write_text("\n".join([*rows[:-1], " ".join(parts)]) + "\n")
    assert tick(1, count=55 if fault == "wrong_count" else None).returncode != 0


@pytest.mark.anyio
async def test_unqualified_player_is_rejected_before_any_remote_write(monkeypatch):
    async def unexpected(*args):
        pytest.fail("An unqualified layout reached the remote transport")
    monkeypatch.setattr(player, "_run", unexpected)
    with pytest.raises(ValueError, match="not been qualified"):
        await player.stage(None, "a" * 48, "", "", profile=PROFILES["ECSAGG"])


@pytest.mark.anyio
@pytest.mark.parametrize("renderer_matches", [True, False])
async def test_reconnect_checks_both_runtime_files_before_reusing_player(monkeypatch, renderer_matches):
    script = player.player_script()
    renderer = (Path(player.__file__).with_name("resident") / "frames.awk").read_text("ascii")
    replies = [hashlib.sha256(script.encode("ascii")).hexdigest(),
               hashlib.sha256(renderer.encode("ascii")).hexdigest() if renderer_matches else "0" * 64,
               "ready"]
    run = AsyncMock(side_effect=replies)
    upload, drain, start = AsyncMock(), AsyncMock(return_value=True), AsyncMock()
    monkeypatch.setattr(player, "_run", run)
    monkeypatch.setattr(player, "status", AsyncMock(return_value="playing"))
    monkeypatch.setattr(player, "_upload", upload)
    monkeypatch.setattr(player, "drain", drain)
    monkeypatch.setattr(player, "start", start)
    await player.upgrade_player(None, "a" * 48)
    if renderer_matches:
        upload.assert_not_awaited()
        drain.assert_not_awaited()
        start.assert_not_awaited()
    else:
        upload.assert_awaited_once_with(None, "a" * 48, {"player.next": script, "frames.edit": renderer})
        drain.assert_awaited_once()
        start.assert_awaited_once()


@pytest.mark.parametrize("profile", PROFILES.values(), ids=PROFILES.keys())
def test_unmanaged_optical_connectors_have_no_render_output(tmp_path, profile):
    engine = scene(profile)
    selected = profile.optical_ports[-1]
    ports = profile.copper_ports + (selected,)
    engine.zones = [Zone("selected", "Selected", ports, create_effect("static", len(ports), {"color": "#ff0000"}))]
    result = renderer(tmp_path, profile, engine)(1)
    assert result.returncode == 0, result.stderr
    assert [int(row.split()[0]) for row in result.stdout.splitlines()] == list(ports)


@pytest.mark.parametrize("profile", [p for p in PROFILES.values() if p.output_verified],
                         ids=[p.key for p in PROFILES.values() if p.output_verified])
def test_prepare_acquires_only_the_selected_optical_connector(tmp_path, profile):
    shell = shutil.which("sh") or "C:/Program Files/Git/usr/bin/sh.exe"
    if not Path(shell).is_file():
        pytest.skip("A shell is unavailable")
    engine = scene(profile)
    selected = profile.optical_ports[-1]
    ports = profile.copper_ports + (selected,)
    engine.zones = [Zone("selected", "Selected", ports, create_effect("static", len(ports), {}))]
    config, palette = compile_scene_data(engine, profile)
    (tmp_path / "config").write_text(config, encoding="ascii")
    (tmp_path / "scene.active").write_text("1\n" + config + palette, encoding="ascii")
    # Exercise only the generated preparation functions with a recording sink.
    # No ubus, device access, telemetry read or player process runs in this test.
    script = player.player_script(profile).split("read -r start unused < /proc/uptime", 1)[0]
    script += "set_led() { printf '%s\\n' \"$1\"; }\nprepare\n"
    (tmp_path / "prepare.sh").write_text(script, encoding="ascii", newline="\n")
    env = {**os.environ, "PATH": str(Path(shell).parent) + os.pathsep + os.environ["PATH"]}
    result = subprocess.run([shell, "prepare.sh"], cwd=tmp_path, env=env,
                            capture_output=True, text=True, timeout=10)
    assert result.returncode == 0, result.stderr
    assert [json.loads(row) for row in result.stdout.splitlines()] == [
        {"behavior": "steady"}, {"port_pwm": f"{selected} 0 0 0 0"},
        {"mcu_mode": f"soc_port_map steady {int(profile.sfp_mask(selected), 16) >> 32:x} "
                     f"{int(profile.sfp_mask(selected), 16) & 0xffffffff:x}"},
    ]


@pytest.mark.parametrize("profile", tuple(p for p in PROFILES.values() if p.output_verified), ids=lambda p: p.key)
def test_player_optical_map_matches_desktop_protocol_for_every_connector(tmp_path, profile):
    from etherlighting_evolved.ubus import _named_attributes, _sfp_mode_data

    shell = shutil.which("sh") or "C:/Program Files/Git/usr/bin/sh.exe"
    if not Path(shell).is_file():
        pytest.skip("A shell is unavailable")
    renderer(tmp_path, profile, scene(profile))
    script = player.player_script(profile).split("read -r start unused < /proc/uptime", 1)[0]
    script += "set_led() { printf '%s\\n' \"$1\"; }\n"
    script += "\n".join(f"optical {port}" for port in profile.optical_ports) + "\n"
    (tmp_path / "map.sh").write_text(script, encoding="ascii", newline="\n")
    env = {**os.environ, "PATH": str(Path(shell).parent) + os.pathsep + os.environ["PATH"]}
    result = subprocess.run([shell, "map.sh"], cwd=tmp_path, env=env, capture_output=True,
                            text=True, timeout=10)
    assert result.returncode == 0, result.stderr
    observed = [json.loads(line)["mcu_mode"] for line in result.stdout.splitlines()]
    expected = []
    for port in profile.optical_ports:
        [(name, _, value)] = _named_attributes(_sfp_mode_data(port, True, profile=profile))
        assert name == b"mcu_mode"
        expected.append(value.rstrip(b"\0").decode("ascii"))
    assert observed == expected
