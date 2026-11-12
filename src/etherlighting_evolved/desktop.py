"""The Windows app.

Runs the EE server in the background, shows the UI in its own window and keeps an icon in the notification area.
Closing the window only hides it, because EE has to keep running for the LEDs to stay on. Quit in the icon's menu
stops EE, which hands the LEDs back to the switch.

The UI draws its own title bar. The window's Windows title bar is removed (see frame.py), and the page controls the
window through `window.pywebview.api`, which has two methods: window_state() and window_action(action).

    EtherlightingEvolved.exe               open the window
    EtherlightingEvolved.exe --background  start with only the notification area icon (used at Windows sign-in)
    EtherlightingEvolved.exe --quit        stop a running copy (used by the installer)
"""

from __future__ import annotations

import argparse
import contextlib
import ctypes
import json
import logging
import logging.handlers
import os
import socket
import sys
import threading
import time
import urllib.request
import webbrowser
from pathlib import Path

from . import PROJECT_NAME, __version__
from .config import data_dir, load_config

log = logging.getLogger(__name__)

APP_ID = "EtherlightingEvolved"  # name of the single-instance mutex; the installer checks it too
RUN_KEY = r"Software\Microsoft\Windows\CurrentVersion\Run"
RUN_VALUE = PROJECT_NAME
PREFERRED_PORT = 8787
WINDOW_ACTIONS = ("close", "minimize", "toggle_maximize", "drag")

# Dragging the UI's title starts Windows' own move loop once the mouse has moved a few pixels, so the window snaps
# to screen edges like any other. Clicks and double-clicks are left to the page.
NATIVE_DRAG_SCRIPT = """
(() => {
  if (window.__eeNativeDrag) return;
  window.__eeNativeDrag = true;
  let start = null;
  addEventListener('mousedown', event => {
    const onTitle = event.button === 0 && event.detail === 1 && event.target.closest('.pywebview-drag-region');
    start = onTitle ? [event.screenX, event.screenY] : null;
  }, true);
  addEventListener('mousemove', event => {
    if (!start) return;
    if (!(event.buttons & 1)) { start = null; return; }
    if (Math.abs(event.screenX - start[0]) + Math.abs(event.screenY - start[1]) < 4) return;
    start = null;
    window.pywebview.api.window_action('drag');
  }, true);
  addEventListener('mouseup', () => { start = null; }, true);
})();
"""
ERROR_ALREADY_EXISTS = 183
SYNCHRONIZE = 0x00100000
WM_QUERYENDSESSION = 0x0011


def hide_taskbar_button(window) -> None:
    from .frame import keep_form_off_taskbar

    keep_form_off_taskbar(window.native)


def package_dir() -> Path:
    return Path(__file__).resolve().parent


def web_dir() -> Path:
    """The web UI: bundled next to the app when frozen, the repository's web/ folder otherwise."""
    if getattr(sys, "frozen", False):
        return Path(sys._MEIPASS) / "web"
    return package_dir().parent.parent / "web"


def can_start_with_windows() -> bool:
    return getattr(sys, "frozen", False)  # from a source checkout there's no .exe to point the Run key at


def starts_with_windows() -> bool:
    import winreg

    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY) as key:
            value, _ = winreg.QueryValueEx(key, RUN_VALUE)
    except OSError:
        return False
    return bool(value)


def set_start_with_windows(enabled: bool) -> None:
    import winreg

    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY, 0, winreg.KEY_SET_VALUE) as key:
        if enabled:
            winreg.SetValueEx(key, RUN_VALUE, 0, winreg.REG_SZ, f'"{sys.executable}" --background')
        else:
            with contextlib.suppress(FileNotFoundError):
                winreg.DeleteValue(key, RUN_VALUE)


def kernel32():
    dll = ctypes.WinDLL("kernel32", use_last_error=True)
    dll.CreateMutexW.restype = dll.OpenMutexW.restype = ctypes.c_void_p
    dll.CloseHandle.argtypes = [ctypes.c_void_p]
    return dll


def claim_single_instance():
    """A handle if this is the only running copy, or None if another copy already runs."""
    k32 = kernel32()
    handle = k32.CreateMutexW(None, False, APP_ID)
    if ctypes.get_last_error() == ERROR_ALREADY_EXISTS:
        k32.CloseHandle(handle)
        return None
    return handle


