"""Windows host for the shared lighting workspace."""

from __future__ import annotations

import argparse
import contextlib
import hashlib
import json
import os
import queue
import secrets
import subprocess
import sys
import threading
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

from . import PROJECT_NAME
from .app_lock import AlreadyRunningError, AppInstanceLock
from .desktop import (
    DesktopApp,
    error_box,
    free_port,
    instance_file,
    quit_running_copy,
    setup_logging,
    window_bridge,
)


def request_json(url: str, *, method: str = "GET", token: str | None = None, timeout: float = 45):
    headers = {"Authorization": f"Bearer {token}"} if token else {}
    request = urllib.request.Request(url, method=method, headers=headers)
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    with opener.open(request, timeout=timeout) as response:
        return json.load(response)


def desktop_command(action: str) -> bool:
    try:
        record = json.loads(instance_file().read_text("utf-8"))
        port, token = record["port"], record.get("token")
        if type(port) is not int or not 1 <= port <= 65535:
            return False
        request_json(f"http://127.0.0.1:{port}/api/desktop/{action}", method="POST", token=token)
        return True
    except (OSError, ValueError, KeyError):
        return False


def desktop_is_running() -> bool:
    try:
        with AppInstanceLock('EtherlightingEvolved.Desktop'):
            return False
    except AlreadyRunningError:
        return True


