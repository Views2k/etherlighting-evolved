import sys
from types import SimpleNamespace

import pytest

from etherlighting_evolved.desktop import hide_taskbar_button
from etherlighting_evolved.workspace_desktop import WorkspaceDesktopApp, workspace_window_geometry


def screen(width, height, x=0, y=0, *, work_area=None, scale=1):
    area = None if work_area is None else SimpleNamespace(**dict(zip(
        ("X", "Y", "Width", "Height"), work_area, strict=True)))
    return SimpleNamespace(x=x, y=y, width=width, height=height, frame=area, scale=scale)


def test_rack_window_fits_the_400_pixel_display():
    assert workspace_window_geometry([screen(1280, 400)]) == {
        "width": 1280, "height": 400, "min_size": (400, 350), "x": 0, "y": 0,
    }


def test_normal_display_keeps_existing_window_dimensions_and_centers():
    assert workspace_window_geometry([screen(1920, 1080, work_area=(0, 0, 1920, 1040))]) == {
        "width": 1280, "height": 820, "min_size": (400, 350), "x": 320, "y": 110,
    }


def test_work_area_excludes_taskbars_on_top_and_left():
    assert workspace_window_geometry([screen(1280, 400, work_area=(40, 24, 1240, 376))]) == {
        "width": 1240, "height": 376, "min_size": (400, 350), "x": 40, "y": 24,
    }


def test_minimum_size_never_exceeds_available_work_area():
    assert workspace_window_geometry([screen(380, 320)]) == {
        "width": 380, "height": 320, "min_size": (380, 320), "x": 0, "y": 0,
    }


def test_primary_screen_is_selected_when_negative_origin_monitor_is_listed_first():
    result = workspace_window_geometry([screen(1280, 400, -1280, -200), screen(1920, 1080)])
    assert result["width"] == 1280 and result["height"] == 820
    assert (result["x"], result["y"]) == (320, 130)


def test_negative_origin_is_preserved_when_it_is_the_only_screen():
    assert workspace_window_geometry([screen(1280, 400, -1280, -400)]) == {
        "width": 1280, "height": 400, "min_size": (400, 350), "x": -1280, "y": -400,
    }


@pytest.mark.parametrize("scale", [1, 1.25, 1.5, 2])
def test_logical_screen_dimensions_are_not_scaled_twice(scale):
    result = workspace_window_geometry([screen(1280, 400, scale=scale)])
    assert (result["width"], result["height"]) == (1280, 400)


@pytest.mark.parametrize("area", [(-1, 0, 1280, 400), (0, 0, 1280, 800), (0, 0, 0, 0)])
def test_invalid_work_area_falls_back_to_screen_bounds(area):
    result = workspace_window_geometry([screen(1280, 400, work_area=area)])
    assert (result["width"], result["height"], result["x"], result["y"]) == (1280, 400, 0, 0)


def test_missing_or_unusable_screens_keep_existing_default_geometry():
    for screens in ([], [screen(0, 0)], [screen(-1, 400)]):
        assert workspace_window_geometry(screens) == {"width": 1280, "height": 820, "min_size": (400, 350)}


def test_desktop_creation_uses_available_bounds_without_starting_a_real_window(monkeypatch, tmp_path):
    calls = {}

    class Event:
        def __iadd__(self, handler):
            self.handler = handler
            return self

    window = SimpleNamespace(events=SimpleNamespace(before_show=Event(), closing=Event()))

    def create_window(*args, **kwargs):
        calls["window"] = kwargs
        return window

    webview = SimpleNamespace(settings={}, screens=[screen(1280, 400, work_area=(0, 0, 1280, 352))],
                              create_window=create_window,
                              start=lambda *args, **kwargs: calls.update(start=kwargs))
    monkeypatch.setitem(sys.modules, "webview", webview)
    app = WorkspaceDesktopApp.__new__(WorkspaceDesktopApp)
    app.url, app.background, app.directory = "http://localhost/", False, tmp_path
    app._run_window()
    assert calls["window"]["height"] == 352
    assert calls["window"]["min_size"] == (400, 350)
    assert calls["start"]["gui"] == "edgechromium"
    assert app.window is window
    assert window.events.before_show.handler is hide_taskbar_button


def quit_fixture(tmp_path, server):
    import threading

    app = WorkspaceDesktopApp.__new__(WorkspaceDesktopApp)
    app.directory, app.server = tmp_path, server
    app.quit_lock, app.stopped = threading.Lock(), threading.Event()
    app.frame = app.window = None
    app.quitting = False
    notifications, stops = [], []
    app.tray = SimpleNamespace(notify=lambda *args: notifications.append(args), stop=lambda: stops.append(True))
    return app, notifications, stops


def test_dead_gateway_allows_shell_exit_and_preserves_unconfirmed_cleanup(tmp_path):
    import json

    from etherlighting_evolved.workspace_desktop import Gateway

    gateway = Gateway(tmp_path, tmp_path, demo=True, port=1)
    gateway.process = SimpleNamespace(poll=lambda: 19)
    journal = tmp_path / "output-ownership.json"
    journal.write_text('{"owner":"retained"}', encoding="utf-8")
    app, notifications, stops = quit_fixture(tmp_path, gateway)
    app.quit()
    assert app.stopped.is_set() and app.quitting
    assert stops == [True]
    assert len(notifications) == 1
    record = json.loads((tmp_path / "last-session.json").read_text("utf-8"))
    assert record["stage"] == "desktop_exit"
    assert record["outcome"] == "cleanup_unconfirmed"
    assert set(record) == {"time", "stage", "outcome", "build"}
    assert journal.read_text("utf-8") == '{"owner":"retained"}'
    app.quit()
    assert stops == [True]


def test_running_gateway_cleanup_failure_keeps_retry_available(tmp_path):
    def stop():
        raise RuntimeError("cleanup has not been confirmed")

    app, notifications, stops = quit_fixture(tmp_path, SimpleNamespace(stop=stop))
    app.quit()
    assert not app.stopped.is_set() and not app.quitting
    assert stops == [] and len(notifications) == 1
    assert not (tmp_path / "last-session.json").exists()


def test_dead_gateway_can_close_shell_even_when_status_storage_is_unavailable(tmp_path):
    from etherlighting_evolved.workspace_desktop import Gateway

    gateway = Gateway(tmp_path, tmp_path, demo=True, port=1)
    gateway.process = SimpleNamespace(poll=lambda: 19)
    blocked = tmp_path / "blocked"
    blocked.write_text("file", encoding="utf-8")
    app, _, stops = quit_fixture(blocked, gateway)
    app.quit()
    assert app.stopped.is_set() and stops == [True]