def copy_is_running() -> bool:
    k32 = kernel32()
    handle = k32.OpenMutexW(SYNCHRONIZE, False, APP_ID)
    if not handle:
        return False
    k32.CloseHandle(handle)
    return True


def instance_file() -> Path:
    return data_dir() / "desktop.json"


def ask_running_copy(action: str) -> bool:
    """Send "show" or "quit" to the copy that's already running."""
    try:
        port = int(json.loads(instance_file().read_text("utf-8"))["port"])
        request = urllib.request.Request(f"http://127.0.0.1:{port}/api/desktop/{action}", method="POST")
        urllib.request.urlopen(request, timeout=3).close()
    except (OSError, ValueError, KeyError) as exc:
        log.warning("the running copy didn't answer %r: %s", action, exc)
        return False
    return True


def quit_running_copy(timeout: float = 20.0) -> bool:
    if not copy_is_running():
        return True
    if not ask_running_copy("quit"):
        return False
    deadline = time.monotonic() + timeout
    while copy_is_running():
        if time.monotonic() > deadline:
            return False
        time.sleep(0.2)
    return True


def free_port(preferred: int) -> int:
    for port in (preferred, 0):
        with socket.socket() as sock:
            try:
                sock.bind(("127.0.0.1", port))
            except OSError:
                continue
            return sock.getsockname()[1]
    raise RuntimeError("no free port on 127.0.0.1")


def error_box(message: str) -> None:
    ctypes.windll.user32.MessageBoxW(None, message, PROJECT_NAME, 0x10)


def setup_logging() -> Path:
    # A windowed .exe has no console; libraries that print would fail without somewhere to write.
    if sys.stdout is None:
        sys.stdout = open(os.devnull, "w")  # noqa: SIM115
    if sys.stderr is None:
        sys.stderr = open(os.devnull, "w")  # noqa: SIM115
    path = data_dir() / "ee.log"
    path.parent.mkdir(parents=True, exist_ok=True)
    handler = logging.handlers.RotatingFileHandler(path, maxBytes=1_000_000, backupCount=2, encoding="utf-8")
    handler.setFormatter(logging.Formatter("%(asctime)s %(levelname)-7s %(name)s: %(message)s"))
    logging.basicConfig(level=logging.INFO, handlers=[handler], force=True)
    return path


class Server:
    """The EE server on a background thread."""

    def __init__(self, app, port: int) -> None:
        import uvicorn

        config = uvicorn.Config(app, host="127.0.0.1", port=port, log_config=None, lifespan="on")
        self.server = uvicorn.Server(config)
        self.thread = threading.Thread(target=self.server.run, name="ee-server", daemon=True)

    def start(self, timeout: float = 20.0) -> None:
        self.thread.start()
        deadline = time.monotonic() + timeout
        while not self.server.started:
            if not self.thread.is_alive() or time.monotonic() > deadline:
                raise RuntimeError("the server didn't start")
            time.sleep(0.05)

    def stop(self, timeout: float = 10.0) -> None:
        self.server.should_exit = True  # runs the shutdown, which logs out and hands the LEDs back
        self.thread.join(timeout)


