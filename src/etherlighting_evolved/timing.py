"""Monotonic deadlines and interruptible waits for animation and command pacing."""

from __future__ import annotations

import asyncio
import contextlib
import ctypes
import math
import sys
import threading
import time
from functools import lru_cache


def monotonic() -> float:
    return time.perf_counter()


@lru_cache(maxsize=1)
def _windows_api():
    from ctypes import wintypes

    api = ctypes.WinDLL("kernel32", use_last_error=True)
    api.CreateWaitableTimerExW.argtypes = [ctypes.c_void_p, wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD]
    api.CreateWaitableTimerExW.restype = wintypes.HANDLE
    api.CreateEventW.argtypes = [ctypes.c_void_p, wintypes.BOOL, wintypes.BOOL, wintypes.LPCWSTR]
    api.CreateEventW.restype = wintypes.HANDLE
    api.SetWaitableTimer.argtypes = [wintypes.HANDLE, ctypes.POINTER(ctypes.c_longlong), wintypes.LONG,
                                    ctypes.c_void_p, ctypes.c_void_p, wintypes.BOOL]
    api.SetWaitableTimer.restype = wintypes.BOOL
    api.SetEvent.argtypes = [wintypes.HANDLE]
    api.SetEvent.restype = wintypes.BOOL
    api.WaitForMultipleObjects.argtypes = [wintypes.DWORD, ctypes.POINTER(wintypes.HANDLE),
                                          wintypes.BOOL, wintypes.DWORD]
    api.WaitForMultipleObjects.restype = wintypes.DWORD
    api.CloseHandle.argtypes = [wintypes.HANDLE]
    api.CloseHandle.restype = wintypes.BOOL
    return api


class _WindowsWait:
    """The worker owns both handles; cancellation only signals its event."""

    def __init__(self, deadline: float) -> None:
        self.deadline = deadline
        self._lock = threading.Lock()
        self._cancelled = False
        self._cancel_handle = None
        self._api = None

    def cancel(self) -> None:
        with self._lock:
            self._cancelled = True
            if self._cancel_handle is not None:
                self._api.SetEvent(self._cancel_handle)

    def run(self) -> None:
        from ctypes import wintypes

        timer = cancel = None
        api = _windows_api()
        try:
            with self._lock:
                if self._cancelled:
                    return
                # Unnamed, non-inheritable handles affect only this wait.
                timer = api.CreateWaitableTimerExW(None, None, 0x2, 0x00100002)
                if not timer:
                    raise ctypes.WinError(ctypes.get_last_error())
                cancel = api.CreateEventW(None, True, False, None)
                if not cancel:
                    raise ctypes.WinError(ctypes.get_last_error())
                self._api, self._cancel_handle = api, cancel
            remaining = max(0.0, self.deadline - monotonic())
            due = ctypes.c_longlong(-max(1, math.ceil(remaining * 10_000_000)))
            if not api.SetWaitableTimer(timer, ctypes.byref(due), 0, None, None, False):
                raise ctypes.WinError(ctypes.get_last_error())
            handles = (wintypes.HANDLE * 2)(cancel, timer)
            while True:
                result = api.WaitForMultipleObjects(2, handles, False, 1000)
                if result in (0, 1):
                    return
                if result == 258:
                    with self._lock:
                        if self._cancelled:
                            return
                    continue
                raise ctypes.WinError(ctypes.get_last_error())
        finally:
            # WaitForMultipleObjects has returned before either handle is closed.
            with self._lock:
                self._cancel_handle = None
                if cancel:
                    api.CloseHandle(cancel)
                if timer:
                    api.CloseHandle(timer)


async def _finish(task: asyncio.Task) -> None:
    """Do not leave a native wait running when its caller is cancelled."""
    cancelled = False
    while not task.done():
        try:
            await asyncio.shield(task)
        except asyncio.CancelledError:
            cancelled = True
        except Exception:
            break
    with contextlib.suppress(asyncio.CancelledError, Exception):
        task.result()
    if cancelled:
        raise asyncio.CancelledError


async def _native_wait(deadline: float, wake: asyncio.Event | None) -> bool:
    waiter = _WindowsWait(deadline)
    worker = asyncio.create_task(asyncio.to_thread(waiter.run))
    signal = asyncio.create_task(wake.wait()) if wake is not None else None
    try:
        if signal is not None:
            await asyncio.wait((worker, signal), return_when=asyncio.FIRST_COMPLETED)
            if wake.is_set():
                return True
        await asyncio.shield(worker)
        return False
    finally:
        waiter.cancel()
        if signal is not None:
            signal.cancel()
        try:
            await _finish(worker)
        finally:
            if signal is not None:
                # Join the canceled signal without swallowing cancellation of this caller.
                await asyncio.gather(signal, return_exceptions=True)


async def wait(delay: float, wake: asyncio.Event | None = None) -> bool:
    """Wait for a duration or an event; True means the event was set."""
    if isinstance(delay, bool) or not math.isfinite(delay) or delay < 0:
        raise ValueError("wait duration must be a finite non-negative number")
    if wake is not None and wake.is_set():
        return True
    if delay == 0:
        return False
    deadline = monotonic() + delay
    if sys.platform == "win32":
        try:
            return await _native_wait(deadline, wake)
        except OSError:
            # Older Windows or unavailable native resources retain the existing wait path.
            delay = max(0.0, deadline - monotonic())
    if wake is None:
        await asyncio.sleep(delay)
        return False
    try:
        await asyncio.wait_for(wake.wait(), delay)
        return True
    except TimeoutError:
        return False
