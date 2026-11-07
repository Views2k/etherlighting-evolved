import shutil
import subprocess
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from etherlighting_evolved import device_playback as player
from etherlighting_evolved.config import Config
from etherlighting_evolved.effects import create_effect
from etherlighting_evolved.engine import Zone
from etherlighting_evolved.hardware import PRO_XG8
from etherlighting_evolved.ownership import device_identity
from etherlighting_evolved.session import LoginError, SessionManager
from etherlighting_evolved.sfp import SfpLight, SfpSync
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
    with pytest.raises(ValueError, match="desktop playback"):
        player.compile_scene(scene("daylight"))
    changed = scene()
    changed.rules = [SimpleNamespace(enabled=True)]
    with pytest.raises(ValueError, match="rules"):
        player.compile_scene(changed)


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


@pytest.mark.anyio
async def test_exit_reconnect_and_offline_handoff(config: Config, monkeypatch):
    manager = SessionManager(config)
    engine = scene()
    engine.switch.transport = SimpleNamespace(host_key_fingerprint=KEY)
    engine.connected, engine.revision = True, 7
    manager.engine = engine
    engine.switch_info, engine.error = {}, None
    engine.switch.output_lease = manager.output_journal.claim("switch.invalid", 22, KEY)
    transport = SimpleNamespace(host="switch.invalid", port=22, host_key_fingerprint=KEY)
    switch = SimpleNamespace(transport=transport, close=AsyncMock(), restore_defaults=AsyncMock())
    calls = []

    async def disconnect():
        manager.engine = None
        engine.switch.output_lease.finish()
        calls.append("desktop stopped")

    async def stage(*args):
        calls.append("staged")

    async def start(*args):
        assert manager.engine is None
        calls.append("started")

    monkeypatch.setattr(SessionManager, "_playback_switch", lambda self, identity: switch)
    monkeypatch.setattr(SessionManager, "_open_player", AsyncMock())
    monkeypatch.setattr(manager, "_disconnect_locked", disconnect)
    monkeypatch.setattr(player, "available", AsyncMock(return_value=True))
    monkeypatch.setattr(player, "stage", stage)
    monkeypatch.setattr(player, "start", start)
    monkeypatch.setattr(player, "status", AsyncMock(return_value="playing"))
    drain = AsyncMock(side_effect=[TransportError("offline"), True])
    monkeypatch.setattr(player, "drain", drain)
    cleared = AsyncMock()
    monkeypatch.setattr(player, "remove", cleared)
    assert (await manager.save_to_switch())["switch_playback"] == "playing"
    assert calls == ["staged", "desktop stopped", "started"]
    await manager.close()
    drain.assert_not_awaited()

    restarted = SessionManager(config)
    await restarted.auto_connect()
    assert restarted.engine is None and restarted.info()["switch_playback"] == "playing"
    with pytest.raises(LoginError, match="Handoff"):
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
