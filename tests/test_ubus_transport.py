import asyncio
from types import SimpleNamespace

import pytest

from etherlighting_evolved import switch as switch_module
from etherlighting_evolved import ubus
from etherlighting_evolved.color import Color
from etherlighting_evolved.switch import EtherlightSwitch, PortOutput, require_write_ack
from etherlighting_evolved.transport import (
    ETHERLIGHT_MCU_SIGNATURE,
    SSHTransport,
    TransportError,
)


class Connection:
    def __init__(self):
        self.closed = False

    def close(self):
        self.closed = True

    async def wait_closed(self):
        pass


class Client:
    def revoke_profile(self):
        self.profile = None

    def __init__(self, *, fail_at=None, started=None, complete=None, stop=None):
        self.calls = []
        self.closed = False
        self.fail_at = fail_at
        self.started, self.complete, self.stop = started, complete, stop

    async def set_port(self, port, rgb, level):
        self.calls.append((port, rgb, level))
        if self.started:
            self.started.set()
        if self.complete:
            await self.complete.wait()
        if len(self.calls) == self.fail_at:
            raise ubus.UbusError("fixture native rejection")
        if self.stop:
            self.stop.set()

    async def close(self):
        self.closed = True


class ExistingSSH(SSHTransport):
    def __init__(self, *, schema=ETHERLIGHT_MCU_SIGNATURE, replies=None):
        super().__init__("fixture.invalid")
        self._conn = Connection()
        self.original = self._conn
        self.schema = schema
        self.shell_commands = []
        self.replies = iter(replies) if replies is not None else None

    async def connect(self):
        raise AssertionError("must reuse the existing SSH session")

    async def run(self, command, **kwargs):
        if command == switch_module.BOARD_LIGHTING_INFO:
            return ""
        if kwargs.get("channel") == "leds":
            self.shell_commands.append(command)
            return next(self.replies) if self.replies is not None else '{"set":"ok"}'
        if command == switch_module.HOSTNAME_COMMAND:
            return ""
        assert command == switch_module.MCU_SCHEMA_COMMAND
        return self.schema + f"\n{switch_module.MCU_SCHEMA_EXIT}0\n"


@pytest.fixture(autouse=True)
def isolate_ownership_from_protocol_fixture(monkeypatch):
    original = EtherlightSwitch.__init__
    def initialize(switch, transport, **kwargs):
        original(switch, transport, **kwargs)
        if isinstance(transport, ExistingSSH):
            switch.output_lease = SimpleNamespace(before_write=lambda fingerprint: None, uncertain=False)
    monkeypatch.setattr(EtherlightSwitch, "__init__", initialize)


def install_factory(monkeypatch, *results):
    opens = []
    pending = iter(results)

    async def open_client(connection):
        opens.append(connection)
        result = next(pending)
        if isinstance(result, BaseException):
            raise result
        return result

    monkeypatch.setattr(ubus, "EtherlightUbus", SimpleNamespace(open=open_client))
    return opens


def frame(count=8):
    return [PortOutput(Color(12, 34, 56), 100)] * count


@pytest.mark.anyio
async def test_negotiation_follows_verified_probe_and_reuses_ssh(monkeypatch):
    client = Client()
    opens = install_factory(monkeypatch, client)
    transport = ExistingSSH()
    switch = EtherlightSwitch(transport)
    await switch.connect()
    assert not opens
    assert (await switch.probe())["port_rgb"]
    await switch.probe()
    assert opens == [transport.original]
    assert transport.direct_rgb
    assert await switch.write(frame()) == 8
    assert len(client.calls) == 8
    assert not transport.shell_commands
    assert not transport.original.closed


@pytest.mark.anyio
async def test_failed_schema_does_not_open_socket(monkeypatch):
    opens = install_factory(monkeypatch, Client())
    switch = EtherlightSwitch(ExistingSSH(schema="'another.object'\n\"set\":{}\n"))
    assert not (await switch.probe())["port_rgb"]
    assert not opens


@pytest.mark.anyio
@pytest.mark.parametrize("failure", [OSError("unsupported forwarding"), ubus.UbusError("bad schema")])
async def test_socket_or_lookup_failure_falls_back_before_any_write(monkeypatch, failure):
    opens = install_factory(monkeypatch, failure)
    transport = ExistingSSH()
    switch = EtherlightSwitch(transport)
    await switch.probe()
    await switch.probe()
    assert opens == [transport.original]
    assert not transport.direct_rgb
    assert await switch.write(frame()) == 8
    assert len(transport.shell_commands) == 8
    assert all(command.count('"port_rgb"') == 1 for command in transport.shell_commands)
    assert not transport.original.closed


