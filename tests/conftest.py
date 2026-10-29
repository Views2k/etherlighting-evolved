import pytest

from etherlighting_evolved.config import Config


@pytest.fixture
def config(tmp_path):
    return Config(data_dir=tmp_path / "data", base_dir=tmp_path)


@pytest.fixture
def anyio_backend():
    return "asyncio"
