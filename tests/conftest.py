import os
import shutil
from pathlib import Path
from typing import NamedTuple

import pytest

from etherlighting_evolved import ownership
from etherlighting_evolved.config import Config


class PosixShell(NamedTuple):
    path: str
    # PATH starts with the shell's own tools (awk, sed, nohup), as it would on the switch.
    env: dict[str, str]


def _find_posix_shell():
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


@pytest.fixture(scope="session")
def posix_shell():
    path = _find_posix_shell()
    if path is None:
        # The switch-side scripts are tested only through a shell, so a CI run must not pass without one.
        if os.environ.get("CI"):
            pytest.fail("A POSIX shell is required in CI to test the switch-side scripts")
        pytest.skip("A POSIX shell is required to test the switch-side scripts")
    return PosixShell(path, {**os.environ, "PATH": os.path.dirname(path) + os.pathsep + os.environ.get("PATH", "")})


@pytest.fixture(autouse=True)
def isolated_ownership_journal(tmp_path, monkeypatch):
    monkeypatch.setattr(ownership, "default_journal_dir", lambda: tmp_path / "ownership")


@pytest.fixture
def config(tmp_path):
    return Config(data_dir=tmp_path / "data")


@pytest.fixture
def anyio_backend():
    return "asyncio"
