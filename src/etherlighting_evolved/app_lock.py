"""Single-controller ownership shared with the Windows desktop launchers."""

from __future__ import annotations

import ctypes
import sys
from ctypes import wintypes

APP_MUTEX = "EtherlightingEvolved"
ERROR_ALREADY_EXISTS = 183


class AppLockError(RuntimeError):
    code = "instance_unavailable"


class AlreadyRunningError(AppLockError):
    code = "instance_running"


class AppInstanceLock:
    def __init__(self, name: str = APP_MUTEX) -> None:
        self.name = name
        self._handle = None
        self._kernel = None

    def __enter__(self):
        if sys.platform != "win32":
            raise AppLockError("Switch control requires the Windows app lock.")
        if self._handle is not None:
            raise AppLockError("The app lock is already held.")
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.CreateMutexW.argtypes = [ctypes.c_void_p, wintypes.BOOL, wintypes.LPCWSTR]
        kernel.CreateMutexW.restype = wintypes.HANDLE
        kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        kernel.CloseHandle.restype = wintypes.BOOL
        ctypes.set_last_error(0)
        handle = kernel.CreateMutexW(None, False, self.name)
        error = ctypes.get_last_error()
        if not handle:
            raise AppLockError("Could not reserve switch control. Close EE and try again.")
        if error == ERROR_ALREADY_EXISTS:
            kernel.CloseHandle(handle)
            raise AlreadyRunningError("EE is already running. Quit it before starting another lighting controller.")
        self._handle, self._kernel = handle, kernel
        return self

    def __exit__(self, *_):
        handle, self._handle = self._handle, None
        if handle is not None:
            self._kernel.CloseHandle(handle)
        self._kernel = None
