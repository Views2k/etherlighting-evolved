import asyncio
from types import SimpleNamespace

import pytest

from etherlighting_evolved import health
from etherlighting_evolved.engine import Engine
from etherlighting_evolved.switch import EtherlightSwitch
from etherlighting_evolved.transport import SimulatedTransport, SSHTransport, TransportError


class Process:
    def __init__(self):
        self.closed = False
        self.writes = []
        self.started = asyncio.Event()
        self.replies = asyncio.Queue()
        self.exit_status = None
        self.exit_signal = None
        self.stdin = SimpleNamespace(write=self.write, write_eof=self.eof)
        self.stdout = SimpleNamespace(readline=self.replies.get)

    def write(self, value):
        self.writes.append(value)
        self.started.set()

    def close(self):
        self.closed = True

    def eof(self):
        self.closed = True
        self.exit_status = 0

    async def wait_closed(self):
        assert self.closed


class Connection:
    def __init__(self):
        self.process = Process()
        self.closed = False

    async def create_process(self, *args, **kwargs):
        return self.process

    def close(self):
        self.closed = True


@pytest.mark.anyio
@pytest.mark.parametrize("fault", ["timeout", "cancel", "oversized", "eof"])
async def test_health_probe_failure_never_closes_output_connection(monkeypatch, fault):
    monkeypatch.setattr(health, "HEALTH_TIMEOUT", 0.02)
    transport = SSHTransport("fixture.invalid")
    connection = transport._conn = Connection()
    rgb = transport._rgb_client = object()
    led_process = Process()
    transport._channels["leds"] = SimpleNamespace(process=led_process)
    pending = asyncio.create_task(transport.read_health())
    await connection.process.started.wait()
    if fault == "cancel":
        pending.cancel()
    elif fault == "oversized":
        connection.process.replies.put_nowait("x" * (health.MAX_HEALTH_RESPONSE + 1))
    elif fault == "eof":
        connection.process.replies.put_nowait("")
    expected = asyncio.CancelledError if fault == "cancel" else TransportError
    with pytest.raises(expected):
        await pending
    assert transport._conn is connection and not connection.closed
    assert transport._rgb_client is rgb and not led_process.closed
    assert connection.process.closed and "health" not in transport._channels
    assert connection.process.writes[0].startswith(health.HEALTH_COMMAND + "\n")


@pytest.mark.anyio
async def test_health_read_success_uses_fixed_command_and_complete_marker():
    transport = SSHTransport("fixture.invalid")
    connection = transport._conn = Connection()
    pending = asyncio.create_task(transport.read_health())
    await connection.process.started.wait()
    for line in ("__EE_HEALTH_V1__\n", "__EE_HEALTH_DONE__\n", "__EE_HEALTH_DONE_1__\n"):
        connection.process.replies.put_nowait(line)
    assert health.parse_health(await pending) == {"memory": None, "temperatures": [], "storage": []}
    assert not connection.closed and not connection.process.closed


@pytest.mark.anyio
async def test_health_stats_share_existing_frames_and_failures_do_not_stop_lighting(monkeypatch):
    transport = SimulatedTransport(latency=0)
    switch = EtherlightSwitch(transport)
    engine = Engine(switch, rules=[])
    await switch.connect()
    engine.connected = True
    await engine._collect_health()
    initial = engine.output_stats()
    assert initial["switch_health"]["memory"]["used_percent"] == 37.5
    assert initial["switch_health"]["temperatures"][0]["label"] == "Simulated sensor"
    assert initial["health_sample_age_ms"] >= 0 and initial["health_error"] is None
    assert engine.frame_message()["stats"]["switch_health"] == initial["switch_health"]
    async def failed():
        raise TransportError("fixture probe timeout with private contents")
    monkeypatch.setattr(switch, "read_health", failed)
    await engine._collect_health()
    assert engine.connected and engine.mode == "running" and engine.error is None
    assert engine.output_stats()["health_error"] == "unavailable"
    assert engine.output_stats()["switch_health"] == initial["switch_health"]
    await switch.close()


@pytest.mark.anyio
async def test_health_reply_from_old_generation_is_not_published(monkeypatch):
    transport = SimulatedTransport(latency=0)
    switch = EtherlightSwitch(transport)
    engine = Engine(switch, rules=[])
    await switch.connect()
    engine.connected = True
    async def replaced():
        await transport.close()
        await transport.connect()
        return {"memory": {"used_percent": 50}}
    monkeypatch.setattr(switch, "read_health", replaced)
    await engine._collect_health()
    assert engine.output_stats()["switch_health"] is None
    assert engine.output_stats()["health_sample_age_ms"] is None
    await switch.close()
