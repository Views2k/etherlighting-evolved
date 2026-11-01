import asyncio
import ctypes
import sys
import threading
from concurrent.futures import ThreadPoolExecutor
from types import SimpleNamespace

import pytest

from etherlighting_evolved import timing


def test_deadlines_use_performance_counter(monkeypatch):
    monkeypatch.setattr(timing, "time", SimpleNamespace(perf_counter=lambda: 123.456789))
    assert timing.monotonic() == 123.456789


@pytest.mark.anyio
@pytest.mark.parametrize("delay", [-1, float("nan"), float("inf"), True])
async def test_invalid_durations_are_rejected(delay):
    with pytest.raises(ValueError):
        await timing.wait(delay)


@pytest.mark.anyio
async def test_zero_and_already_set_event_do_not_start_worker(monkeypatch):
    async def forbidden(*args):
        raise AssertionError("An immediate result must not create a native wait")

    monkeypatch.setattr(timing, "_native_wait", forbidden)
    assert await timing.wait(0) is False
    wake = asyncio.Event()
    wake.set()
    assert await timing.wait(100, wake) is True


@pytest.mark.anyio
async def test_portable_fallback_preserves_wake_and_timeout(monkeypatch):
    monkeypatch.setattr(timing, "sys", SimpleNamespace(platform="other"))
    wake = asyncio.Event()
    task = asyncio.create_task(timing.wait(60, wake))
    await asyncio.sleep(0)
    wake.set()
    assert await task is True
    assert await timing.wait(0.001, asyncio.Event()) is False
    assert await timing.wait(0.001) is False


@pytest.mark.anyio
async def test_unavailable_native_timer_falls_back_for_remaining_duration(monkeypatch):
    clock = [100.0]
    slept = []

    async def unavailable(*args):
        clock[0] += 0.025
        raise OSError("High-resolution timers are unavailable")

    async def sleep(delay):
        slept.append(delay)

    monkeypatch.setattr(timing, "sys", SimpleNamespace(platform="win32"))
    monkeypatch.setattr(timing, "monotonic", lambda: clock[0])
    monkeypatch.setattr(timing, "_native_wait", unavailable)
    monkeypatch.setattr(timing, "asyncio", SimpleNamespace(sleep=sleep))
    assert await timing.wait(0.1) is False
    assert slept == pytest.approx([0.075])


class ControlledWait:
    def __init__(self, loop, started):
        self.loop = loop
        self.started = started
        self.release = threading.Event()
        self.finished = threading.Event()
        self.cancelled = threading.Event()
        self.cancel_seen = asyncio.Event()

    def run(self):
        self.loop.call_soon_threadsafe(self.started.set)
        assert self.release.wait(5)
        self.finished.set()

    def cancel(self):
        self.cancelled.set()
        self.loop.call_soon_threadsafe(self.cancel_seen.set)


@pytest.mark.anyio
async def test_cancel_during_signal_join_is_not_swallowed(monkeypatch):
    started, signal_started, joining, gate = [asyncio.Event() for _ in range(4)]
    worker = ControlledWait(asyncio.get_running_loop(), started)
    monkeypatch.setattr(timing, '_WindowsWait', lambda deadline: worker)

    class SlowSignal:
        def is_set(self):
            return False

        async def wait(self):
            signal_started.set()
            try:
                await asyncio.Event().wait()
            except asyncio.CancelledError:
                joining.set()
                await gate.wait()
                raise

    task = asyncio.create_task(timing._native_wait(timing.monotonic() + 60, SlowSignal()))
    try:
        await asyncio.wait_for(signal_started.wait(), 2)
        worker.release.set()
        await asyncio.wait_for(joining.wait(), 2)
        task.cancel()
        with pytest.raises(asyncio.CancelledError):
            await asyncio.wait_for(task, 2)
    finally:
        worker.release.set()
        gate.set()


@pytest.mark.anyio
@pytest.mark.parametrize("exit_path", ["event", "cancel", "cancel_twice"])
async def test_cancellation_waits_for_worker_completion(monkeypatch, exit_path):
    started, wake = asyncio.Event(), asyncio.Event()
    worker = ControlledWait(asyncio.get_running_loop(), started)
    monkeypatch.setattr(timing, "_WindowsWait", lambda deadline: worker)
    task = asyncio.create_task(timing._native_wait(timing.monotonic() + 60, wake))
    await asyncio.wait_for(started.wait(), 2)
    if exit_path == "event":
        wake.set()
    else:
        task.cancel()
    await asyncio.wait_for(worker.cancel_seen.wait(), 2)
    assert not task.done()
    if exit_path == "cancel_twice":
        task.cancel()
        await asyncio.sleep(0)
        assert not task.done()
    worker.release.set()
    if exit_path == "event":
        assert await task is True
    else:
        with pytest.raises(asyncio.CancelledError):
            await task
    assert worker.finished.is_set()


