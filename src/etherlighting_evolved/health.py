"""Bounded read-only system health sampling and strict Linux ABI parsing."""

from __future__ import annotations

import re
from typing import Any

HEALTH_INTERVAL = 10.0
HEALTH_TIMEOUT = 5.0
MAX_HEALTH_RESPONSE = 65_536
MAX_HEALTH_LINES = 512
MAX_TEMPERATURES = 32
MAX_STORAGE_MOUNTS = 16

# Absolute, fixed paths only. The caller must isolate this read's channel timeout
# from the SSH connection carrying lighting output.
HEALTH_COMMAND = r"""(
LC_ALL=C
export LC_ALL
printf '__EE_HEALTH_V1__\n'
ee_n=0
while read -r ee_key ee_value ee_unit ee_extra; do
    ee_n=$((ee_n + 1))
    [ "$ee_n" -le 128 ] || break
    case "$ee_key" in
        MemTotal:|MemAvailable:|MemFree:)
            printf 'M\t%s\t%s\t%s\n' "$ee_key" "$ee_value" "$ee_unit" ;;
    esac
done < /proc/meminfo
ee_n=0
for ee_path in /sys/class/thermal/thermal_zone[0-9]*; do
    ee_n=$((ee_n + 1))
    [ "$ee_n" -le 32 ] || break
    [ -r "$ee_path/temp" ] || continue
    ee_label=
    ee_temp=
    IFS= read -r ee_label < "$ee_path/type"
    IFS= read -r ee_temp < "$ee_path/temp"
    printf 'T\t%s\t%s\t%s\n' "${ee_path##*/}" "$ee_label" "$ee_temp"
done
ee_n=0
for ee_path in /sys/class/hwmon/hwmon[0-9]*/temp[0-9]*_input \
               /sys/class/hwmon/hwmon[0-9]*/device/temp[0-9]*_input; do
    [ -r "$ee_path" ] || continue
    ee_dir=${ee_path%/*}
    ee_class=${ee_dir%/device}
    if [ "$ee_dir" != "$ee_class" ] && [ -e "$ee_class/${ee_path##*/}" ]; then continue; fi
    ee_n=$((ee_n + 1))
    [ "$ee_n" -le 32 ] || break
    ee_base=${ee_path%_input}
    ee_sensor=${ee_base##*/}
    ee_name=
    ee_label=
    ee_temp=
    ee_fault=0
    ee_type=
    IFS= read -r ee_name < "$ee_dir/name"
    [ ! -r "${ee_base}_label" ] || IFS= read -r ee_label < "${ee_base}_label"
    IFS= read -r ee_temp < "$ee_path"
    if [ -e "${ee_base}_fault" ]; then
        ee_fault='?'
        IFS= read -r ee_fault < "${ee_base}_fault"
    fi
    if [ -e "${ee_base}_type" ]; then
        ee_type='?'
        IFS= read -r ee_type < "${ee_base}_type"
    fi
    printf 'H\t%s/%s\t%s\t%s\t%s\t%s\t%s\n' \
        "${ee_class##*/}" "$ee_sensor" "$ee_name" "$ee_label" "$ee_temp" "$ee_fault" "$ee_type"
done
ee_n=0
ee_mounts=0
while read -r ee_device ee_mount ee_fs ee_options ee_rest; do
    ee_n=$((ee_n + 1))
    [ "$ee_n" -le 256 ] || break
    case "$ee_fs" in ext2|ext3|ext4|f2fs|jffs2|ubifs|btrfs|xfs) ;; *) continue ;; esac
    case ",$ee_options," in *,rw,*) ;; *) continue ;; esac
    case "$ee_device" in /dev/*|ubi[0-9]*:*|mtd:*) ;; *) continue ;; esac
    case "$ee_mount" in /*) ;; *) continue ;; esac
    case "$ee_mount" in *\\*) continue ;; esac
    ee_mounts=$((ee_mounts + 1))
    [ "$ee_mounts" -le 16 ] || break
    printf 'S\t%s\t%s\n' "$ee_mount" "$ee_fs"
    df -Pk "$ee_mount"
    printf 'E\n'
done < /proc/mounts
printf '__EE_HEALTH_DONE__\n'
) 2>/dev/null"""

_LOCAL_FILESYSTEMS = frozenset({"ext2", "ext3", "ext4", "f2fs", "jffs2", "ubifs", "btrfs", "xfs"})
_MEMORY_KEYS = frozenset({"MemTotal:", "MemAvailable:", "MemFree:"})


class HealthParseError(ValueError):
    """The sample is incomplete or exceeds the collector's bounds."""


def _unsigned(value: str) -> int | None:
    if not re.fullmatch(r"[0-9]{1,19}", value):
        return None
    result = int(value)
    return result if result <= 2**63 - 1 else None


def _label(value: str) -> str | None:
    if len(value) > 160 or any(ord(char) < 32 or ord(char) == 127 for char in value):
        return None
    return value.strip()


def _temperature(value: str) -> float | None:
    if not re.fullmatch(r"-?[0-9]{1,7}", value):
        return None
    millidegrees = int(value)
    # Reject sentinel values without classifying a real sensor as CPU or drive.
    return millidegrees / 1000 if -273_150 <= millidegrees <= 300_000 else None