@pytest.mark.anyio
async def test_failed_direct_write_never_replays_through_shell(monkeypatch):
    client = Client(fail_at=3)
    install_factory(monkeypatch, client)
    transport = ExistingSSH()
    switch = EtherlightSwitch(transport)
    await switch.probe()
    with pytest.raises(TransportError, match="rejection"):
        await switch.write(frame())
    assert len(client.calls) == 3
    assert set(switch._last) == {1, 2}
    assert switch.stats.commands == 2
    assert switch.stats.attempted_commands == 3
    assert switch._last_full == 0
    assert client.closed and transport.connected and not transport.original.closed
    assert transport.direct_rgb
    with pytest.raises(TransportError, match="reconnect"):
        await switch.write(frame())
    assert not transport.shell_commands
    assert len(client.calls) == 3


@pytest.mark.anyio
async def test_reprobe_restores_only_socket_after_failure(monkeypatch):
    first, second = Client(fail_at=1), Client()
    opens = install_factory(monkeypatch, first, second)
    transport = ExistingSSH()
    switch = EtherlightSwitch(transport)
    await switch.probe()
    with pytest.raises(TransportError):
        await switch.write(frame())
    await switch.probe()
    assert await switch.write(frame()) == 8
    assert opens == [transport.original, transport.original]
    assert not transport.original.closed and not transport.shell_commands


@pytest.mark.anyio
async def test_failed_reprobe_does_not_downgrade_to_shell(monkeypatch):
    install_factory(monkeypatch, Client(fail_at=1), OSError("fixture socket closed"))
    transport = ExistingSSH()
    switch = EtherlightSwitch(transport)
    await switch.probe()
    with pytest.raises(TransportError):
        await switch.write(frame())
    with pytest.raises(TransportError, match="restored"):
        await switch.probe()
    assert transport.direct_rgb and not transport.shell_commands


@pytest.mark.anyio
async def test_cancel_closes_only_direct_channel_and_does_not_cache(monkeypatch):
    started, complete = asyncio.Event(), asyncio.Event()
    client = Client(started=started, complete=complete)
    install_factory(monkeypatch, client)
    transport = ExistingSSH()
    switch = EtherlightSwitch(transport)
    await switch.probe()
    task = asyncio.create_task(switch.write(frame()))
    await asyncio.wait_for(started.wait(), 1)
    task.cancel()
    with pytest.raises(asyncio.CancelledError):
        await task
    assert client.closed and not transport.original.closed
    assert not switch._last and not transport.shell_commands


@pytest.mark.anyio
async def test_release_stops_before_next_direct_port(monkeypatch):
    stop = asyncio.Event()
    client = Client(stop=stop)
    install_factory(monkeypatch, client)
    transport = ExistingSSH()
    switch = EtherlightSwitch(transport)
    await switch.probe()
    assert await switch.write(frame(), stop=stop) == 1
    assert len(client.calls) == 1 and not client.closed
    assert set(switch._last) == {1} and switch._last_full == 0


@pytest.mark.anyio
async def test_close_releases_socket_before_existing_connection(monkeypatch):
    client = Client()
    install_factory(monkeypatch, client)
    transport = ExistingSSH()
    await EtherlightSwitch(transport).probe()
    await transport.close()
    assert client.closed and transport.original.closed
    assert not transport.connected and not transport.direct_rgb


@pytest.mark.anyio
async def test_close_during_socket_setup_cannot_leak_client(monkeypatch):
    started, complete = asyncio.Event(), asyncio.Event()
    client = Client()

    async def open_client(connection):
        started.set()
        await complete.wait()
        return client

    monkeypatch.setattr(ubus, "EtherlightUbus", SimpleNamespace(open=open_client))
    transport = ExistingSSH()
    task = asyncio.create_task(EtherlightSwitch(transport).probe())
    await asyncio.wait_for(started.wait(), 1)
    await transport.close()
    complete.set()
    with pytest.raises(TransportError, match="session closed"):
        await task
    assert client.closed and not transport.direct_rgb


@pytest.mark.anyio
async def test_direct_off_uses_black_with_native_level100(monkeypatch):
    client = Client()
    install_factory(monkeypatch, client)
    switch = EtherlightSwitch(ExistingSSH(), num_ports=1)
    await switch.probe()
    await switch.set_port(1, Color(12, 34, 56), 0)
    assert client.calls == [(1, "000000", 100)]


