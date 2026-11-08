"""Compile lighting scenes and transfer them to the qualified switch's RAM."""
from __future__ import annotations

import asyncio
import hashlib
import json
import os
import re
import secrets
import shlex
import time
from pathlib import Path

from .color import BLACK
from .effects import create_effect
from .effects.base import RenderContext
from .effects.library import DAYLIGHT_PALETTES
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
    settings = {p: [0, p, 0, 0, 1, engine.brightness, engine.gamma] for p in range(1, 11)}
    inputs = {}
    groups = []
    for group, zone in enumerate(engine.zones, 1):
        fx = create_effect(zone.effect.id, len(zone.ports), zone.effect.params_json())
        groups.append((zone.ports, fx, RenderContext(len(zone.ports))))
        for i, p in enumerate(zone.ports):
            if fx.id == "activity_glow":
                settings[p][:5] = [group, p, fx.p["minimum_mbps"], int(fx.p["include_uplinks"]), fx.p["speed"]]
            elif fx.id == "traffic":
                settings[p][3:5] = [int(fx.p["include_uplinks"]), fx.p["smoothing"]]
                inputs[p] = [1, int(fx.p["custom_uplink"])]
            elif fx.id == "traffic_trails":
                settings[p][2:5] = [fx.p["max_mbps"], int(fx.p["include_uplinks"]), fx.p["speed"]]
                inputs[p] = [2, i, len(zone.ports), fx.p["width"]]
            elif fx.id == "poe_usage":
                settings[p][4] = fx.p["speed"]
                inputs[p] = [3]
            elif fx.id == "daylight":
                stops = DAYLIGHT_PALETTES[fx.p["palette"]]
                inputs[p] = [4, fx.p["night_brightness"], len(stops)]
                for hour, color in stops:
                    inputs[p].extend([hour, *color.precise_channels])
            elif fx.category in {"time", "data"}:
                raise ValueError(f"{fx.name} has no offline sensor mapping.")
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
        inputs.pop(p, None)
        settings[p][:2] = [0, p]
        if override.brightness is not None:
            settings[p][5] = override.brightness
    for p in engine.sfp.keys() - grouped:
        if engine.sfp_sync.mode == "ethernet":
            settings[p][5] = settings[engine.sfp_sync.source_port][5]
    rows = []
    for index in range(120):
        t = index / 4
        colors = {p: (BLACK, BLACK) for p in settings}
        extra = {p: (BLACK, BLACK) for p in settings}
        for ports, fx, ctx in groups:
            if fx.id == "activity_glow":
                palette = fx.colors(t)
                for p in ports:
                    colors[p] = palette["idle_color"], palette["active_color"]
            elif fx.id in {"traffic", "traffic_trails", "poe_usage"}:
                palette = fx.colors(t)
                for p in ports:
                    if fx.id == "poe_usage":
                        colors[p] = palette["no_power_color"], palette["low_color"]
                        extra[p] = palette["high_color"], BLACK
                    else:
                        colors[p] = palette["idle_color"], palette["busy_color"]
                        extra[p] = palette["offline_color"], palette.get("uplink_color", BLACK)
            elif fx.id != "daylight":
                for p, color in zip(ports, fx.render(t, ctx), strict=True):
                    colors[p] = color, color
        for p, light in independent.items():
            color = light.render(t, p)
            colors[p] = color, color
        for p, override in engine.overrides.items():
            colors[p] = override.color, override.color
        pairs = list(colors.values())
        if any(fields[0] in {1, 2, 3} for fields in inputs.values()):
            pairs.extend(extra.values())
        channels = (c for pair in pairs for col in pair for c in col.precise_channels)
        rows.append(" ".join(format(c, ".7g") for c in channels))
    if all(row == rows[0] for row in rows):
        rows = rows[:1]
    limit = max(1, min(10, int(engine.max_commands)))
    config = "".join(" ".join(str(n) for n in [p, *values, limit, *inputs.get(p, [])]) + "\n"
                     for p, values in settings.items())
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
    await _upload(transport, owner, files)


async def _upload(transport, owner, files):
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


async def edit_revision(transport, owner):
    command = _owned(owner) + ('if [ -f scene.active ]; then sha256sum scene.active | cut -d" " -f1; '
                               'elif [ ! -e scene.active ]; then echo original; fi')
    digest = await _run(transport, command)
    if digest != "original" and not re.fullmatch(r"[a-f0-9]{64}", digest):
        raise TransportError("The switch could not confirm its current scene.")
    return digest


