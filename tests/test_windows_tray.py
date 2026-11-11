import sys
from uuid import UUID

import pytest


@pytest.mark.skipif(sys.platform != "win32", reason="Windows notification area")
def test_installed_tray_identity_survives_relaunch_and_preserves_notifications(monkeypatch):
    from pystray._util import win32

    from etherlighting_evolved.windows_tray import WindowsTrayIcon

    messages = []
    monkeypatch.setattr(win32, "Shell_NotifyIcon", lambda code, data: messages.append((code, data)))
    monkeypatch.setattr(WindowsTrayIcon, "_register_class", lambda self: 0)
    monkeypatch.setattr(WindowsTrayIcon, "_assert_icon_handle", lambda self: None)
    for hwnd in (41, 42):
        icon = WindowsTrayIcon("EE", title="Etherlighting Evolved")
        icon._hwnd = hwnd
        icon._show()
        icon._notify("Running in the tray", "EE")
        icon._hide()

        added, updated, removed = messages[-3:]
        assert [added[0], updated[0], removed[0]] == [win32.NIM_ADD, win32.NIM_MODIFY, win32.NIM_DELETE]
        assert added[1].uFlags == win32.NIF_MESSAGE | win32.NIF_TIP | win32.NIF_ICON | win32.NIF_GUID
        assert added[1].uCallbackMessage == win32.WM_NOTIFY
        assert added[1].szTip == "Etherlighting Evolved"
        assert updated[1].uFlags == win32.NIF_INFO | win32.NIF_GUID
        assert (updated[1].szInfo, updated[1].szInfoTitle) == ("Running in the tray", "EE")
        assert removed[1].uFlags == win32.NIF_GUID
        for _, data in (added, updated, removed):
            assert data.hWnd == hwnd
            assert UUID(bytes_le=bytes(data.guidItem)) == UUID("7982f283-0a47-45ce-9140-36056b57dc94")