@pytest.mark.parametrize("reply", [
    "", "{}", "null", '{"set":"bad"}', '{"set":"ok","error":{"code":22}}',
    '{"error":{"code":5,"message":"fixture rejected"}}',
    '{"set":"bad","set":"ok"}', '{"set":"ok"}\n{"set":"ok"}',
    '{"set":"ok"}\n__EE_LED_WRITE_FAILED__',
])
def test_shell_requires_exact_unambiguous_native_success(reply):
    with pytest.raises(TransportError):
        require_write_ack(reply)


@pytest.mark.anyio
async def test_shell_native_error_stops_before_next_port(monkeypatch):
    install_factory(monkeypatch, OSError("unsupported"))
    transport = ExistingSSH(replies=['{"set":"ok"}', '{"error":{"code":5}}'])
    switch = EtherlightSwitch(transport)
    await switch.probe()
    with pytest.raises(TransportError, match="rejected"):
        await switch.write(frame())
    assert len(transport.shell_commands) == 2
    assert set(switch._last) == {1}
    assert switch.stats.commands == 1 and switch.stats.attempted_commands == 2
    assert switch._last_full == 0


@pytest.mark.anyio
@pytest.mark.parametrize("operation", ["set", "benchmark"])
async def test_one_off_and_benchmark_also_require_native_ack(operation):
    transport = ExistingSSH(replies=['{"error":{"code":22}}'])
    switch = EtherlightSwitch(transport)
    with pytest.raises(TransportError, match="rejected"):
        if operation == "set":
            await switch.set_port(1, Color(12, 34, 56))
        else:
            await switch.benchmark(1)
    assert len(transport.shell_commands) == 1


@pytest.mark.anyio
async def test_direct_large_frame_paces_every_port_at_its_absolute_ceiling(monkeypatch):
    tick, calls = [100.0], []

    class TimedClient(Client):
        async def set_port(self, *args):
            calls.append(tick[0])
            await super().set_port(*args)

    async def sleep(delay, wake=None):
        tick[0] += delay

    install_factory(monkeypatch, TimedClient())
    monkeypatch.setattr(switch_module.timing, "monotonic", lambda: tick[0])
    monkeypatch.setattr(switch_module.timing, "wait", sleep)
    switch = EtherlightSwitch(ExistingSSH(), num_ports=16)
    await switch.probe()
    assert await switch.write(frame(16), max_commands=1000) == 16
    assert calls == pytest.approx([100 + index / 200 for index in range(16)])


def fake_clock(monkeypatch):
    clock = [100.0]

    async def sleep(delay, wake=None):
        clock[0] += delay

    monkeypatch.setattr(switch_module.timing, "monotonic", lambda: clock[0])
    monkeypatch.setattr(switch_module.time, "perf_counter", lambda: clock[0])
    monkeypatch.setattr(switch_module.timing, "wait", sleep)
    return clock


@pytest.mark.anyio
async def test_pacing_spans_frames_cache_clear_and_budget_changes(monkeypatch):
    clock, calls = fake_clock(monkeypatch), []

    class TimedClient(Client):
        async def set_port(self, *args):
            calls.append(clock[0])

    install_factory(monkeypatch, TimedClient())
    switch = EtherlightSwitch(ExistingSSH(), num_ports=1)
    await switch.probe()
    for budget in (200, 200, 20, 200):
        switch.forget()
        await switch.write(frame(1), max_commands=budget)
    assert calls == pytest.approx([100, 100.005, 100.055, 100.060])


@pytest.mark.anyio
async def test_shell_is_capped_at_thirty_even_if_requested_rate_is_higher(monkeypatch):
    clock, calls = fake_clock(monkeypatch), []

    class TimedShell(ExistingSSH):
        async def run(self, command, **kwargs):
            if kwargs.get("channel") == "leds":
                calls.append(clock[0])
            return await super().run(command, **kwargs)

    switch = EtherlightSwitch(TimedShell(), num_ports=2)
    await switch.write(frame(2), max_commands=200)
    switch.forget()
    await switch.write(frame(2), max_commands=200)
    assert calls == pytest.approx([100 + index / 30 for index in range(4)])
    assert switch.ack_latency() == {"count": 0, "p95": None, "last_at": None}


@pytest.mark.anyio
async def test_ack_latency_excludes_pacing_and_expired_samples(monkeypatch):
    clock = fake_clock(monkeypatch)

    class DelayedClient(Client):
        async def set_port(self, *args):
            clock[0] += 0.002

    install_factory(monkeypatch, DelayedClient())
    switch = EtherlightSwitch(ExistingSSH(), num_ports=8)
    await switch.probe()
    await switch.write(frame(), max_commands=10)
    assert clock[0] == pytest.approx(100.702)
    assert switch.ack_latency() == {"count": 8, "p95": pytest.approx(0.002),
                                    "last_at": pytest.approx(100.702)}
    clock[0] += 3.01
    assert switch.ack_latency() == {"count": 0, "p95": None, "last_at": None}