def workspace_window_geometry(screens) -> dict:
    options = {"width": 1280, "height": 820, "min_size": (400, 350)}
    screens = [screen for screen in screens if screen.width > 0 and screen.height > 0]
    if not screens:
        return options
    screen = next((screen for screen in screens
                   if screen.x <= 0 < screen.x + screen.width
                   and screen.y <= 0 < screen.y + screen.height), screens[0])
    left, top, width, height = screen.x, screen.y, screen.width, screen.height
    area = getattr(screen, "frame", None)
    if (area is not None and all(hasattr(area, name) for name in ("X", "Y", "Width", "Height"))
            and area.Width > 0 and area.Height > 0 and left <= area.X and top <= area.Y
            and area.X + area.Width <= left + width and area.Y + area.Height <= top + height):
        left, top, width, height = area.X, area.Y, area.Width, area.Height
    # pywebview exposes logical pixels and applies the display scale itself.
    initial_width, initial_height = min(1280, int(width)), min(820, int(height))
    options.update(width=initial_width, height=initial_height,
                   min_size=(min(400, initial_width), min(350, initial_height)),
                   x=int(left + (width - initial_width) // 2),
                   y=int(top + (height - initial_height) // 2))
    return options


class Gateway:
    def __init__(self, root: Path, directory: Path, *, demo: bool, port: int):
        self.root, self.directory, self.demo, self.port = root, directory, demo, port
        self.process = None

    def start(self):
        env = dict(os.environ)
        for key in list(env):
            if key.startswith("EE_") or key.startswith("PYTHON") or key in {"NODE_OPTIONS", "NODE_PATH"}:
                env.pop(key)
        env.update(EE_RUNTIME_MODE="packaged", EE_PREVIEW_LIVE="0" if self.demo else "1",
                   EE_PREVIEW_PORT=str(self.port), EE_WORKSPACE_DATA_DIR=str(self.directory), EE_DESKTOP_HOST="1")
        self.process = subprocess.Popen(
            [str(self.root / "runtime/node.exe"), str(self.root / "preview/server.mjs")],
            cwd=self.root, env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL, text=True, encoding="utf-8",
            creationflags=subprocess.CREATE_NO_WINDOW,
        )
        replies = queue.Queue()

        def drain():
            for line in self.process.stdout:
                replies.put(line.strip())
            replies.put(None)

        threading.Thread(target=drain, daemon=True, name="workspace-startup").start()
        try:
            line = replies.get(timeout=45)
            if line != f"Browser preview: http://127.0.0.1:{self.port}":
                raise RuntimeError("Close the other EE workspace before opening this app.")
            result = request_json(f"http://127.0.0.1:{self.port}/api/workspace")
            if result.get("live") != (not self.demo):
                raise RuntimeError("The lighting workspace could not be verified.")
        except Exception:
            self.process.stdin.close()
            raise

    def stop(self):
        if self.process is None:
            return
        code = self.process.poll()
        if code is not None:
            if code != 0:
                raise RuntimeError("The workspace stopped without confirming lighting cleanup.")
            return
        result = request_json(f"http://127.0.0.1:{self.port}/api/workspace/close", method="POST")
        if result.get("closed") is not True:
            raise RuntimeError("Lighting cleanup is not confirmed. Try Quit again.")
        self.process.stdin.close()
        if self.process.wait(timeout=25) != 0:
            raise RuntimeError("The workspace did not close cleanly.")


class WorkspaceDesktopApp(DesktopApp):
    def __init__(self, *, root: Path, directory: Path, background: bool, demo: bool, verify: Path | None = None):
        self.background, self.demo, self.verify = background, demo, verify
        self.window = self.tray = self.frame = None
        self.quitting = self.closing_for_windows = self.window_failed = self.mentioned_tray = False
        self.stopped, self.frame_ready = threading.Event(), threading.Event()
        self.quit_lock = threading.Lock()
        self.directory = directory
        self.directory.mkdir(parents=True, exist_ok=True)
        self.server = Gateway(root, directory, demo=demo, port=free_port(4180))
        self.url = f"http://127.0.0.1:{self.server.port}/"
        self.control = None
        self.control_token = secrets.token_hex(32)

    def start_control(self):
        owner = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *_):
                pass

            def do_POST(self):
                allowed_host = f"127.0.0.1:{self.server.server_port}"
                authorized = secrets.compare_digest(
                    self.headers.get("Authorization", ""), f"Bearer {owner.control_token}")
                if (self.headers.get("Host") != allowed_host or self.headers.get("Origin")
                        or not authorized):
                    self.send_error(403)
                    return
                if self.path not in {"/api/desktop/show", "/api/desktop/quit"}:
                    self.send_error(404)
                    return
                if self.path.endswith("show"):
                    owner.show()
                else:
                    threading.Thread(target=owner.quit, daemon=True).start()
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.end_headers()
                self.wfile.write(b'{"accepted":true}')

        self.control = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        threading.Thread(target=self.control.serve_forever, daemon=True, name="desktop-actions").start()
        if not self.demo:
            record = {"pid": os.getpid(), "port": self.control.server_port, "token": self.control_token}
            instance_file().write_text(json.dumps(record), "utf-8")

    def quit(self):
        if not self.quit_lock.acquire(blocking=False):
            return
        try:
            if self.stopped.is_set():
                return
            self.quitting = True
            try:
                self.server.stop()
            except Exception:
                self.quitting = False
                if self.tray:
                    self.tray.notify("Lighting cleanup is not confirmed. Try Quit again.", PROJECT_NAME)
                return
            if self.frame and self.window:
                with contextlib.suppress(Exception):
                    self.on_gui(self.frame.uninstall)
            if self.window:
                with contextlib.suppress(Exception):
                    self.window.destroy()
            if self.tray:
                self.tray.stop()
            self.stopped.set()
        finally:
            self.quit_lock.release()

    def _run_window(self):
        import webview

        webview.settings["DRAG_REGION_SELECTOR"] = ".ee-pywebview-drag-unused"
        geometry = workspace_window_geometry(webview.screens)
        self.window = webview.create_window(
            PROJECT_NAME, self.url, **geometry,
            hidden=self.background, background_color="#1b1923", js_api=window_bridge(self), easy_drag=False,
        )
        self.window.events.closing += self.on_closing
        webview.start(self.after_window_start, gui="edgechromium", private_mode=False,
                      storage_path=str(self.directory / "webview"))

    def after_window_start(self):
        super().after_window_start()
        if self.verify:
            threading.Thread(target=self.verify_demo, daemon=True, name="desktop-verification").start()

    def verify_demo(self):
        import time

        result = {"passed": False, "pages": []}
        try:
            result['phase'] = 'workspace_and_window_controls'
            deadline = time.monotonic() + 30
            while time.monotonic() < deadline:
                if self.window.evaluate_js(
                    "Boolean(document.querySelector('[data-port]')) && "
                    "document.documentElement.classList.contains('native-window') && "
                    "document.querySelectorAll('.traffic-lights button').length === 3"
                ):
                    break
                time.sleep(.1)
            else:
                raise RuntimeError("The demo workspace and native window controls did not load.")
            result['phase'] = 'isolated_session'
            session = request_json(self.url + "api/session")
            assert session["simulated"] is True
            assert session["has_saved_login"] is False
            assert self.window.evaluate_js("document.documentElement.classList.contains('native-window')")
            assert self.window.evaluate_js("document.querySelectorAll('.traffic-lights button').length") == 3
            for page in ["Lighting", "Effects", "Automation", "Profiles", "Help", "Settings"]:
                result['phase'] = page
                self.window.evaluate_js(f"document.querySelector('[data-page=\"{page}\"]').click()")
                deadline = time.monotonic() + 10
                while time.monotonic() < deadline:
                    if self.window.evaluate_js("document.title").startswith(page):
                        break
                    time.sleep(.1)
                else:
                    raise RuntimeError("Page did not open.")
                time.sleep(.2)
                result["pages"].append(page)
            result["passed"] = True
            result.pop('phase')
        except Exception as exc:
            result["error"] = type(exc).__name__
        finally:
            self.verify.parent.mkdir(parents=True, exist_ok=True)
            self.verify.write_text(json.dumps(result, indent=2), "utf-8")
            self.quit()

    def run(self):
        import webbrowser

        self.server.start()
        self.start_control()
        self.tray = self.make_tray()
        threading.Thread(target=self.tray.run, daemon=True, name="ee-tray").start()
        try:
            try:
                self._run_window()
            except Exception:
                self.window_failed = True
            if self.window_failed and not self.quitting:
                self.window = None
                if self.verify:
                    self.verify.write_text('{"passed":false,"error":"window_failed"}', "utf-8")
                    self.quit()
                else:
                    if not self.background:
                        webbrowser.open(self.url)
                    self.stopped.wait()
        finally:
            self.quit()
            if not self.stopped.is_set():
                self.stopped.wait()
            if self.control:
                self.control.shutdown()
                self.control.server_close()
            if not self.demo:
                with contextlib.suppress(OSError):
                    instance_file().unlink()


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="EtherlightingEvolved", description=PROJECT_NAME)
    parser.add_argument("--background", action="store_true")
    parser.add_argument("--quit", action="store_true")
    parser.add_argument("--demo", action="store_true")
    parser.add_argument("--data-dir", type=Path)
    parser.add_argument("--verify", type=Path, help=argparse.SUPPRESS)
    args = parser.parse_args(argv)
    if (args.data_dir or args.verify) and not args.demo:
        parser.error("Isolated data and verification require --demo.")
    setup_logging()
    if args.quit:
        if desktop_command("quit"):
            import time

            from .desktop import copy_is_running
            deadline = time.monotonic() + 60
            while (copy_is_running() or desktop_is_running()) and time.monotonic() < deadline:
                time.sleep(.1)
            return 0 if not copy_is_running() and not desktop_is_running() else 1
        return 0 if quit_running_copy() else 1
    root = (Path(sys.executable).resolve().parent if getattr(sys, "frozen", False)
            else Path(__file__).resolve().parents[2])
    default_data = Path(os.environ['LOCALAPPDATA']) / PROJECT_NAME / ('demo' if args.demo else 'workspace')
    directory = (args.data_dir or default_data).resolve()
    suffix = '.Demo.' + hashlib.sha256(str(directory).encode()).hexdigest()[:16] if args.demo else ''
    mutex = 'EtherlightingEvolved.Desktop' + suffix
    try:
        with AppInstanceLock(mutex):
            WorkspaceDesktopApp(root=root, directory=directory, background=args.background,
                                demo=args.demo, verify=args.verify).run()
    except AlreadyRunningError:
        if not args.background and not args.demo:
            desktop_command("show")
    except Exception:
        error_box("EE could not open. Close any other EE workspace, then try again.")
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
