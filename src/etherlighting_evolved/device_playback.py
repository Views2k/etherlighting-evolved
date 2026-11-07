"""Compile lighting scenes and transfer them to the qualified switch's RAM."""
from __future__ import annotations

import asyncio
import hashlib
import json
import os
import re
import secrets
import shlex
from pathlib import Path

from .color import BLACK
from .effects import create_effect
from .effects.base import RenderContext
from .hardware import PRO_XG8
from .transport import TransportError

LOCATION = "/tmp/ee-lighting-playback-v1"
_FILES = ("frames.awk", "player.sh", "config", "palette")
_RUNTIME = Path(__file__).with_name("resident")
_OWNER = re.compile(r"[a-f0-9]{48}")
# /tmp can be a separate tmpfs or part of the firmware's RAM root.
_PREFLIGHT = r"""cd /tmp && [ "$(pwd -P)" = /tmp ] &&
awk '$2=="/"{root=$3} $2=="/tmp"{tmp=$3}
END{fs=(tmp?tmp:root); exit !(fs=="tmpfs" || fs=="ramfs" || fs=="rootfs")}' /proc/mounts &&
awk '/MemAvailable:/{ok=($2>8192)} END{exit !ok}' /proc/meminfo &&
for app in awk ubus sh swctrl timeout nohup sha256sum tr stat; do command -v "$app" >/dev/null || exit 1; done"""
_CHECK_DIRECTORY = '[ ! -L "$d" ] && [ -d "$d" ] && [ "$(stat -c %u:%a "$d")" = "$(id -u):700" ]'


def supported(switch):
    return switch.hardware_profile is PRO_XG8 and switch.native_breathing and switch.sfp_precise


def compile_scene(engine) -> tuple[str, str]:
    if not supported(engine.switch) or engine.mode != "running":
        raise ValueError("Start lighting on the verified Pro XG 8 PoE before saving to the switch.")
    if any(rule.enabled for rule in engine.rules):
        raise ValueError("Turn off active rules before saving this scene to the switch.")
    settings = {p: [0, p, 0, 0, 1, engine.brightness, engine.gamma] for p in range(1, 11)}
    groups = []
    for group, zone in enumerate(engine.zones, 1):
        fx = create_effect(zone.effect.id, len(zone.ports), zone.effect.params_json())
        if fx.category in {"time", "data"} and fx.id != "activity_glow":
            raise ValueError(f"{fx.name} needs desktop playback. Choose Activity Glow for live switch traffic.")
        groups.append((zone.ports, fx, RenderContext(len(zone.ports))))
        for p in zone.ports:
            if fx.id == "activity_glow":
                settings[p][:5] = [group, p, fx.p["minimum_mbps"], int(fx.p["include_uplinks"]), fx.p["speed"]]
            if p in engine.sfp:
                settings[p][5] *= engine.sfp[p].brightness / 100
    grouped = {p for ports, _, _ in groups for p in ports}
    independent = {}
    for p in engine.sfp.keys() - grouped:
        if engine.sfp_sync.mode == "ethernet":
            source = engine.sfp_sync.source_port
            settings[p][1] = source
            settings[p][5] = settings[source][5]
        else:
            light = engine.sfp[min(engine.sfp) if engine.sfp_sync.mode == "together" else p]
            if light.mode == "unmanaged":
                settings[p][1] = 0
            else:
                independent[p] = light
                settings[p][5] *= light.brightness / 100
    for p, override in engine.overrides.items():
        if override.brightness is not None:
            settings[p][5] = override.brightness
    for p in engine.sfp.keys() - grouped:
        if engine.sfp_sync.mode == "ethernet":
            settings[p][5] = settings[engine.sfp_sync.source_port][5]
    rows = []
    for index in range(120):
        t = index / 4
        colors = {p: (BLACK, BLACK) for p in settings}
        for ports, fx, ctx in groups:
            if fx.id == "activity_glow":
                palette = fx.colors(t)
                for p in ports:
                    colors[p] = palette["idle_color"], palette["active_color"]
            else:
                for p, color in zip(ports, fx.render(t, ctx), strict=True):
                    colors[p] = color, color
        for p, light in independent.items():
            color = light.render(t, p)
            colors[p] = color, color
        for p, override in engine.overrides.items():
            colors[p] = override.color, override.color
        channels = (c for pair in colors.values() for col in pair for c in col.precise_channels)
        rows.append(" ".join(format(c, ".7g") for c in channels))
    if all(row == rows[0] for row in rows):
        rows = rows[:1]
    limit = max(1, min(10, int(engine.max_commands)))
    config = "".join(" ".join(str(n) for n in [p, *values, limit]) + "\n" for p, values in settings.items())
    return config, "\n".join(rows) + "\n"


