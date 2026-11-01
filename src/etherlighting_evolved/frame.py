"""Removes the Windows title bar from the app window, for the web UI's own title bar.

A borderless window can't be resized or snapped and covers the taskbar when maximized. So the window keeps its
normal frame and only the title bar is taken away: WM_NCCALCSIZE is answered so that the client area starts at the
top edge of the window. Resizing from the left, right and bottom edges and corners, Snap, the window shadow,
maximizing above the taskbar and Win+arrow keys all stay with Windows.

Dragging hands the mouse to Windows' own move loop (WM_NCLBUTTONDOWN on the caption), so dragging to a screen edge
snaps and dragging a maximized window restores it, as with any other window.

Everything here must run on the window's GUI thread.
"""

from __future__ import annotations

import ctypes
import logging
from ctypes import wintypes

log = logging.getLogger(__name__)

GWLP_WNDPROC = -4
WM_NCCALCSIZE = 0x0083
WM_NCLBUTTONDOWN = 0x00A1
HTCAPTION = 2
SM_CYFRAME = 33
SM_CXPADDEDBORDER = 92
# SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED: recalculate the frame, nothing else.
SWP_FRAME_CHANGED = 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020

LRESULT = ctypes.c_ssize_t
WNDPROC = ctypes.WINFUNCTYPE(LRESULT, wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM)


class NCCALCSIZE_PARAMS(ctypes.Structure):  # noqa: N801 (the Windows name)
    _fields_ = [("rgrc", wintypes.RECT * 3), ("lppos", ctypes.c_void_p)]


def _user32():
    dll = ctypes.WinDLL("user32", use_last_error=True)
    dll.SetWindowLongPtrW.restype = ctypes.c_void_p
    dll.SetWindowLongPtrW.argtypes = [wintypes.HWND, ctypes.c_int, ctypes.c_void_p]
    dll.CallWindowProcW.restype = LRESULT
    dll.CallWindowProcW.argtypes = [ctypes.c_void_p, wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
    dll.SendMessageW.restype = LRESULT
    dll.SendMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
    dll.SetWindowPos.argtypes = [wintypes.HWND, wintypes.HWND, ctypes.c_int, ctypes.c_int, ctypes.c_int,
                                 ctypes.c_int, wintypes.UINT]
    dll.IsZoomed.argtypes = [wintypes.HWND]
    dll.GetDpiForWindow.argtypes = [wintypes.HWND]
    dll.GetDpiForWindow.restype = wintypes.UINT
    dll.GetSystemMetricsForDpi.argtypes = [ctypes.c_int, wintypes.UINT]
    return dll


class TitleBarRemover:
    def __init__(self, hwnd: int) -> None:
        self.hwnd = hwnd
        self._user32 = _user32()
        self._proc = WNDPROC(self._wndproc)  # kept alive here; Windows calls it for as long as it's installed
        self._previous: int | None = None

    @property
    def installed(self) -> bool:
        return self._previous is not None

    def install(self) -> None:
        previous = self._user32.SetWindowLongPtrW(self.hwnd, GWLP_WNDPROC, ctypes.cast(self._proc, ctypes.c_void_p))
        if not previous:
            raise OSError(ctypes.get_last_error(), "could not take over the window procedure")
        self._previous = previous
        self._user32.SetWindowPos(self.hwnd, None, 0, 0, 0, 0, SWP_FRAME_CHANGED)

    def uninstall(self) -> None:
        if self._previous is None:
            return
        self._user32.SetWindowLongPtrW(self.hwnd, GWLP_WNDPROC, self._previous)
        self._previous = None
        self._user32.SetWindowPos(self.hwnd, None, 0, 0, 0, 0, SWP_FRAME_CHANGED)

    def maximized(self) -> bool:
        return bool(self._user32.IsZoomed(self.hwnd))

    def start_drag(self) -> None:
        """Move the window with the mouse, as if its title bar had been pressed. Returns when the button is up."""
        self._user32.ReleaseCapture()
        self._user32.SendMessageW(self.hwnd, WM_NCLBUTTONDOWN, HTCAPTION, 0)

    def _wndproc(self, hwnd, msg, wparam, lparam):
        previous = self._previous
        try:
            if msg == WM_NCCALCSIZE and wparam and previous is not None:
                params = NCCALCSIZE_PARAMS.from_address(lparam)
                top = params.rgrc[0].top
                result = self._user32.CallWindowProcW(previous, hwnd, msg, wparam, lparam)
                # Windows has made room for the title bar; give that space back to the page. A maximized window
                # hangs over the screen edge by the frame width, so keep that much at the top.
                params.rgrc[0].top = top + (self._frame_overhang() if self._user32.IsZoomed(hwnd) else 0)
                return result
        except Exception:
            log.exception("title bar handling failed; passing the message on")
        return self._user32.CallWindowProcW(previous, hwnd, msg, wparam, lparam)

    def _frame_overhang(self) -> int:
        dpi = self._user32.GetDpiForWindow(self.hwnd)
        return (self._user32.GetSystemMetricsForDpi(SM_CYFRAME, dpi)
                + self._user32.GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpi))
