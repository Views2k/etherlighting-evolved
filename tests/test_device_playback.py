import json
import os
import shutil
import subprocess
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from etherlighting_evolved import device_playback as player
from etherlighting_evolved.config import Config
from etherlighting_evolved.effects import create_effect
from etherlighting_evolved.engine import Engine, Zone
from etherlighting_evolved.hardware import PRO_XG8
from etherlighting_evolved.ownership import OutputOwnershipError, device_identity
from etherlighting_evolved.session import LoginError, SessionManager
from etherlighting_evolved.sfp import SfpLight, SfpSync
from etherlighting_evolved.switch import EtherlightSwitch
from etherlighting_evolved.transport import TransportError

KEY = "SHA256:playback-fixture-identity"
IDENTITY = device_identity(KEY)


def scene(effect="activity_glow"):
    switch = SimpleNamespace(hardware_profile=PRO_XG8, native_breathing=True, sfp_precise=True)
    params = {"idle_color": "#000000", "active_color": "#ffffff"} if effect == "activity_glow" else {}
    return SimpleNamespace(
        switch=switch, mode="running", rules=[], brightness=100, gamma=1, max_commands=30, overrides={},
        sfp={9: SfpLight(brightness=50), 10: SfpLight()}, sfp_sync=SfpSync(),
        zones=[Zone("all", "All", tuple(range(1, 11)), create_effect(effect, 10, params))],
    )


def test_player_scene_uses_actual_settings_and_qualified_ports():
    config, palette = player.compile_scene(scene())
    lines = [line.split() for line in config.splitlines()]
    assert len(lines) == 10 and lines[8][0:3] == ["9", "1", "9"] and float(lines[8][6]) == 50
    assert len(palette.splitlines()) == 1 and len(palette.split()) == 60
    assert len(player.compile_scene(scene("rainbow"))[1].splitlines()) == 120
    for effect, mode in (("traffic", 1), ("traffic_trails", 2), ("poe_usage", 3), ("daylight", 4)):
        rows, colors = player.compile_scene(scene(effect))
        assert all(int(row.split()[9]) == mode for row in rows.splitlines())
        assert len(colors.splitlines()[0].split()) == (60 if mode == 4 else 120)
    changed = scene()
    changed.rules = [SimpleNamespace(enabled=True)]
    assert player.compile_scene(changed) == (config, palette)


def test_actual_device_renderer_handles_traffic_and_counter_reset(tmp_path):
    awk = shutil.which("awk")
    if not awk:
        binary = Path("C:/Program Files/Git/usr/bin/awk.exe")
        if not binary.is_file():
            pytest.skip("awk is unavailable")
        awk = str(binary)
    config, palette = player.compile_scene(scene())
    (tmp_path / "config").write_text(config, encoding="ascii")
    (tmp_path / "palette").write_text(palette, encoding="ascii")
    (tmp_path / "previous").write_text("", encoding="ascii")
    runtime = Path(player.__file__).with_name("resident") / "frames.awk"
    shutil.copyfile(runtime, tmp_path / "frames.awk")

    def tick(t, total):
        rows = [f"{p} U/U 1000F 0 0 {total if p in (1, 9) else 0} 0" for p in range(1, 11)]
        (tmp_path / "ports").write_text("\n".join(rows) + "\n", encoding="ascii")
        run = subprocess.run([awk, "-v", f"stamp={t}", "-v", "frame=0", "-f", "frames.awk",
                              "config", "palette", "previous", "ports"], cwd=tmp_path,
                             capture_output=True, text=True, check=True)
        (tmp_path / "next").replace(tmp_path / "previous")
        return [list(map(int, line.split())) for line in run.stdout.splitlines()]

    assert tick(1, 0)[0] == [1, 0, 0, 0]
    assert tick(2, 125000)[0] == [1, 0, 0, 0]
    rows = tick(3, 0)
    assert rows[0] == [1, 64084, 64084, 64084]
    assert rows[8] == [9, 32042, 32042, 32042]
    assert rows[9] == [10, 0, 0, 0]
    assert 0 < tick(4, 0)[0][1] < rows[0][1]

    # A 0.4.22 player keeps the original files open by name on each tick.
    # Staging must be invisible; publishing a complete numeric scene changes all ports together.
    replacement = scene("static")
    replacement.zones[0].effect = create_effect("static", 10, {"color": "#ff0000"})
    cfg, colors = player.compile_scene(replacement)
    pending = tmp_path / "scene.edit"
    pending.write_text("1\n" + cfg + colors, encoding="ascii")
    assert tick(5, 0)[1][1] == 0
    pending.replace(tmp_path / "scene.active")
    rows = tick(6, 0)
    assert len(rows) == 10 and rows[1] == [2, 65280, 0, 0] and rows[8] == [9, 32640, 0, 0]