class DesktopApp:
    def __init__(self, *, background: bool) -> None:
        from .api import create_app

        settings = data_dir() / "ee.toml"
        self.config = load_config(settings if settings.is_file() else None)
        self.config.api.host = "127.0.0.1"
        self.config.api.port = free_port(PREFERRED_PORT)
        self.config.api.static_dir = str(web_dir())
        self.url = f"http://127.0.0.1:{self.config.api.port}/?v={__version__}"
        self.background = background
        self.window = None
        self.tray = None
        self.quitting = False
        self.closing_for_windows = False
        self.window_failed = False
        self.stopped = threading.Event()
        self.mentioned_tray = False
        self.frame = None  # a TitleBarRemover once the UI's title bar has taken over
        self.frame_ready = threading.Event()  # set when that has worked or failed
        app = create_app(self.config, on_show=self.show, on_quit=self.quit_soon)
        self.server = Server(app, self.config.api.port)

    # Window

    def show(self) -> None:
        if self.window is None:
            webbrowser.open(self.url)
            return
        self.window.show()
        self.window.restore()
        self.set_page_visible(True)
        if not self.window.events.loaded.is_set():  # first time a window started in the background is shown
            threading.Thread(target=self.check_window_loads, daemon=True).start()

    def on_closing(self) -> bool:
        if self.quitting or self.window_failed or self.closing_for_windows:
            return True
        self.hide_to_tray()
        return False

    def hide_to_tray(self) -> None:
        self.set_page_visible(False)
        self.window.hide()
        if not self.mentioned_tray and self.tray is not None:
            self.mentioned_tray = True
            self.tray.notify("Etherlighting Evolved is still running in the notification area. "
                             "Right-click its icon to quit.", PROJECT_NAME)

    def set_page_visible(self, visible: bool) -> None:
        """Tell the browser control whether its page can be seen.

        Hiding or minimizing the window does not stop the page: it kept rendering and receiving the live stream at
        full rate while nobody could see it. The control's own visibility is switched while the window is still
        shown, which marks the page hidden, and the page is told directly as well. It then stops its animation
        and, a few seconds later, its stream.
        """
        window = self.window
        native = getattr(window, "native", None)
        if native is None:
            return

        def apply() -> None:
            control = getattr(native, "webview", None)
            if control is None:
                return
            # The getter reports False whenever the window is hidden, so the value is always assigned.
            control.Visible = visible
            core = getattr(control, "CoreWebView2", None)
            if core is not None:
                core.ExecuteScriptAsync("window.dispatchEvent(new CustomEvent('ee-host-visibility',"
                                        f"{{detail:{{hidden:{'false' if visible else 'true'}}}}}))")

        try:
            if native.InvokeRequired:
                self.on_gui(apply)
            else:
                apply()
        except Exception:
            log.warning("couldn't update the page visibility; a hidden window may keep rendering")

    def follow_window_state(self) -> None:
        """A minimized window is as invisible as one in the tray, so its page is marked hidden too."""
        native = getattr(self.window, "native", None)
        if native is None:
            return
        from System.Windows.Forms import FormWindowState

        def on_resize(sender, args) -> None:
            if bool(native.Visible):
                self.set_page_visible(native.WindowState != FormWindowState.Minimized)

        native.Resize += on_resize

    # The UI's own title bar

    def on_gui(self, fn) -> None:
        """Run fn on the window's GUI thread and wait for it to finish."""
        from System import Action

        self.window.native.Invoke(Action(fn))

    def remove_title_bar(self) -> None:
        """Let the UI's title bar replace the Windows one. If anything fails, the normal title bar stays."""
        from .frame import TitleBarRemover

        try:
            native = self.window.native
            for _ in range(200):
                if native.IsHandleCreated:
                    break
                time.sleep(0.05)
            made = []

            def install() -> None:
                frame = TitleBarRemover(native.Handle.ToInt64())
                frame.install()
                made.append(frame)

            self.on_gui(install)
            self.frame = made[0]
            self.window.events.maximized += self.send_window_state
            self.window.events.restored += self.send_window_state
            self.window.events.loaded += self.add_native_drag
            if self.window.events.loaded.is_set():
                self.add_native_drag()
            log.info("using the UI's title bar")
        except Exception:
            log.exception("couldn't remove the Windows title bar; keeping it")
            self.frame = None
        finally:
            self.frame_ready.set()

    def add_native_drag(self) -> None:
        with contextlib.suppress(Exception):
            self.window.evaluate_js(NATIVE_DRAG_SCRIPT)

    def send_window_state(self) -> None:
        """Tell the page when the window is maximized or restored, so its maximize button shows the right label."""
        if self.frame is None:
            return
        maximized = "true" if self.frame.maximized() else "false"
        with contextlib.suppress(Exception):
            self.window.evaluate_js(
                f"window.dispatchEvent(new CustomEvent('ee-window-state', {{detail: {{maximized: {maximized}}}}}))"
            )

    def window_state(self) -> dict:
        """Answers only once the Windows title bar is gone; the UI shows its own title bar after that."""
        self.frame_ready.wait(10)
        if self.frame is None:
            raise RuntimeError("the window uses the normal Windows title bar")
        return {"frameless": True, "maximized": self.frame.maximized()}

    def window_action(self, action: str) -> None:
        if action not in WINDOW_ACTIONS:
            raise ValueError(f"unknown window action {action!r}")
        self.frame_ready.wait(10)
        if self.frame is None:
            raise RuntimeError("the window uses the normal Windows title bar")
        if action in {"close", "minimize"}:
            self.hide_to_tray()
        elif action == "toggle_maximize":
            if self.frame.maximized():
                self.window.restore()
            else:
                self.window.maximize()
        elif action == "drag":
            self.on_gui(self.frame.start_drag)

    # Notification area

    def make_tray(self):
        import pystray
        from PIL import Image

        def toggle_start_with_windows(icon, item) -> None:
            try:
                set_start_with_windows(not starts_with_windows())
            except OSError as exc:
                log.warning("could not change start with Windows: %s", exc)

        menu = pystray.Menu(
            pystray.MenuItem("Open Etherlighting Evolved", lambda icon, item: self.show(), default=True),
            pystray.MenuItem("Start with Windows", toggle_start_with_windows,
                             checked=lambda item: starts_with_windows(), visible=can_start_with_windows()),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem("Quit", lambda icon, item: self.quit()),
        )
        image = Image.open(package_dir() / "assets" / "ee.png")
        icon_type = pystray.Icon
        if getattr(sys, "frozen", False) and not getattr(self, "demo", False):
            from .windows_tray import WindowsTrayIcon

            icon_type = WindowsTrayIcon
        icon = icon_type(APP_ID, image, PROJECT_NAME, menu)
        allow_session_end(icon)
        return icon

    def let_windows_close_the_app(self) -> None:
        """Closing the window normally only hides it, but when Windows signs out or shuts down, or an installer
        closes the app, it has to really exit. Otherwise Windows reports that EE is blocking the shutdown.

        pywebview doesn't say why a window is closing, so this listens on the form underneath. Its handler runs
        after pywebview's and lets the close go ahead for anything but the user's own close.
        """
        for _ in range(100):
            if self.window is None or self.window.native is not None:
                break
            time.sleep(0.1)
        if self.window is None or self.window.native is None:
            log.warning("couldn't reach the native window; Windows shutdown may have to wait for EE")
            return
        from System.Windows.Forms import CloseReason

        def on_form_closing(sender, args) -> None:
            if args.CloseReason != CloseReason.UserClosing:
                log.info("closing for Windows (%s)", args.CloseReason)
                self.closing_for_windows = True
                args.Cancel = False
                self.stop_for_windows()

        self.window.native.FormClosing += on_form_closing

    def stop_for_windows(self) -> None:
        """Return the switch lighting before Windows ends the process.

        Windows may terminate the app shortly after its window closes for a sign-out or shutdown, which used to
        leave the lighting ownership record behind and refuse the next login. A shutdown block reason asks
        Windows to wait while the lighting is handed back; the user can still end the session at once.
        """
        handle = None
        try:
            handle = ctypes.c_void_p(int(self.window.native.Handle.ToInt64()))
            user32 = ctypes.windll.user32
            user32.ShutdownBlockReasonCreate.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p]
            user32.ShutdownBlockReasonDestroy.argtypes = [ctypes.c_void_p]
            user32.ShutdownBlockReasonCreate(handle, "Returning the switch lighting to UniFi")
        except Exception:
            handle = None
        try:
            self.server.stop()
            log.info("lighting returned before the Windows session ended")
        except Exception as exc:
            log.warning("lighting cleanup was not confirmed before Windows closed EE: %s", exc)
        finally:
            if handle is not None:
                with contextlib.suppress(Exception):
                    ctypes.windll.user32.ShutdownBlockReasonDestroy(handle)

    # Lifecycle

    def quit_soon(self) -> None:
        """For the API: quitting stops the server, which can't be done from inside one of its own requests."""
        threading.Thread(target=self.quit, name="ee-quit", daemon=True).start()

    def quit(self) -> None:
        if self.quitting:
            return
        self.quitting = True
        log.info("quitting")
        self.server.stop()
        if self.window is not None:
            if self.frame is not None:
                with contextlib.suppress(Exception):
                    self.on_gui(self.frame.uninstall)
            with contextlib.suppress(Exception):
                self.window.destroy()
        if self.tray is not None:
            self.tray.stop()
        self.stopped.set()

    def run(self) -> None:
        self.server.start()
        instance_file().write_text(json.dumps({"pid": os.getpid(), "port": self.config.api.port}), "utf-8")
        log.info("%s %s running at %s", PROJECT_NAME, __version__, self.url)

        self.tray = self.make_tray()
        threading.Thread(target=self.tray.run, name="ee-tray", daemon=True).start()
        try:
            try:
                self._run_window()
            except Exception:
                log.exception("could not open the app window")
                self.window_failed = True
            if self.window_failed and not self.quitting:
                # Keep running from the notification area and use the default browser instead.
                self.window = None
                if not self.background:
                    webbrowser.open(self.url)
                self.stopped.wait()
        finally:
            self.quit()
            with contextlib.suppress(OSError):
                instance_file().unlink()

    def _run_window(self) -> None:
        import webview

        # pywebview would move the window from JavaScript when its drag region is pressed. Windows' own move loop
        # (NATIVE_DRAG_SCRIPT) replaces that, so point pywebview's handler at nothing.
        webview.settings["DRAG_REGION_SELECTOR"] = ".ee-pywebview-drag-unused"
        self.window = webview.create_window(PROJECT_NAME, self.url, width=1200, height=800, min_size=(900, 600),
                                           hidden=self.background, background_color="#151618",
                                           js_api=window_bridge(self), easy_drag=False)
        self.window.events.before_show += hide_taskbar_button
        self.window.events.closing += self.on_closing
        webview.start(self.after_window_start, private_mode=False, storage_path=str(data_dir() / "webview"))

    def after_window_start(self) -> None:
        self.let_windows_close_the_app()
        self.remove_title_bar()
        with contextlib.suppress(Exception):
            self.follow_window_state()
        if not self.background:
            self.check_window_loads()

    def check_window_loads(self, timeout: float = 20.0) -> None:
        """If WebView2 can't start, pywebview only logs it and leaves an empty window. Fall back to the browser."""
        window = self.window
        if window is None or window.events.loaded.wait(timeout):
            return
        log.error("the app window didn't load within %.0f s; opening the browser instead", timeout)
        self.window_failed = True
        window.destroy()


