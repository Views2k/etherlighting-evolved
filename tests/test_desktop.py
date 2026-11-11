import sys
import threading

import pytest

from etherlighting_evolved.desktop import WINDOW_ACTIONS, DesktopApp, window_bridge


class FakeWindow:
    def __init__(self):
        self.calls = []

    def __getattr__(self, name):
        return lambda *args: self.calls.append(name)


class FakeFrame:
    def __init__(self, maximized=False):
        self._maximized = maximized
        self.dragged = 0

    def maximized(self):
        return self._maximized

    def start_drag(self):
        self.dragged += 1


class FakeTray:
    def __init__(self):
        self.notes = []

    def notify(self, message, title):
        self.notes.append(message)


def make_app(frame):
    """A DesktopApp with only the parts the window controls touch (no server, no real window)."""
    app = DesktopApp.__new__(DesktopApp)
    app.window, app.frame, app.tray = FakeWindow(), frame, FakeTray()
    app.frame_ready = threading.Event()
    app.frame_ready.set()
    app.mentioned_tray = False
    app.on_gui = lambda fn: fn()
    return app


def test_the_page_sees_two_methods_and_nothing_else():
    bridge = window_bridge(make_app(FakeFrame()))
    public = sorted(name for name in dir(bridge) if not name.startswith("_"))
    assert public == ["window_action", "window_state"]
    assert vars(bridge) == {}  # no attributes the page could walk into


def test_state_reports_frameless_only_with_the_title_bar_removed():
    assert make_app(FakeFrame(maximized=True)).window_state() == {"frameless": True, "maximized": True}
    with pytest.raises(RuntimeError):
        make_app(None).window_state()


def test_actions():
    frame = FakeFrame()
    app = make_app(frame)
    app.window_action("minimize")
    app.window_action("toggle_maximize")
    frame._maximized = True
    app.window_action("toggle_maximize")
    app.window_action("drag")
    assert app.window.calls == ["hide", "maximize", "restore"]
    assert frame.dragged == 1


def test_close_hides_to_the_tray_and_explains_once():
    app = make_app(FakeFrame())
    app.window_action("close")
    app.window_action("close")
    assert app.window.calls == ["hide", "hide"]
    assert len(app.tray.notes) == 1


@pytest.mark.parametrize("action", ["quit", "destroy", "", "CLOSE", "close; minimize"])
def test_other_actions_are_refused(action):
    with pytest.raises(ValueError):
        make_app(FakeFrame()).window_action(action)
    assert action not in WINDOW_ACTIONS


def test_actions_are_refused_without_the_custom_title_bar():
    with pytest.raises(RuntimeError):
        make_app(None).window_action("minimize")


@pytest.mark.skipif(sys.platform != "win32", reason="Windows only")
def test_title_bar_remover_needs_a_real_window():
    from etherlighting_evolved.frame import TitleBarRemover

    with pytest.raises(OSError):
        TitleBarRemover(0).install()  # no such window; nothing is changed


@pytest.mark.skipif(sys.platform != "win32", reason="Windows only")
def test_the_tray_icon_does_not_block_windows_shutdown():
    import pystray
    from PIL import Image

    from etherlighting_evolved.desktop import WM_QUERYENDSESSION, allow_session_end

    icon = pystray.Icon("test", Image.new("RGB", (16, 16)), "test")
    assert WM_QUERYENDSESSION not in icon._message_handlers  # so pystray on its own answers 0 and blocks shutdown
    allow_session_end(icon)
    assert icon._message_handlers[WM_QUERYENDSESSION](0, 1) == 1


@pytest.mark.skipif(sys.platform != "win32", reason="Windows only")
def test_open_workspace_keeps_its_native_handle_when_removed_from_taskbar():
    import ctypes
    from ctypes import wintypes
    from types import SimpleNamespace

    import clr

    from etherlighting_evolved.desktop import hide_taskbar_button
    from etherlighting_evolved.frame import GWL_EXSTYLE, WS_EX_APPWINDOW, WS_EX_TOOLWINDOW

    clr.AddReference("System.Windows.Forms")
    from System.Windows.Forms import Form

    form = Form()
    try:
        handle = form.Handle.ToInt64()
        hide_taskbar_button(SimpleNamespace(native=form))
        assert form.Handle.ToInt64() == handle
        style = ctypes.windll.user32.GetWindowLongW(wintypes.HWND(handle), GWL_EXSTYLE)
        assert style & WS_EX_TOOLWINDOW and not style & WS_EX_APPWINDOW
    finally:
        form.Dispose()
