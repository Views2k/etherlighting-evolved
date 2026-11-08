"""Private loopback service owned by the native desktop process."""

from __future__ import annotations

import asyncio
import json
import logging
import os
import socket
import sys
import threading
import traceback
from contextlib import nullcontext
from pathlib import Path

import uvicorn

from etherlighting_evolved import __version__
from etherlighting_evolved.api import create_app
from etherlighting_evolved.app_lock import AppInstanceLock, AppLockError
from etherlighting_evolved.config import Config, data_dir, load_config
from etherlighting_evolved.session import DEMO_HOSTS, LoginError
from etherlighting_evolved.transport import SimulatedTransport
from etherlighting_evolved.websocket_protocol import websocket_protocol


async def run(options: dict) -> None:
    token = options.get("token", "")
    if not isinstance(token, str) or len(token) < 32:
        raise ValueError("invalid desktop session")
    demo = options.get("demo") is True
    browser_live = options.get("browser_live") is True
    if demo and browser_live:
        raise ValueError("conflicting service modes")
    with AppInstanceLock() if browser_live else nullcontext():
        await serve(options, token, demo=demo, browser_live=browser_live)


async def serve(options: dict, token: str, *, demo: bool, browser_live: bool) -> None:
    if demo:
        isolated = Path(options["data_dir"])
        if not isolated.is_absolute():
            raise ValueError("demo storage must be absolute")
        isolated.mkdir(parents=True, exist_ok=True)
        config = Config(data_dir=isolated)
    else:
        settings = data_dir() / "ee.toml"
        config = load_config(settings if settings.is_file() else None)
    config.api.host = "127.0.0.1"
    config.api.token = token
    config.api.cors_origins = []
    config.api.static_dir = str(config.data_dir / "native-no-web")
    def preview_transport(**credentials):
        if credentials.get("host", "").strip().lower() not in DEMO_HOSTS:
            raise LoginError("preview_only", "Open the app normally to connect to a switch.", 409)
        return SimulatedTransport(lit_ports=config.switch.num_ports)

    desktop = os.environ.get("EE_DESKTOP_HOST") == "1"
    app = create_app(config, demo=demo, autoconnect=not demo and (desktop or not browser_live),
                     transport_factory=preview_transport if demo else None)
    server = uvicorn.Server(uvicorn.Config(
        app, host="127.0.0.1", port=0, access_log=False, log_config=None,
        lifespan="on", timeout_graceful_shutdown=15, ws=websocket_protocol(),
    ))
    loop = asyncio.get_running_loop()

    def stop() -> None:
        server.should_exit = True

    def monitor_parent() -> None:
        while True:
            line = sys.stdin.readline()
            if not line or line.strip() == "quit":
                loop.call_soon_threadsafe(stop)
                return

    threading.Thread(target=monitor_parent, name="desktop-lifetime", daemon=True).start()
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as listener:
        if os.name == "nt":
            listener.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
        listener.bind(("127.0.0.1", 0))
        port = listener.getsockname()[1]
        task = asyncio.create_task(server.serve(sockets=[listener]))
        while not server.started:
            if task.done():
                await task
                raise RuntimeError("service stopped before startup")
            await asyncio.sleep(0.025)
        print(json.dumps({"ready": True, "port": port, "version": __version__}), flush=True)
        await task


def report_failure(error: BaseException) -> None:
    frames = traceback.extract_tb(error.__traceback__)[-8:]
    record = {
        "event": "engine_failure",
        "error_type": type(error).__name__[:80],
        "frames": [{"module": Path(frame.filename).name[:80],
                    "function": frame.name[:80], "line": frame.lineno}
                   for frame in frames],
    }
    print(json.dumps(record), file=sys.stderr, flush=True)


class DiagnosticLogHandler(logging.Handler):
    def emit(self, record: logging.LogRecord) -> None:
        if record.exc_info and isinstance(record.exc_info[1], BaseException):
            report_failure(record.exc_info[1])
            return
        known = {"etherlighting_evolved", "uvicorn", "asyncio", "websockets"}
        logger = record.name if record.name.split(".")[0] in known else "other"
        diagnostic = {
            "event": "engine_log", "level": record.levelname, "logger": logger[:80],
            "frames": [{"module": Path(record.pathname).name[:80],
                        "function": record.funcName[:80], "line": record.lineno}],
        }
        print(json.dumps(diagnostic), file=sys.stderr, flush=True)


def configure_logging() -> None:
    logging.disable(logging.NOTSET)
    logging.basicConfig(level=logging.WARNING, handlers=[DiagnosticLogHandler()], force=True)


def main() -> int:
    # Startup options and credentials never enter command-line arguments or logs.
    configure_logging()
    try:
        options = json.loads(sys.stdin.readline())
        if not isinstance(options, dict):
            raise ValueError("invalid desktop options")
        asyncio.run(run(options))
        return 0
    except AppLockError as exc:
        print(json.dumps({"ready": False, "code": exc.code, "error": str(exc)}), flush=True)
        return 1
    except Exception as error:
        report_failure(error)
        print(json.dumps({"ready": False, "error": "Lighting service could not start."}), flush=True)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
