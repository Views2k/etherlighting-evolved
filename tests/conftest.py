import pytest

from etherlighting_evolved import ownership
from etherlighting_evolved.config import Config


@pytest.fixture(autouse=True)
def isolated_ownership_journal(tmp_path, monkeypatch):
    monkeypatch.setattr(ownership, "default_journal_dir", lambda: tmp_path / "ownership")


@pytest.fixture
def config(tmp_path):
    return Config(data_dir=tmp_path / "data", base_dir=tmp_path)


@pytest.fixture
def anyio_backend():
    return "asyncio"