@pytest.mark.parametrize("effect", ["traffic", "traffic_trails", "poe_usage", "daylight"])
def test_offline_sensor_effects_read_their_live_input(tmp_path, effect):
    awk = shutil.which("awk") or "C:/Program Files/Git/usr/bin/awk.exe"
    if not Path(awk).exists():
        pytest.skip("awk unavailable")
    config, palette = player.compile_scene(scene(effect))
    runtime = Path(player.__file__).with_name("resident") / "frames.awk"
    script = runtime.read_text().replace("timeout -t 3 swctrl poe show", "cat power").replace(
        "date '+%H %M %S'", "cat clock")
    files = {"config": config, "palette": palette, "frames.awk": script, "previous": ""}
    for name, data in files.items():
        (tmp_path / name).write_text(data, encoding="ascii", newline="\n")
    samples = []
    env = {**os.environ, "PATH": str(Path(awk).parent) + os.pathsep + os.environ["PATH"]}
    for t in range(1, 5):
        data = "".join(f"{p} U/U 1000F 0 0 {125000000 * (t - 1)} 0\n" for p in range(1, 11))
        (tmp_path / "ports").write_text(data, encoding="ascii", newline="\n")
        (tmp_path / "power").write_text("1 Auto On Good 15.0 50.0 300.0\n", encoding="ascii")
        (tmp_path / "clock").write_text("12 00 00\n" if t >= 2 else "02 00 00\n", encoding="ascii")
        result = subprocess.run([awk, "-v", f"stamp={t}", "-v", "frame=0", "-f", "frames.awk",
                                 "config", "palette", "previous", "ports"], cwd=tmp_path,
                                capture_output=True, check=True, text=True, env=env)
        (tmp_path / "next").replace(tmp_path / "previous")
        ports = [list(map(int, row.split())) for row in result.stdout.splitlines()]
        assert len(ports) == 10 and all(0 <= value <= 65280 for row in ports for value in row[1:])
        samples.append(ports[0][1:])
    assert samples[0] != samples[-1]


@pytest.mark.anyio
async def test_exit_reconnect_and_offline_handoff(config: Config, monkeypatch):
    manager = SessionManager(config)
    owner = "a" * 48
    manager._playback.save({"state": "last_playing", "device": IDENTITY, "owner": owner})
    manager.output_journal.player_lease("switch.invalid", 22, KEY, owner)
    transport = SimpleNamespace(host="switch.invalid", port=22, host_key_fingerprint=KEY)
    switch = SimpleNamespace(transport=transport, close=AsyncMock(), restore_defaults=AsyncMock())
    monkeypatch.setattr(SessionManager, "_playback_switch", lambda self, identity: switch)
    monkeypatch.setattr(SessionManager, "_open_player", AsyncMock())
    drain = AsyncMock(side_effect=[TransportError("offline"), True])
    monkeypatch.setattr(player, "drain", drain)
    cleared = AsyncMock()
    monkeypatch.setattr(player, "remove", cleared)
    await manager.close()
    drain.assert_not_awaited()

    restarted = SessionManager(config)
    reconnect = AsyncMock()
    monkeypatch.setattr(restarted, "_auto_connect", reconnect)
    await restarted.auto_connect()
    reconnect.assert_awaited_once()
    with pytest.raises(LoginError, match="Reconnect"):
        await restarted.connect("demo")
    with pytest.raises(LoginError):
        await restarted.handoff()
    assert restarted._playback.data["state"] == "handoff"
    assert not restarted.info()["handed_off"]
    cleared.assert_not_awaited()
    await restarted.close()
    final = SessionManager(config)
    await final.auto_connect()
    assert final.info()["handed_off"] and final.info()["switch_playback"] is None
    switch.restore_defaults.assert_awaited_once()
    cleared.assert_awaited_once()
    await final.close()


