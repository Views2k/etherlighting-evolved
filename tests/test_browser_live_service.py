import importlib.util
from pathlib import Path

import pytest

from etherlighting_evolved.app_lock import AlreadyRunningError


@pytest.fixture
def service():
    source = Path(__file__).resolve().parents[1] / "packaging" / "native_service.py"
    spec = importlib.util.spec_from_file_location("browser_service_fixture", source)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


@pytest.mark.anyio
async def test_browser_live_holds_lock_for_entire_service(service, monkeypatch):
    events = []

    class Lock:
        def __enter__(self):
            events.append("locked")

        def __exit__(self, *_):
            events.append("released")

    async def serve(options, token, **kwargs):
        assert events == ["locked"]
        assert kwargs == {"demo": False, "browser_live": True}
        events.append("served")

    monkeypatch.setattr(service, "AppInstanceLock", Lock)
    monkeypatch.setattr(service, "serve", serve)
    await service.run({"token": "fixture-" * 5, "browser_live": True})
    assert events == ["locked", "served", "released"]


@pytest.mark.anyio
async def test_browser_live_refuses_an_existing_writer_before_service_start(service, monkeypatch):
    class Locked:
        def __enter__(self):
            raise AlreadyRunningError("fixture already owns output")

        def __exit__(self, *_):
            pytest.fail("unacquired lock released")

    async def serve(*args, **kwargs):
        pytest.fail("service started without ownership")

    monkeypatch.setattr(service, "AppInstanceLock", Locked)
    monkeypatch.setattr(service, "serve", serve)
    with pytest.raises(AlreadyRunningError):
        await service.run({"token": "fixture-" * 5, "browser_live": True})


@pytest.mark.anyio
@pytest.mark.parametrize("options", [{}, {"demo": True, "data_dir": "unused"}])
async def test_existing_native_and_demo_launches_do_not_take_host_mutex(service, monkeypatch, options):
    def lock():
        pytest.fail("host already owns the native app mutex")

    async def serve(options, token, **kwargs):
        assert kwargs == {"demo": options.get("demo", False), "browser_live": False}

    monkeypatch.setattr(service, "AppInstanceLock", lock)
    monkeypatch.setattr(service, "serve", serve)
    await service.run({"token": "fixture-" * 5, **options})


@pytest.mark.anyio
async def test_demo_and_live_are_mutually_exclusive(service):
    with pytest.raises(ValueError, match="conflicting"):
        await service.run({"token": "fixture-" * 5, "demo": True, "browser_live": True})


@pytest.mark.anyio
@pytest.mark.parametrize("browser_live,autoconnect", [(True, False), (False, True)])
async def test_live_browser_waits_for_explicit_restore_and_keeps_production_config(
        service, config, monkeypatch, browser_live, autoconnect):
    captured = {}

    def create_app(settings, **kwargs):
        assert settings is config
        captured.update(kwargs)
        return object()

    class Server:
        started = True

        def __init__(self, config):
            pass

        async def serve(self, **kwargs):
            pass

    class Thread:
        def __init__(self, **kwargs):
            pass

        def start(self):
            pass

    class Listener:
        def __enter__(self):
            return self

        def __exit__(self, *_):
            pass

        def setsockopt(self, *args):
            pass

        def bind(self, address):
            assert address == ("127.0.0.1", 0)

        def getsockname(self):
            return ("127.0.0.1", 12345)

    monkeypatch.setattr(service, "data_dir", lambda: config.data_dir)
    monkeypatch.setattr(service, "load_config", lambda _: config)
    monkeypatch.setattr(service, "create_app", create_app)
    monkeypatch.setattr(service.uvicorn, "Server", Server)
    monkeypatch.setattr(service.threading, "Thread", Thread)
    monkeypatch.setattr(service.socket, "socket", lambda *args: Listener())
    await service.serve({"data_dir": "ignored-demo-folder"}, "fixture-" * 5, demo=False, browser_live=browser_live)
    assert captured == {"demo": False, "autoconnect": autoconnect, "transport_factory": None}
    assert config.api.host == "127.0.0.1"
    assert config.api.cors_origins == []