@pytest.mark.anyio
async def test_cancelled_queued_worker_is_joined_before_return(monkeypatch):
    # Occupying one private executor exercises cancellation before native handles exist.
    loop = asyncio.get_running_loop()
    gate = threading.Event()
    started = asyncio.Event()
    worker = ControlledWait(loop, started)
    executor = ThreadPoolExecutor(max_workers=1)
    blocker = executor.submit(gate.wait, 5)

    async def enqueue(function):
        return await loop.run_in_executor(executor, function)

    monkeypatch.setattr(timing, "_WindowsWait", lambda deadline: worker)
    monkeypatch.setattr(timing, "asyncio", SimpleNamespace(**(vars(asyncio) | {"to_thread": enqueue})))
    try:
        task = asyncio.create_task(timing._native_wait(timing.monotonic() + 60, None))
        await asyncio.sleep(0)
        await asyncio.sleep(0)
        task.cancel()
        await asyncio.sleep(0)
        assert not task.done()
        gate.set()
        await asyncio.wait_for(started.wait(), 2)
        worker.release.set()
        with pytest.raises(asyncio.CancelledError):
            await task
        assert worker.finished.is_set()
    finally:
        gate.set()
        worker.release.set()
        blocker.result(timeout=5)
        executor.shutdown(wait=True)


def test_pre_cancelled_wait_never_allocates_handles(monkeypatch):
    class NoAllocation:
        def CreateWaitableTimerExW(self, *args):
            raise AssertionError("A cancelled queued wait must not allocate handles")

    monkeypatch.setattr(timing, "_windows_api", NoAllocation)
    waiter = timing._WindowsWait(timing.monotonic() + 60)
    waiter.cancel()
    waiter.run()


@pytest.mark.parametrize("failure", ["CreateWaitableTimerExW", "CreateEventW", "SetWaitableTimer",
                                     "WaitForMultipleObjects"])
def test_native_failure_closes_every_previously_allocated_handle(monkeypatch, failure):
    allocated, closed = [], []

    class FailingAPI:
        def __getattr__(self, name):
            def call(*args):
                if name == failure:
                    raise OSError("Native resource unavailable")
                if name.startswith("Create"):
                    handle = 100 + len(allocated)
                    allocated.append(handle)
                    return handle
                if name == "CloseHandle":
                    closed.append(args[0])
                return 1

            return call

    monkeypatch.setattr(timing, "_windows_api", FailingAPI)
    waiter = timing._WindowsWait(timing.monotonic() + 60)
    with pytest.raises(OSError, match="unavailable"):
        waiter.run()
    assert sorted(allocated) == sorted(closed)
    assert waiter._cancel_handle is None


@pytest.mark.skipif(sys.platform != "win32", reason="Windows native timer lifecycle")
@pytest.mark.anyio
@pytest.mark.parametrize("exit_path", ["event", "cancel"])
async def test_active_native_wait_closes_both_handles_after_cancellation(monkeypatch, exit_path):
    api = timing._windows_api()
    loop = asyncio.get_running_loop()
    entered, wake = asyncio.Event(), asyncio.Event()
    allocated, closed = [], []
    waiting = [False]

    class TracedAPI:
        def __getattr__(self, name):
            function = getattr(api, name)

            def call(*args):
                if name == "WaitForMultipleObjects":
                    waiting[0] = True
                    loop.call_soon_threadsafe(entered.set)
                if name == "CloseHandle":
                    assert not waiting[0]
                    closed.append(args[0])
                result = function(*args)
                if name.startswith("Create"):
                    allocated.append(result)
                if name == "WaitForMultipleObjects":
                    waiting[0] = False
                return result

            return call

    monkeypatch.setattr(timing, "_windows_api", TracedAPI)
    task = asyncio.create_task(timing._native_wait(timing.monotonic() + 60, wake))
    await asyncio.wait_for(entered.wait(), 2)
    assert len(allocated) == 2 and not closed
    if exit_path == "event":
        wake.set()
        assert await asyncio.wait_for(task, 2) is True
    else:
        task.cancel()
        with pytest.raises(asyncio.CancelledError):
            await asyncio.wait_for(task, 2)
    assert sorted(allocated) == sorted(closed)


@pytest.mark.skipif(sys.platform != "win32", reason="Windows native timer lifecycle")
@pytest.mark.anyio
async def test_native_timers_do_not_leak_handles_and_stop_promptly(monkeypatch):
    from ctypes import wintypes

    api = ctypes.WinDLL("kernel32", use_last_error=True)
    api.GetCurrentProcess.restype = wintypes.HANDLE
    api.GetProcessHandleCount.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]
    api.GetProcessHandleCount.restype = wintypes.BOOL

    def handle_count():
        count = wintypes.DWORD()
        assert api.GetProcessHandleCount(api.GetCurrentProcess(), ctypes.byref(count))
        return count.value

    loop = asyncio.get_running_loop()
    executor = ThreadPoolExecutor(max_workers=1)

    async def enqueue(function):
        return await loop.run_in_executor(executor, function)

    monkeypatch.setattr(timing, "asyncio", SimpleNamespace(**(vars(asyncio) | {"to_thread": enqueue})))
    try:
        # Warm the private executor so thread-pool growth is not counted as timer leakage.
        for _ in range(4):
            assert await timing._native_wait(timing.monotonic() + 0.001, None) is False
        before = handle_count()
        for index in range(60):
            if index % 3 == 0:
                deadline = timing.monotonic() + 0.001
                assert await timing._native_wait(deadline, None) is False
                assert timing.monotonic() >= deadline
            else:
                wake = asyncio.Event()
                task = asyncio.create_task(timing._native_wait(timing.monotonic() + 60, wake))
                await asyncio.sleep(0)
                if index % 3 == 1:
                    wake.set()
                    assert await asyncio.wait_for(task, 2) is True
                else:
                    task.cancel()
                    with pytest.raises(asyncio.CancelledError):
                        await asyncio.wait_for(task, 2)
        assert handle_count() <= before + 2
    finally:
        executor.shutdown(wait=True)
