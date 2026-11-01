import asyncio
from types import SimpleNamespace

import pytest

from etherlighting_evolved.transport import SSHTransport, TransportError, _Channel


class Process:
    def __init__(self, *, acknowledged=True):
        self.exit_status = None
        self.exit_signal = None
        self.acknowledged = acknowledged
        self.eof_count = 0
        self.released = asyncio.Event()
        self.stdin = SimpleNamespace(write_eof=self.eof)

    def eof(self):
        self.eof_count += 1

    async def wait_closed(self):
        await self.released.wait()
        if self.acknowledged:
            self.exit_status = 0


class Connection:
    def __init__(self):
        self.close_calls = 0
        self.confirm = asyncio.Event()

    def close(self):
        self.close_calls += 1

    async def wait_closed(self):
        await self.confirm.wait()


@pytest.fixture
def transport(monkeypatch):
    monkeypatch.setattr("etherlighting_evolved.transport.SSH_CLOSE_TIMEOUT", 0.03)
    instance = SSHTransport("fixture.invalid")
    instance._conn = Connection()
    return instance


@pytest.mark.anyio
async def test_shell_exit_precedes_connection_close_and_timeout_is_retryable(transport):
    connection = transport._conn
    process = Process()
    transport._channels["links"] = _Channel(process)
    with pytest.raises(TransportError, match="cleanup"):
        await transport.close()
    assert process.eof_count == 1 and connection.close_calls == 0
    assert transport._cleanup is not None and not transport.connected
    with pytest.raises(TransportError, match="previous SSH"):
        await transport.connect()
    process.released.set()
    connection.confirm.set()
    await transport.close()
    assert process.eof_count == 1 and connection.close_calls == 1
    assert transport._cleanup is None


@pytest.mark.anyio
async def test_connection_closure_timeout_cannot_become_empty_success(transport):
    connection = transport._conn
    with pytest.raises(TransportError, match="cleanup"):
        await transport.close()
    with pytest.raises(TransportError, match="cleanup"):
        await transport.close()
    assert transport._cleanup["connection"] is connection
    connection.confirm.set()
    await transport.close()
    assert transport._cleanup is None


@pytest.mark.anyio
async def test_remote_channel_close_without_exit_status_is_not_exit_proof(transport):
    process = Process(acknowledged=False)
    process.released.set()
    connection = transport._conn
    connection.confirm.set()
    transport._channels["health"] = _Channel(process)
    for _ in range(2):
        with pytest.raises(TransportError, match="cleanup"):
            await transport.close()
    assert not connection.close_calls and transport._cleanup is not None
    process.exit_signal = ("TERM", False, "", "")
    await transport.close()
    assert connection.close_calls == 1


@pytest.mark.anyio
async def test_concurrent_and_cancelled_callers_share_owned_cleanup(transport):
    process = Process()
    connection = transport._conn
    connection.confirm.set()
    transport._channels["links"] = _Channel(process)
    first = asyncio.create_task(transport.close())
    second = asyncio.create_task(transport.close())
    await asyncio.sleep(0)
    first.cancel()
    process.released.set()
    with pytest.raises(asyncio.CancelledError):
        await first
    await second
    assert process.eof_count == 1 and connection.close_calls == 1
    assert transport._cleanup is None


@pytest.mark.anyio
async def test_retired_health_shell_is_included_in_handoff(transport):
    process = Process()
    connection = transport._conn
    transport._retire_process(connection, process)
    with pytest.raises(TransportError, match="cleanup"):
        await transport.close()
    assert process in transport._cleanup["processes"]
    process.released.set()
    connection.confirm.set()
    await transport.close()
    assert not transport._retired_processes and transport._cleanup is None


@pytest.mark.anyio
async def test_handshake_cannot_attach_after_cleanup_starts(monkeypatch, transport):
    import asyncssh

    transport._conn = None
    entered, release = asyncio.Event(), asyncio.Event()
    connection = Connection()
    connection.confirm.set()

    async def connect(**kwargs):
        entered.set()
        await release.wait()
        return connection

    monkeypatch.setattr(asyncssh, "connect", connect)
    opening = asyncio.create_task(transport.connect())
    await entered.wait()
    with pytest.raises(TransportError, match="cleanup"):
        await transport.close()
    assert not transport.connected
    release.set()
    with pytest.raises(TransportError, match="closed while connecting"):
        await opening
    assert not transport.connected and connection in transport._late_connections
    await transport.close()
    assert connection.close_calls == 1 and transport._cleanup is None


@pytest.mark.anyio
async def test_cancelled_key_persistence_cleanup_retains_connection(monkeypatch, transport):
    import asyncssh

    transport._conn = None
    connection = Connection()
    closing = asyncio.Event()

    def fail_key():
        raise OSError("fixture key persistence failed")

    async def wait_closed():
        closing.set()
        await connection.confirm.wait()

    async def connect(**kwargs):
        return connection

    connection.get_server_host_key = fail_key
    connection.wait_closed = wait_closed
    monkeypatch.setattr(asyncssh, "connect", connect)
    opening = asyncio.create_task(transport.connect())
    await closing.wait()
    opening.cancel()
    with pytest.raises(asyncio.CancelledError):
        await opening
    assert connection in transport._late_connections and not transport.connected
    connection.confirm.set()
    await transport.close()
    assert not transport._late_connections and transport._cleanup is None