@pytest.mark.anyio
async def test_slow_ack_has_no_accumulated_burst_credit(monkeypatch):
    clock, calls = fake_clock(monkeypatch), []

    class SlowClient(Client):
        async def set_port(self, *args):
            calls.append(clock[0])
            clock[0] += 0.1

    install_factory(monkeypatch, SlowClient())
    switch = EtherlightSwitch(ExistingSSH(), num_ports=3)
    await switch.probe()
    await switch.write(frame(3), max_commands=200)
    assert calls == pytest.approx([100, 100.1, 100.2])
    assert switch.ack_latency()["p95"] == pytest.approx(0.1)


@pytest.mark.anyio
async def test_latency_samples_are_bounded_and_reset_without_pacing_credit(monkeypatch):
    clock = fake_clock(monkeypatch)
    client = Client()
    install_factory(monkeypatch, client)
    switch = EtherlightSwitch(ExistingSSH(), num_ports=1)
    await switch.probe()
    for _ in range(65):
        await switch.write(frame(1), force=True, max_commands=200)
    assert switch.ack_latency()["count"] == 64
    previous = clock[0]
    switch.reset_timing()
    assert switch.ack_latency()["count"] == 0
    await switch.write(frame(1), force=True, max_commands=200)
    assert clock[0] - previous == pytest.approx(0.005)
    client.fail_at = len(client.calls) + 1
    with pytest.raises(TransportError):
        await switch.write(frame(1), force=True, max_commands=200)
    assert switch.ack_latency()["count"] == 0


@pytest.mark.anyio
async def test_direct_output_preserves_single_channel_steps(monkeypatch):
    install_factory(monkeypatch, Client())
    direct = EtherlightSwitch(ExistingSSH(), num_ports=1, keepalive=60)
    shell = EtherlightSwitch(ExistingSSH(), num_ports=1, keepalive=60)
    await direct.probe()
    initial = [PortOutput(Color(20, 20, 20), 100)]
    step = [PortOutput(Color(21, 20, 20), 100)]
    await direct.write(initial)
    await shell.write(initial)
    assert await direct.write(step) == 1
    assert await shell.write(step) == 0


@pytest.mark.anyio
async def test_release_interrupts_pacing_without_starting_another_command(monkeypatch):
    client = Client()
    install_factory(monkeypatch, client)
    switch = EtherlightSwitch(ExistingSSH(), num_ports=1)
    await switch.probe()
    await switch.write(frame(1), max_commands=1)
    stop = asyncio.Event()
    pending = asyncio.create_task(switch.write(frame(1), force=True, max_commands=1, stop=stop))
    await asyncio.sleep(0.01)
    stop.set()
    assert await asyncio.wait_for(pending, 0.2) == 0
    assert len(client.calls) == 1 and switch.stats.attempted_commands == 1


@pytest.mark.anyio
async def test_budget_is_refreshed_after_wait_crosses_cpu_freshness_deadline(monkeypatch):
    clock, calls = fake_clock(monkeypatch), []
    cpu_updated_at = 97.025

    class TimedClient(Client):
        async def set_port(self, *args):
            calls.append(clock[0])

    def budget():
        return 45 if clock[0] - cpu_updated_at <= 3 else 30

    install_factory(monkeypatch, TimedClient())
    switch = EtherlightSwitch(ExistingSSH(), num_ports=4)
    await switch.probe()
    await switch.write(frame(4), max_commands=45, budget_getter=budget)
    # The third port crosses CPU expiry while waiting, then waits the remaining interval for 30/s.
    assert calls == pytest.approx([100, 100 + 1 / 45, 100 + 1 / 45 + 1 / 30, 100 + 1 / 45 + 2 / 30])
    assert all(after - before >= 1 / 30 - 1e-9 for before, after in zip(calls[1:], calls[2:], strict=False))


@pytest.mark.anyio
async def test_budget_refresh_can_halt_old_frame_before_next_attempt(monkeypatch):
    stop = asyncio.Event()
    client = Client()
    checks = 0

    def budget():
        nonlocal checks
        checks += 1
        if checks > 1:
            stop.set()
        return 30

    install_factory(monkeypatch, client)
    switch = EtherlightSwitch(ExistingSSH(), num_ports=4)
    await switch.probe()
    assert await switch.write(frame(4), budget_getter=budget, stop=stop) == 1
    assert len(client.calls) == 1 and switch.stats.attempted_commands == 1
