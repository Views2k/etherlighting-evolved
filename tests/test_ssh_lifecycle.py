import asyncio
from types import SimpleNamespace

import pytest

from etherlighting_evolved import ubus
from etherlighting_evolved.transport import SSHTransport, TransportError


class Process:
    def __init__(self):
        self.closed = False
        self.writes = []
        self.started = asyncio.Event()
        self.replies = asyncio.Queue()
        self.exit_status = None
        self.stdin = SimpleNamespace(write=self.write, write_eof=self.eof)
        self.stdout = SimpleNamespace(readline=self.readline)

    def write(self, value):
        self.writes.append(value)
        self.started.set()

    async def readline(self):
        reply = await self.replies.get()
        if isinstance(reply, Exception):
            raise reply
        return reply

    def close(self):
        self.closed = True

    def eof(self):
        self.closed = True
        self.exit_status = 0

    async def wait_closed(self):
        assert self.closed


class Connection:
    def __init__(self, *, opening=None, complete=None):
        self.closed = False
        self.process = Process()
        self.opening = opening
        self.complete = complete

    async def create_process(self, *args, **kwargs):
        if self.opening:
            self.opening.set()
        if self.complete:
            await self.complete.wait()
        return self.process

    def close(self):
        self.closed = True

    async def wait_closed(self):
        pass


@pytest.mark.anyio
async def test_closing_old_connection_preserves_new_channels():
    entered, finish = asyncio.Event(), asyncio.Event()

    async def close_client():
        entered.set()
        await finish.wait()

    transport = SSHTransport("fixture.invalid")
    original, replacement = Connection(), Connection()
    transport._conn = original
    old_channel = await transport._channel("links")
    transport._rgb_client = SimpleNamespace(close=close_client)
    closing = asyncio.create_task(transport.close())
    await asyncio.wait_for(entered.wait(), 1)
    transport._conn = replacement
    channel = await transport._channel("links")
    assert channel is not old_channel
    finish.set()
    await closing
    assert original.closed and old_channel.process.closed
    assert transport._conn is replacement and not replacement.closed
    assert not channel.process.closed and transport._channels["links"] is channel


@pytest.mark.anyio
async def test_cancelling_close_still_releases_old_resources_and_preserves_replacement(monkeypatch):
    monkeypatch.setattr("etherlighting_evolved.transport.SSH_CLOSE_TIMEOUT", 0.05)
    entered, blocked = asyncio.Event(), asyncio.Event()

    async def close_client():
        entered.set()
        await blocked.wait()

    transport = SSHTransport("fixture.invalid")
    original, replacement = Connection(), Connection()
    transport._conn = original
    old_channel = await transport._channel("links")
    transport._rgb_client = SimpleNamespace(close=close_client)
    closing = asyncio.create_task(transport.close())
    await asyncio.wait_for(entered.wait(), 1)
    transport._conn = replacement
    channel = await transport._channel("links")
    closing.cancel()
    with pytest.raises(asyncio.CancelledError):
        await closing
    assert original.closed and old_channel.process.closed
    assert transport._conn is replacement and not replacement.closed
    assert not channel.process.closed and transport._channels["links"] is channel


@pytest.mark.anyio
@pytest.mark.parametrize("fault", ["eof", "error", "timeout", "cancel"])
async def test_old_shell_failure_never_closes_replacement_connection(fault):
    transport = SSHTransport("fixture.invalid")
    original, replacement = Connection(), Connection()
    transport._conn = original
    running = asyncio.create_task(transport.run("fixture read", channel="links", timeout=0.1))
    await asyncio.wait_for(original.process.started.wait(), 1)
    await transport.close()
    transport._conn = replacement
    channel = await transport._channel("links")
    if fault == "cancel":
        running.cancel()
    elif fault == "eof":
        original.process.replies.put_nowait("")
    elif fault == "error":
        original.process.replies.put_nowait(OSError("fixture closed"))
    expected = asyncio.CancelledError if fault == "cancel" else TransportError
    with pytest.raises(expected):
        await asyncio.wait_for(running, 1)
    assert original.closed and original.process.closed
    assert transport._conn is replacement and not replacement.closed
    assert not channel.process.closed and transport._channels["links"] is channel
    assert not replacement.process.writes


@pytest.mark.anyio
async def test_cancelling_current_shell_command_still_closes_its_resources():
    transport = SSHTransport("fixture.invalid")
    connection = Connection()
    transport._conn = connection
    running = asyncio.create_task(transport.run("fixture read", channel="links"))
    await asyncio.wait_for(connection.process.started.wait(), 1)
    running.cancel()
    with pytest.raises(asyncio.CancelledError):
        await running
    assert connection.closed and connection.process.closed
    assert transport._conn is None and not transport._channels


@pytest.mark.anyio
async def test_old_channel_open_failure_retains_cleanup_until_opening_finishes(monkeypatch):
    monkeypatch.setattr("etherlighting_evolved.transport.SSH_CLOSE_TIMEOUT", 0.05)
    opening, complete = asyncio.Event(), asyncio.Event()
    transport = SSHTransport("fixture.invalid")
    original, replacement = Connection(opening=opening, complete=complete), Connection()
    transport._conn = original
    running = asyncio.create_task(transport.run("fixture read"))
    await asyncio.wait_for(opening.wait(), 1)
    with pytest.raises(TransportError, match="cleanup"):
        await transport.close()
    complete.set()
    with pytest.raises(TransportError, match="opening a channel"):
        await asyncio.wait_for(running, 1)
    await transport.close()
    transport._conn = replacement
    assert original.closed and original.process.closed
    assert transport._conn is replacement and not replacement.closed
    assert not original.process.writes and not replacement.process.writes


@pytest.mark.anyio
async def test_queued_command_does_not_write_after_its_connection_changes():
    transport = SSHTransport("fixture.invalid")
    original, replacement = Connection(), Connection()
    transport._conn = original
    old_channel = await transport._channel("links")
    await old_channel.lock.acquire()
    running = asyncio.create_task(transport.run("fixture read", channel="links"))
    await asyncio.sleep(0)
    await asyncio.sleep(0)
    await transport.close()
    transport._conn = replacement
    old_channel.lock.release()
    with pytest.raises(TransportError):
        await asyncio.wait_for(running, 1)
    assert not original.process.writes and not replacement.process.writes
    assert transport._conn is replacement and not replacement.closed


@pytest.mark.anyio
async def test_old_failed_negotiation_does_not_mark_new_connection_as_unsupported(monkeypatch):
    opening, complete = asyncio.Event(), asyncio.Event()
    original, replacement = Connection(), Connection()
    transport = SSHTransport("fixture.invalid")
    transport._conn = original
    calls = []
    client = SimpleNamespace()

    async def open_client(connection):
        calls.append(connection)
        if connection is original:
            opening.set()
            await complete.wait()
            raise OSError("fixture old socket closed")
        return client

    monkeypatch.setattr(ubus, "EtherlightUbus", SimpleNamespace(open=open_client))
    pending = asyncio.create_task(transport.prepare_rgb())
    await asyncio.wait_for(opening.wait(), 1)
    await transport.close()
    transport._conn = replacement
    complete.set()
    with pytest.raises(TransportError, match="session closed"):
        await pending
    assert not transport._rgb_checked and not transport._rgb_failed
    assert await transport.prepare_rgb()
    assert calls == [original, replacement] and transport._rgb_client is client
    assert not replacement.closed