@pytest.mark.anyio
async def test_unacknowledged_transfer_never_starts_a_second_writer(tmp_path, monkeypatch):
    record = player.PlaybackRecord(tmp_path / "owner.json")
    owner = "a" * 48
    record.save(dict(state="preparing", device=IDENTITY, owner=owner))
    seen = []

    async def run(_, command):
        seen.append(command)
        return "reserved" if len(seen) == 1 else "saved" if len(seen) == 2 else "wrong"

    monkeypatch.setattr(player, "_run", run)
    with pytest.raises(TransportError, match="integrity"):
        await player.stage(None, owner, "numeric", "palette")
    assert not any("nohup sh player.sh" in cmd for cmd in seen)
    assert player.PlaybackRecord(record.path).data["state"] == "preparing"
    with pytest.raises(ValueError, match="owner"):
        await player.start(None, "invalid")


@pytest.mark.anyio
@pytest.mark.parametrize("condition", ["absent", "other_device", "other_lease", "occupied", "offline", "probe_denied"])
async def test_reopening_saved_glow_after_switch_ram_loss(config, monkeypatch, condition):
    manager = SessionManager(config)
    owner = "a" * 48
    receipt = {"state": "last_playing", "device": IDENTITY, "owner": owner, "applied_edit": "b" * 64}
    manager._playback.save(receipt)
    engine = scene()
    engine.snapshot = lambda: {"mode": "running"}
    engine.revision = 5
    transport = SimpleNamespace(host="switch.invalid", port=22, host_key_fingerprint=KEY,
                                lighting_generation=object())
    engine.switch.transport = transport
    candidate = SimpleNamespace(engine=engine, fingerprint=KEY)
    if condition == "other_device":
        candidate.fingerprint = transport.host_key_fingerprint = "SHA256:another-fixture-identity"
    lease_owner = "f" * 48 if condition == "other_lease" else owner
    manager.output_journal.player_lease(transport.host, transport.port, KEY, lease_owner)
    replies = {"absent": "available", "other_lease": "available", "occupied": "occupied",
               "probe_denied": "permission denied"}
    async def run(_, command):
        if "then echo occupied" in command:
            if condition == "offline":
                raise TransportError("offline")
            return replies[condition]
        if "desktop.next" in command:
            return "granted"
        pytest.fail("unexpected switch operation")
    monkeypatch.setattr(player, "_run", run)
    stage, start = AsyncMock(), AsyncMock()
    monkeypatch.setattr(player, "stage", stage)
    monkeypatch.setattr(player, "start", start)
    upgrade = AsyncMock(side_effect=TransportError("existing player belongs to another owner"))
    monkeypatch.setattr(player, "upgrade_player", upgrade)
    if condition != "absent":
        with pytest.raises((TransportError, OutputOwnershipError)):
            await manager._prepare_offline_lighting(candidate)
        stage.assert_not_awaited()
        start.assert_not_awaited()
        assert upgrade.await_count == int(condition == "occupied")
        assert manager._playback.data == receipt
        return
    await manager._prepare_offline_lighting(candidate)
    stage.assert_awaited_once_with(transport, owner, *player.compile_scene(engine))
    start.assert_awaited_once_with(transport, owner)
    upgrade.assert_not_awaited()
    assert engine.switch.output_lease.keep_for_player
    assert manager._playback.data == {"state": "last_playing", "device": IDENTITY, "owner": owner,
                                      "scene_hash": manager._offline_scene_hash(engine)}