class PlaybackRecord:
    """Keep the recovery identity until an explicit, acknowledged handoff."""
    def __init__(self, path):
        self.path = path
        try:
            data = json.loads(path.read_text("utf-8"))
            if not (isinstance(data, dict) and _OWNER.fullmatch(str(data.get("owner", "")))
                    and re.fullmatch(r"[a-f0-9]{64}", str(data.get("device", "")))
                    and data.get("state") in {"preparing", "last_playing", "handoff"}):
                raise ValueError("Invalid playback record")
            self.data = data
        except FileNotFoundError:
            self.data = None
        except (OSError, UnicodeError, ValueError):
            self.data = {"state": "unreadable"}

    def save(self, data):
        self.path.parent.mkdir(exist_ok=True, parents=True)
        temp = self.path.with_suffix("." + secrets.token_hex(8) + ".tmp")
        try:
            with temp.open("x", encoding="utf-8") as stream:
                json.dump(data, stream)
                stream.flush()
                os.fsync(stream.fileno())
            temp.replace(self.path)
        finally:
            temp.unlink(missing_ok=True)
        self.data = data

    def clear(self):
        self.path.unlink(missing_ok=True)
        self.data = None


async def _run(transport, command):
    return (await transport.run("(\n" + command + "\n)", channel="ee-playback", timeout=8)).strip()


async def available(transport):
    # Fail closed on command or transport errors, including a dangling path.
    return await _run(transport, f'd={LOCATION}; [ ! -e "$d" ] && [ ! -L "$d" ] && echo available') == "available"


def _owned(owner):
    if not _OWNER.fullmatch(owner):
        raise ValueError("Invalid playback owner")
    return f'd={LOCATION}; {_CHECK_DIRECTORY} && cd "$d" && [ "$(cat owner)" = {owner} ] || exit 1\n'


async def stage(transport, owner, config, palette):
    if not _OWNER.fullmatch(owner):
        raise ValueError("Invalid playback owner")
    init = f'umask 077\n{_PREFLIGHT} && mkdir -m 700 {LOCATION} && cd {LOCATION} && printf "%s" {owner} > owner'
    if await _run(transport, init + " && echo reserved") != "reserved":
        raise TransportError("The switch could not reserve its RAM playback directory.")
    files = {name: (_RUNTIME / name).read_text("ascii") for name in _FILES[:2]}
    files.update(config=config, palette=palette)
    for name, source in files.items():
        for offset in range(0, len(source), 8192):
            redirect = ">" if offset == 0 else ">>"
            command = _owned(owner) + f"printf %s {shlex.quote(source[offset:offset + 8192])} {redirect} {name}"
            if await _run(transport, command + " && echo saved") != "saved":
                raise TransportError("The switch could not store the scene.")
        digest = hashlib.sha256(source.encode("ascii")).hexdigest()
        verify = f'[ "$(sha256sum {name} | cut -d" " -f1)" = {digest} ] && echo verified'
        if await _run(transport, _owned(owner) + verify) != "verified":
            raise TransportError("The saved switch scene did not pass its integrity check.")


async def start(transport, owner):
    command = _owned(owner) + """[ ! -e stop ] && mkdir launched || exit 1
nohup sh player.sh </dev/null >/dev/null 2>&1 &
echo $! > launched/pid && echo started"""
    if await _run(transport, command) != "started":
        raise TransportError("The switch did not confirm starting the player.")
    for _ in range(20):
        if await _run(transport, _owned(owner) + 'cat status 2>/dev/null') == "playing":
            return
        await asyncio.sleep(0.25)
    raise TransportError("The switch has not acknowledged a full frame. Use Handoff to UniFi to recover.")


async def status(transport, owner):
    if await available(transport):
        return "absent"
    command = _owned(owner) + """pid=$(cat launched/pid 2>/dev/null)
case "$pid" in ''|*[!0-9]*) exit 1;; esac
[ "$(readlink "/proc/$pid/cwd")" = "$d" ] && cat status"""
    state = await _run(transport, command)
    return state if state in {"playing", "starting", "retrying"} else "unconfirmed"


async def drain(transport, owner):
    if await available(transport):
        return False
    command = _owned(owner) + """: > stop || exit 1
if [ ! -d launched ]; then echo drained; else
  for n in 1 2 3 4 5 6; do
    if [ "$(cat status 2>/dev/null)" = stopped ]; then echo drained; break; fi
    pid=$(cat launched/pid 2>/dev/null)
    case "$pid" in ''|*[!0-9]*) ;; *) if [ ! -d "/proc/$pid" ]; then echo drained; break; fi;; esac
    sleep 1
  done
fi"""
    if await _run(transport, command) != "drained":
        raise TransportError("The switch has not confirmed the player stopped.")
    return True


async def remove(transport, owner):
    files = " ".join((*_FILES, "previous", "next", "ports", "output", "status", "status.new", "stop", "launched/pid"))
    command = _owned(owner) + f"""rm -f {files} || exit 1
if [ -d launched ]; then rmdir launched || exit 1; fi
rm owner && cd / && rmdir "$d" && echo removed"""
    if await _run(transport, command) != "removed":
        raise TransportError("The switch could not confirm cleanup of its playback files.")