async def edit_scene(transport, owner, content, previous):
    if await status(transport, owner) != "playing" or await edit_revision(transport, owner) != previous:
        raise ValueError("Switch playback changed. Reopen the effect editor.")
    renderer = (_RUNTIME / "frames.awk").read_text("ascii")
    await _upload(transport, owner, {"frames.edit": renderer, "scene.edit": content})
    # Upgrade the reader first. It renders the original scene until the data
    # rename succeeds. The resident process keeps ownership throughout.
    command = _owned(owner) + ('[ ! -e stop ] && mv -f frames.edit frames.awk && '
                               'mv -f scene.edit scene.active && echo applied')
    if await _run(transport, command) != "applied":
        raise TransportError("The switch could not confirm applying the effect.")


class DesktopOutput:
    """Allow normal desktop rendering while an acknowledged switch-side deadline is live."""

    def __init__(self, transport, owner):
        self.transport = transport
        self.owner = owner
        self.ticket = secrets.token_hex(16)
        self.until = 0.0
        self.generation = None
        self.lock = asyncio.Lock()
        self.fingerprint = transport.host_key_fingerprint

    async def renew(self):
        async with self.lock:
            generation = self.transport.lighting_generation
            if generation is None or self.transport.host_key_fingerprint != self.fingerprint:
                raise TransportError("The lighting connection closed.")
            if self.generation is generation and time.monotonic() < self.until:
                return
            started = time.monotonic()
            # The local grant expires well before the switch-side deadline.
            # Rotate the ticket after a lapse so an earlier ACK cannot grant output.
            if self.generation is not generation or started >= self.until + 4:
                self.ticket = secrets.token_hex(16)
            command = _owned(self.owner) + f"""[ ! -e stop ] || exit 1
read -r now unused < /proc/uptime || exit 1
born=$(cut -d' ' -f22 /proc/$$/stat) && [ -n "$born" ] || exit 1
printf '%s %s %s %s\\n' {self.ticket} "$((${{now%.*}}+24))" "$$" "$born" > desktop.next &&
mv -f desktop.next desktop.request || exit 1
for n in 1 2 3 4 5 6; do
    [ "$(cat desktop.ack 2>/dev/null)" = {self.ticket} ] && {{ echo granted; break; }}
    sleep 1
done"""
            if (await _run(self.transport, command) != "granted"
                    or self.transport.lighting_generation is not generation or time.monotonic() >= started + 10):
                self.until = 0
                raise TransportError("The switch did not acknowledge desktop lighting control.")
            self.generation, self.until = generation, started + 10

    async def release(self):
        self.until = 0
        command = _owned(self.owner) + (
            f'if read -r ticket expiry < desktop.request && [ "$ticket" = {self.ticket} ]; '
            'then rm -f desktop.request; fi')
        await _run(self.transport, command)


async def upgrade_player(transport, owner):
    """Upgrade an earlier RAM player in its own directory, keeping its committed scene."""
    script = (_RUNTIME / "player.sh").read_text("ascii")
    digest = hashlib.sha256(script.encode("ascii")).hexdigest()
    command = _owned(owner) + 'sha256sum player.sh | cut -d" " -f1'
    if await _run(transport, command) == digest and await status(transport, owner) == "playing":
        return
    renderer = (_RUNTIME / "frames.awk").read_text("ascii")
    await _upload(transport, owner, {"player.next": script, "frames.edit": renderer})
    if not await drain(transport, owner):
        raise TransportError("The saved lighting process changed.")
    command = _owned(owner) + """mv -f frames.edit frames.awk && mv -f player.next player.sh &&
rm -f launched/pid stop status desktop.ack desktop.request &&
{ [ ! -d launched ] || rmdir launched; } && echo ready"""
    if await _run(transport, command) != "ready":
        raise TransportError("The switch could not restart its saved lighting.")
    await start(transport, owner)


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
    files = " ".join((*_FILES, "frames.edit", "scene.edit", "scene.active", "previous", "next", "ports",
                      "player.next", "desktop.request", "desktop.next", "desktop.ack", "desktop.ack.new",
                      "output", "status", "status.new", "stop", "launched/pid"))
    command = _owned(owner) + f"""rm -f {files} || exit 1
if [ -d launched ]; then rmdir launched || exit 1; fi
rm owner && cd / && rmdir "$d" && echo removed"""
    if await _run(transport, command) != "removed":
        raise TransportError("The switch could not confirm cleanup of its playback files.")