@pytest.mark.anyio
async def test_normal_editor_reopens_and_commits_to_existing_switch_scene(config, monkeypatch):
    manager = SessionManager(config)
    transport = SimpleNamespace(host="switch.invalid", port=22, host_key_fingerprint=KEY, direct_rgb=True,
                                lighting_generation=object())
    switch = EtherlightSwitch(transport, num_ports=8)
    switch.hardware_profile = PRO_XG8
    monkeypatch.setattr(type(switch), "native_breathing", property(lambda self: True))
    monkeypatch.setattr(type(switch), "sfp_precise", property(lambda self: True))
    engine = Engine(switch, rules=[])
    engine.connected = True
    store = manager._device_store(switch)
    store.save(engine.snapshot())
    manager._playback.save({"state": "last_playing", "device": IDENTITY, "owner": "b" * 48})
    monkeypatch.setattr(player, "available", AsyncMock(return_value=False))
    upgrade = AsyncMock()
    monkeypatch.setattr(player, "upgrade_player", upgrade)
    remote = AsyncMock(return_value="original")
    monkeypatch.setattr(player, "edit_revision", remote)
    grant = AsyncMock(return_value="granted")
    monkeypatch.setattr(player, "_run", grant)
    await manager._prepare_offline_lighting(SimpleNamespace(engine=engine, fingerprint=KEY, store=store))
    upgrade.assert_awaited_once()
    grant.assert_awaited_once()
    engine.set_effect("rainbow", {}, clear_overrides=False)
    manager.engine, manager._scene_store = engine, store
    assert manager.info()["logged_in"] and manager.info()["connected"]

    async def accept(transport, owner, content, previous):
        import hashlib
        assert previous == "original" and content.startswith("120\n")
        remote.return_value = hashlib.sha256(content.encode("ascii")).hexdigest()

    send = AsyncMock(side_effect=accept)
    monkeypatch.setattr(player, "edit_scene", send)
    await manager.sync_offline_lighting(engine)
    await manager.sync_offline_lighting(engine)
    assert manager.info()["logged_in"]
    send.assert_awaited_once()
    assert manager._playback.data["applied_edit"] == remote.return_value
    switch.output_lease.finish()
    assert all(path.is_file() for path in switch.output_lease.paths)


@pytest.mark.anyio
@pytest.mark.parametrize("reported", ["to", "from", "other"])
async def test_reopening_settles_an_edit_recorded_by_an_earlier_version(config, monkeypatch, reported):
    # Earlier versions saved pending_edit before editing the switch scene, so a lost reply left it in the record.
    manager = SessionManager(config)
    transport = SimpleNamespace(host="switch.invalid", port=22, host_key_fingerprint=KEY, direct_rgb=True,
                                lighting_generation=object())
    switch = EtherlightSwitch(transport, num_ports=8)
    switch.hardware_profile = PRO_XG8
    monkeypatch.setattr(type(switch), "native_breathing", property(lambda self: True))
    monkeypatch.setattr(type(switch), "sfp_precise", property(lambda self: True))
    engine = Engine(switch, rules=[])
    store = manager._device_store(switch)
    store.save(engine.snapshot())
    original = store.load()
    edited = Engine(switch, rules=[])
    edited.set_effect("rainbow", {}, clear_overrides=False)
    pending = {"from": "original", "to": "c" * 64, "scene": json.loads(json.dumps(edited.snapshot()))}
    record = {"state": "last_playing", "device": IDENTITY, "owner": "b" * 48, "pending_edit": pending}
    manager._playback.save(record)
    current = {"to": pending["to"], "from": pending["from"], "other": "d" * 64}[reported]
    monkeypatch.setattr(player, "available", AsyncMock(return_value=False))
    monkeypatch.setattr(player, "upgrade_player", AsyncMock())
    monkeypatch.setattr(player, "edit_revision", AsyncMock(return_value=current))
    monkeypatch.setattr(player, "_run", AsyncMock(return_value="granted"))
    candidate = SimpleNamespace(engine=engine, fingerprint=KEY, store=store)
    if reported == "other":
        with pytest.raises(ValueError, match="unknown scene revision"):
            await manager._prepare_offline_lighting(candidate)
        assert store.load() == original
        assert manager._playback.data == record
        return
    await manager._prepare_offline_lighting(candidate)
    expected = pending["scene"] if reported == "to" else original
    assert store.load() == expected
    assert engine.snapshot()["zones"][0]["effect"]["id"] == ("rainbow" if reported == "to" else "static")
    assert manager._playback.data == {"state": "last_playing", "device": IDENTITY, "owner": "b" * 48,
                                      "scene_hash": None, "applied_edit": current}


@pytest.mark.anyio
async def test_desktop_output_requires_a_fresh_ack_and_the_same_switch(monkeypatch):
    transport = SimpleNamespace(host_key_fingerprint=KEY, lighting_generation=object())
    grant = player.DesktopOutput(transport, "a" * 48)
    run = AsyncMock(side_effect=["", "granted", ""])
    monkeypatch.setattr(player, "_run", run)
    with pytest.raises(TransportError, match="acknowledge"):
        await grant.renew()
    await grant.renew()
    await grant.renew()
    assert run.await_count == 2
    transport.lighting_generation = object()
    with pytest.raises(TransportError):
        await grant.renew()
    transport.host_key_fingerprint = "different"
    with pytest.raises(TransportError):
        await grant.renew()
    assert run.await_count == 3
