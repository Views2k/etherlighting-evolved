import asyncio
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest

from etherlighting_evolved import ubus
from etherlighting_evolved.transport import (
    READ_CPU,
    READ_POE,
    READ_PORTS,
    UNFINISHED_PORT_READ_LIMIT,
    PortSampleUnavailable,
    SSHTransport,
    TransportError,
)


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

    def is_closed(self):
        return self.closed

    async def wait_closed(self):
        pass


@pytest.mark.anyio
@pytest.mark.parametrize(("command", "fault"), [(READ_PORTS, "timeout"), (READ_POE, "eof"), (READ_CPU, "cancel")])
async def test_lost_status_sample_keeps_output_alive_and_can_resume(command, fault):
    transport = SSHTransport("fixture.invalid")
    connection = transport._conn = Connection()
    rgb = transport._rgb_client = SimpleNamespace(set_port=AsyncMock(), close=AsyncMock())
    first = connection.process
    running = asyncio.create_task(transport.run(command, channel="links", timeout=0.1))
    await asyncio.wait_for(first.started.wait(), 1)
    if fault == "eof":
        first.replies.put_nowait("")
    elif fault == "cancel":
        running.cancel()
    with pytest.raises(asyncio.CancelledError if fault == "cancel" else PortSampleUnavailable):
        await running
    assert first.closed and not connection.closed and transport.lighting_generation is rgb
    await transport.write_port_rgb(1, "00ff00", 30)
    rgb.set_port.assert_awaited_once_with(1, "00ff00", 30)
    replacement = connection.process = Process()
    next_read = asyncio.create_task(transport.run(command, channel="links"))
    await asyncio.wait_for(replacement.started.wait(), 1)
    replacement.replies.put_nowait("new data\n")
    replacement.replies.put_nowait(replacement.writes[0].splitlines()[-1].removeprefix("echo "))
    assert await next_read == "new data\n"
    assert id(first) not in transport._process_tags
    await transport.close()


@pytest.mark.anyio
async def test_unconfirmed_status_reader_is_bounded_and_still_checked_on_handoff():
    transport = SSHTransport("fixture.invalid")
    connection = transport._conn = Connection()
    first = connection.process
    first.stdin.write_eof = first.close  # The shell has not acknowledged exit.
    first.replies.put_nowait("")
    with pytest.raises(PortSampleUnavailable):
        await transport.run(READ_PORTS, channel="links")
    replacement = connection.process = Process()
    reading = asyncio.create_task(transport.run(READ_PORTS, channel="links"))
    await asyncio.wait_for(replacement.started.wait(), 1)
    replacement.replies.put_nowait("second sample\n")
    replacement.replies.put_nowait(replacement.writes[-1].splitlines()[-1].removeprefix("echo "))
    assert await reading == "second sample\n"
    replacement.stdin.write_eof = replacement.close
    replacement.replies.put_nowait("")
    with pytest.raises(PortSampleUnavailable):
        await transport.run(READ_PORTS, channel="links")
    unused = connection.process = Process()
    for _ in range(UNFINISHED_PORT_READ_LIMIT - 1):
        with pytest.raises(PortSampleUnavailable):
            await transport.run(READ_PORTS, channel="links")
    # At the limit the session is dropped, so the engine reconnects instead of using frozen status values.
    with pytest.raises(TransportError, match="cleanup") as error:
        await transport.run(READ_PORTS, channel="links")
    assert not isinstance(error.value, PortSampleUnavailable)
    assert not unused.started.is_set() and len(first.writes) == 1
    assert set(transport._process_tags) == {id(first), id(replacement)}
    assert not connection.closed
    first.exit_signal = ("TERM", False, "", "")
    replacement.exit_status = 0
    await transport.close()
    assert connection.closed


@pytest.mark.anyio
async def test_status_shell_open_timeout_closes_the_session():
    # A cancelled open can leave the shell running on the switch without a handle; only closing the session ends it.
    transport = SSHTransport("fixture.invalid")
    opening, complete = asyncio.Event(), asyncio.Event()
    connection = transport._conn = Connection(opening=opening, complete=complete)
    with pytest.raises(TransportError) as error:
        await transport.run(READ_PORTS, channel="links", timeout=0.05)
    assert opening.is_set() and not isinstance(error.value, PortSampleUnavailable)
    assert connection.closed and not transport.connected


@pytest.mark.anyio
async def test_successful_status_read_resets_the_unfinished_count():
    transport = SSHTransport("fixture.invalid")
    connection = transport._conn = Connection()
    transport._unfinished_port_reads = UNFINISHED_PORT_READ_LIMIT - 1
    reading = asyncio.create_task(transport.run(READ_PORTS, channel="links"))
    await asyncio.wait_for(connection.process.started.wait(), 1)
    connection.process.replies.put_nowait("sample\n")
    connection.process.replies.put_nowait(connection.process.writes[-1].splitlines()[-1].removeprefix("echo "))
    assert await reading == "sample\n"
    assert transport._unfinished_port_reads == 0


@pytest.mark.anyio
async def test_closed_ssh_during_status_read_still_fails_the_session():
    transport = SSHTransport("fixture.invalid")
    connection = transport._conn = Connection()
    reading = asyncio.create_task(transport.run(READ_PORTS, channel="links"))
    await asyncio.wait_for(connection.process.started.wait(), 1)
    connection.close()
    connection.process.replies.put_nowait("")
    with pytest.raises(TransportError) as error:
        await reading
    assert not isinstance(error.value, PortSampleUnavailable) and not transport.connected


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