def allow_session_end(icon) -> None:
    """pystray's hidden window answers 0 to every message it has no handler for. For WM_QUERYENDSESSION, 0 means
    "don't end the session", so Windows would report EE as blocking shutdown and sign-out. Answer yes instead;
    the app window then closes for the session end like any other."""
    handlers = getattr(icon, "_message_handlers", None)
    if isinstance(handlers, dict):
        handlers[WM_QUERYENDSESSION] = lambda wparam, lparam: 1
    else:
        log.warning("couldn't hook the notification area icon; Windows shutdown may have to wait for EE")


def window_bridge(app: DesktopApp):
    """The object the page sees as window.pywebview.api.

    pywebview exposes an object's public attributes to the page, so this one has the two window methods and holds
    nothing else; the app is only reachable through them.
    """

    class WindowBridge:
        def window_state(self) -> dict:
            return app.window_state()

        def window_action(self, action: str) -> None:
            app.window_action(str(action))

    return WindowBridge()


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="EtherlightingEvolved", description=PROJECT_NAME)
    parser.add_argument("--background", action="store_true", help="start with only the notification area icon")
    parser.add_argument("--quit", action="store_true", help="stop the running copy and exit")
    args = parser.parse_args(argv)

    log_path = setup_logging()
    if sys.platform != "win32":
        print("The desktop app is for Windows. Run `etherlighting-evolved` and open http://localhost:8787 instead.")
        return 1
    if args.quit:
        return 0 if quit_running_copy() else 1
    if claim_single_instance() is None:
        if not args.background:
            ask_running_copy("show")
        return 0
    try:
        DesktopApp(background=args.background).run()
    except Exception as exc:
        log.exception("%s stopped with an error", PROJECT_NAME)
        error_box(f"{PROJECT_NAME} couldn't start: {exc}\n\nDetails are in {log_path}")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
