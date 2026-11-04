import os
import shutil
import subprocess
from pathlib import Path

import pytest

from etherlighting_evolved.health import (
    HEALTH_COMMAND,
    MAX_HEALTH_RESPONSE,
    HealthParseError,
    parse_health,
)


def sample(*records):
    return "\n".join(("__EE_HEALTH_V1__", *records, "__EE_HEALTH_DONE__")) + "\n"


def mount(path="/overlay", filesystem="jffs2", row=None):
    return (
        f"S\t{path}\t{filesystem}",
        "Filesystem 1024-blocks Used Available Capacity Mounted on",
        row or f"/dev/mtdblock5 1000 400 550 43% {path}",
        "E",
    )


def test_memory_uses_available_instead_of_free_and_keeps_units():
    health = parse_health(sample("M\tMemTotal:\t1000\tkB", "M\tMemAvailable:\t600\tkB", "M\tMemFree:\t200\tkB"))
    assert health["memory"] == {
        "total_bytes": 1_024_000,
        "available_bytes": 614_400,
        "used_bytes": 409_600,
        "used_percent": 40,
        "free_bytes": 204_800,
    }


def test_old_kernel_without_memavailable_keeps_usage_unknown():
    memory = parse_health(sample("M\tMemTotal:\t1000\tkB", "M\tMemFree:\t250\tkB"))["memory"]
    assert memory["total_bytes"] == 1_024_000
    assert memory["free_bytes"] == 256_000
    assert memory["available_bytes"] is None
    assert memory["used_bytes"] is None
    assert memory["used_percent"] is None


def test_measured_zero_available_is_not_missing():
    memory = parse_health(sample("M\tMemTotal:\t1000\tkB", "M\tMemAvailable:\t0\tkB", "M\tMemFree:\t0\tkB"))["memory"]
    assert memory["available_bytes"] == memory["free_bytes"] == 0
    assert memory["used_percent"] == 100


@pytest.mark.parametrize("available", ["1001", "-1", "x", "9223372036854775807"])
def test_invalid_available_does_not_become_zero(available):
    memory = parse_health(sample("M\tMemTotal:\t1000\tkB", f"M\tMemAvailable:\t{available}\tkB"))["memory"]
    assert memory["available_bytes"] is None
    assert memory["used_bytes"] is None


@pytest.mark.parametrize("records", [
    ("M\tMemTotal:\t1000\tMB",),
    ("M\tMemTotal:\t0\tkB",),
    ("M\tMemTotal:\t9223372036854775807\tkB",),
    ("M\tMemTotal:\t1000\tkB", "M\tMemTotal:\t2000\tkB"),
])
def test_invalid_or_duplicate_memory_total_is_unknown(records):
    assert parse_health(sample(*records))["memory"] is None


def test_actual_sensor_labels_and_units_are_preserved_without_role_inference():
    temperatures = parse_health(sample(
        "T\tthermal_zone0\tboard\t43500",
        "H\thwmon0/temp1\ttmp102\tAmbient\t27000\t0\t2",
        "H\thwmon0/temp2\ttmp102\t\t-5500\t0\t",
        "T\tthermal_zone1\t\t0",
    ))["temperatures"]
    assert temperatures == [
        {"id": "thermal_zone0", "label": "board", "celsius": 43.5, "source": "thermal_zone"},
        {"id": "hwmon0/temp1", "label": "tmp102 / Ambient", "celsius": 27, "source": "hwmon"},
        {"id": "hwmon0/temp2", "label": "tmp102 / Sensor 2", "celsius": -5.5, "source": "hwmon"},
        {"id": "thermal_zone1", "label": "thermal_zone1", "celsius": 0, "source": "thermal_zone"},
    ]
    assert all("role" not in sensor for sensor in temperatures)


def test_hwmon_collector_reads_device_channels_once_and_respects_class_fault(tmp_path):
    shell = shutil.which("sh")
    if os.name == "nt":
        git_shell = Path(os.environ["PROGRAMFILES"]) / "Git/bin/sh.exe"
        shell = str(git_shell) if git_shell.is_file() else shell
    if not shell:
        pytest.skip("Shell unavailable for the sysfs fixture")
    hwmon = tmp_path / "hwmon/hwmon0"
    device = hwmon / "device"
    device.mkdir(parents=True)
    for directory in (hwmon, device):
        (directory / "name").write_text("adt7475\n", newline="\n")
    for channel, reading in enumerate((71750, 66750, 66500, 99000), 1):
        (device / f"temp{channel}_input").write_text(f"{reading}\n", newline="\n")
    (hwmon / "temp4_input").write_text("99000\n", newline="\n")
    (hwmon / "temp4_fault").write_text("1\n", newline="\n")
    command = HEALTH_COMMAND.replace("/sys/class/", "./").replace("/proc/", "./")
    output = subprocess.run([shell, "-c", command], cwd=tmp_path, check=True,
                            capture_output=True, text=True, timeout=10).stdout
    sensors = parse_health(output)["temperatures"]
    assert [(item["label"], item["celsius"]) for item in sensors] == [
        ("adt7475 / Sensor 1", 71.75), ("adt7475 / Sensor 2", 66.75), ("adt7475 / Sensor 3", 66.5),
    ]
    assert output.count("hwmon0/temp4") == 1


