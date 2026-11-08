"""Exercise the shipped player loop with scriptable uptime and an isolated LED sink."""

import os
import shutil
import subprocess
import time
from pathlib import Path

import pytest

from etherlighting_evolved import device_playback


def eventually(predicate, timeout=12):
    deadline = time.monotonic() + timeout
    while not predicate():
        if time.monotonic() >= deadline:
            pytest.fail("The switch fixture did not reach its expected output state")
        time.sleep(0.05)


def test_close_lost_session_and_expired_lease_resume_without_desktop(tmp_path):
    shell = (Path(os.environ.get("PROGRAMFILES", "C:/Program Files")) / "Git/bin/bash.exe"
             if os.name == "nt" else Path(shutil.which("sh") or "/bin/sh"))
    if not shell.exists():
        pytest.skip("POSIX shell unavailable")
    runtime = Path(device_playback.__file__).with_name("resident")
    (tmp_path / "player.sh").write_text((runtime / "player.sh").read_text().replace("/proc/", "./proc/"),
                                        encoding="ascii", newline="\n")
    shutil.copyfile(runtime / "frames.awk", tmp_path / "frames.awk")
    (tmp_path / "config").write_text("".join(f"{p} 0 {p} 0 0 1 100 1 10\n" for p in range(1, 11)),
                                     encoding="ascii", newline="\n")
    (tmp_path / "palette").write_text(" ".join(["10"] * 60) + "\n", encoding="ascii", newline="\n")
    proc = tmp_path / "proc"
    holder = proc / "42"
    holder.mkdir(parents=True)
    (proc / "uptime").write_text("50.10 0\n", encoding="ascii", newline="\n")
    (proc / "stat").write_text("cpu 1 2 3 4 5 6 7 8\n", encoding="ascii", newline="\n")
    led = tmp_path / "ubus"
    led.write_text("#!/bin/sh\necho frame >> led.log\necho '{\"set\":\"ok\"}'\n", encoding="ascii", newline="\n")
    led.chmod(0o755)
    process = subprocess.Popen([str(shell), "-c", 'export PATH="$PWD:$PATH"; exec sh player.sh'], cwd=tmp_path,
                               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    output = tmp_path / "led.log"
    ack = tmp_path / "desktop.ack"
    try:
        eventually(lambda: (tmp_path / "status").exists() and (tmp_path / "status").read_text().strip() == "playing")
        for case in ("closed", "connection_lost", "expired"):
            (holder / "stat").write_text(" ".join(["0"] * 21 + ["17"]) + "\n", encoding="ascii", newline="\n")
            request = tmp_path / "desktop.request"
            request.write_text(f"{case} 74 42 17\n", encoding="ascii", newline="\n")
            eventually(lambda case=case: ack.exists() and ack.read_text().strip() == case)
            previous = output.stat().st_size
            time.sleep(1.15)
            assert output.stat().st_size == previous
            if case == "closed":
                request.unlink()
            elif case == "connection_lost":
                (holder / "stat").unlink()
            else:
                (proc / "uptime").write_text("75.00 0\n", encoding="ascii", newline="\n")
            eventually(lambda previous=previous: output.stat().st_size > previous and not ack.exists())
    finally:
        (tmp_path / "stop").touch()
        process.wait(timeout=12)
    assert process.returncode == 0