def _memory(records: list[list[str]]) -> dict[str, Any] | None:
    values: dict[str, int | None] = {}
    for record in records:
        if len(record) != 4 or record[1] not in _MEMORY_KEYS:
            continue
        key, raw, unit = record[1:]
        if key in values:
            return None
        amount = _unsigned(raw) if unit == "kB" else None
        values[key] = amount * 1024 if amount is not None and amount <= (2**63 - 1) // 1024 else None
    total = values.get("MemTotal:")
    if total is None or total <= 0:
        return None
    available = values.get("MemAvailable:")
    free = values.get("MemFree:")
    if available is not None and available > total:
        available = None
    if free is not None and free > total:
        free = None
    used = total - available if available is not None else None
    return {
        "total_bytes": total,
        "available_bytes": available,
        "used_bytes": used,
        "used_percent": 100 * used / total if used is not None else None,
        "free_bytes": free,
    }


def _sensor(record: list[str]) -> dict[str, Any] | None:
    if record[0] == "T" and len(record) == 4:
        _, identifier, raw_label, raw_temperature = record
        if not re.fullmatch(r"thermal_zone[0-9]{1,4}", identifier):
            return None
        label = _label(raw_label)
        source = "thermal_zone"
    elif record[0] == "H" and len(record) == 7:
        _, identifier, name, raw_label, raw_temperature, fault, sensor_type = record
        if not re.fullmatch(r"hwmon[0-9]{1,4}/temp[0-9]{1,4}", identifier):
            return None
        # Some external thermistors report voltage instead of temperature.
        if fault != "0" or sensor_type not in {"", "1", "2", "3", "5", "6"}:
            return None
        driver = _label(name)
        label = _label(raw_label)
        if driver is None or label is None:
            return None
        if not label:
            label = f"Sensor {identifier.rsplit('/temp', 1)[1]}"
        label = " / ".join(part for part in (driver, label) if part)
        source = "hwmon"
    else:
        return None
    temperature = _temperature(raw_temperature)
    if label is None or temperature is None:
        return None
    return {"id": identifier, "label": label or identifier, "celsius": temperature, "source": source}


def _storage(header: list[str], lines: list[str]) -> dict[str, Any] | None:
    if len(header) != 3:
        return None
    _, mount, filesystem = header
    if (filesystem not in _LOCAL_FILESYSTEMS or not mount.startswith("/") or "\\" in mount
            or _label(mount) != mount or any(char.isspace() for char in mount)):
        return None
    rows = [line.split() for line in lines if line and not line.startswith("Filesystem")]
    if len(rows) != 1 or len(rows[0]) != 6:
        return None
    device, raw_total, raw_used, raw_available, percent, reported_mount = rows[0]
    if not (device.startswith(("/dev/", "mtd:")) or re.match(r"ubi[0-9]+:", device)):
        return None
    if reported_mount != mount or not re.fullmatch(r"[0-9]{1,3}%", percent):
        return None
    numbers = [_unsigned(value) for value in (raw_total, raw_used, raw_available)]
    if any(value is None or value > (2**63 - 1) // 1024 for value in numbers):
        return None
    total, used, available = (int(value) * 1024 for value in numbers)
    if total <= 0 or used + available > total:
        return None
    return {
        "mount": mount,
        "filesystem": filesystem,
        "total_bytes": total,
        "used_bytes": used,
        "available_bytes": available,
        "used_percent": 100 * used / total,
    }


def parse_health(output: str) -> dict[str, Any]:
    """Parse a complete bounded sample; absent or invalid readings stay unknown."""
    if len(output) > MAX_HEALTH_RESPONSE or len(output.encode("utf-8")) > MAX_HEALTH_RESPONSE:
        raise HealthParseError("Health response exceeds the size limit")
    lines = output.splitlines()
    if len(lines) > MAX_HEALTH_LINES:
        raise HealthParseError("Health response exceeds the line limit")
    if not lines or lines[0] != "__EE_HEALTH_V1__" or lines[-1] != "__EE_HEALTH_DONE__":
        raise HealthParseError("Health response is incomplete")
    memory: list[list[str]] = []
    sensors: dict[str, dict[str, Any]] = {}
    storage: dict[str, dict[str, Any]] = {}
    pending_mount: list[str] | None = None
    mount_lines: list[str] = []
    for line in lines[1:-1]:
        record = line.split("\t")
        if pending_mount is not None:
            if line == "E":
                mount = _storage(pending_mount, mount_lines)
                if mount is not None and len(storage) < MAX_STORAGE_MOUNTS:
                    storage.setdefault(mount["mount"], mount)
                pending_mount = None
            else:
                mount_lines.append(line)
            continue
        if record[0] == "M":
            memory.append(record)
        elif record[0] in {"T", "H"}:
            sensor = _sensor(record)
            if sensor is not None and len(sensors) < MAX_TEMPERATURES:
                sensors.setdefault(sensor["id"], sensor)
        elif record[0] == "S":
            pending_mount = record
            mount_lines = []
    if pending_mount is not None:
        raise HealthParseError("Health storage response is incomplete")
    return {"memory": _memory(memory), "temperatures": list(sensors.values()), "storage": list(storage.values())}