@pytest.mark.parametrize("record", [
    "H\thwmon0/temp1\tdriver\tBoard\t43000\t1\t2",
    "H\thwmon0/temp1\tdriver\tBoard\t43000\t?\t2",
    "H\thwmon0/temp1\tdriver\tBoard\t43000\t0\t4",
    "H\thwmon0/temp1\tdriver\tBoard\t43000\t0\t?",
    "H\thwmon0/temp1\tdriver\tBoard\t43000\t0\t99",
    "T\tthermal_zone0\tboard\t2147483647",
    "T\tthermal_zone0\tboard\t-273151",
    "T\tthermal_zone0\tboard\t999999",
    "T\tthermal_zone0\tboard\t43.5",
    "T\tthermal_zone0\tboard\x1b[31m\t43000",
    "T\tthermal_zone0/other\tboard\t43000",
])
def test_faulted_unknown_units_or_malformed_temperatures_are_omitted(record):
    assert parse_health(sample(record))["temperatures"] == []


def test_storage_reports_mounted_capacity_and_preserves_reserved_space():
    storage = parse_health(sample(*mount()))["storage"]
    assert storage == [{
        "mount": "/overlay",
        "filesystem": "jffs2",
        "total_bytes": 1_024_000,
        "used_bytes": 409_600,
        "available_bytes": 563_200,
        "used_percent": 40,
    }]
    assert "drive" not in storage[0]


@pytest.mark.parametrize("filesystem", ["squashfs", "romfs", "tmpfs", "proc", "sysfs", "overlay", "nfs", "cifs"])
def test_pseudo_readonly_or_remote_storage_is_not_reported(filesystem):
    assert parse_health(sample(*mount(filesystem=filesystem)))["storage"] == []


@pytest.mark.parametrize("row", [
    "/dev/root 0 0 0 0% /overlay",
    "/dev/root 1000 1200 0 100% /overlay",
    "/dev/root 1000 400 700 40% /overlay",
    "/dev/root 1000 -1 500 0% /overlay",
    "/dev/root 1000 400 500 40% /other",
    "/dev/root 9223372036854775807 400 500 1% /overlay",
    "server:/share 1000 400 500 40% /overlay",
    "tmpfs 1000 400 500 40% /overlay",
    "df: cannot read table of mounted file systems",
])
def test_failed_or_inconsistent_df_output_is_not_capacity(row):
    assert parse_health(sample(*mount(row=row)))["storage"] == []


def test_ubifs_mount_is_supported_without_assuming_a_drive():
    storage = parse_health(sample(*mount("/data", "ubifs", "ubi0:data 1000 0 1000 0% /data")))["storage"]
    assert storage[0]["mount"] == "/data"
    assert storage[0]["used_bytes"] == 0


@pytest.mark.parametrize("path", ["/path\\040with\\040spaces", "relative", "/path with spaces", "/path\tfield"])
def test_unsafe_or_ambiguous_mount_paths_are_omitted(path):
    assert parse_health(sample(*mount(path)))["storage"] == []


def test_missing_subsystems_do_not_become_zero_readings():
    assert parse_health(sample()) == {"memory": None, "temperatures": [], "storage": []}


def test_result_count_and_duplicate_bounds():
    sensors = [f"T\tthermal_zone{i}\tsensor{i}\t42000" for i in range(40)]
    mounts = [line for i in range(20) for line in mount(f"/volume{i}")]
    health = parse_health(sample(*sensors, *sensors[:2], *mounts, *mount("/volume0")))
    assert len(health["temperatures"]) == 32
    assert len(health["storage"]) == 16


@pytest.mark.parametrize("output", [
    "", "__EE_HEALTH_V1__\n", "__EE_HEALTH_DONE__\n", "noise\n" + sample(),
    sample() + "noise\n", sample("S\t/overlay\tjffs2"),
    sample(*(["ignored"] * 513)),
    sample("x" * MAX_HEALTH_RESPONSE),
    sample("\u2603" * (MAX_HEALTH_RESPONSE // 2)),
], ids=[
    "empty", "start-only", "end-only", "prefix", "suffix", "open-mount", "many-lines", "many-bytes", "unicode-bytes",
])
def test_incomplete_or_oversized_samples_are_rejected(output):
    with pytest.raises(HealthParseError):
        parse_health(output)
