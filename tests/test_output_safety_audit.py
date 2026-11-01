import asyncio
import json
from datetime import datetime
from types import SimpleNamespace

import pytest
from fastapi.testclient import TestClient

from etherlighting_evolved import engine as engine_module
from etherlighting_evolved import switch as switch_module
from etherlighting_evolved.api import create_app
from etherlighting_evolved.color import WHITE, Color
from etherlighting_evolved.engine import Engine
from etherlighting_evolved.session import LoginError, SessionManager
from etherlighting_evolved.switch import EtherlightSwitch, PortOutput, parse_mcu_probe, port_rgb_command
from etherlighting_evolved.transport import (
    HostKeyMismatchError,
    HostKeyStore,
    SimulatedTransport,
    SSHTransport,
    TransportError,
    _Channel,
)


def make_engine(**kwargs):
    return Engine(EtherlightSwitch(SimulatedTransport(latency=0)), rules=[], **kwargs)


@pytest.mark.parametrize("value", [float("nan"), float("inf"), -float("inf"), True])
def test_invalid_output_values_do_not_change_settings(value):
    engine = make_engine()
    before = engine.state()["output"]
    with pytest.raises(ValueError):
        engine.set_output(gamma=1.5, transition=value)
    assert engine.state()["output"] == before
    with pytest.raises(ValueError):
        engine.set_brightness(value)
    assert engine.brightness == 100


def test_requested_large_budget_still_starts_at_thirty_and_shell_cannot_ramp_above_it():
    engine = make_engine(max_commands=200)
    assert engine.max_commands == 200 and engine.command_budget == 30
    engine.set_output(max_commands=200)
    assert engine.command_budget == 30


@pytest.mark.parametrize("port", [0, 65, 1.2, True, "1;reboot", float("nan")])
def test_command_rejects_invalid_port_values(port):
    with pytest.raises((ValueError, OverflowError)):
        port_rgb_command(port, WHITE, 100)


@pytest.mark.parametrize("level", [float("nan"), float("inf"), True, "100;reboot"])
def test_command_rejects_invalid_brightness_values(level):
    with pytest.raises((ValueError, OverflowError)):
        port_rgb_command(1, WHITE, level)


def test_only_color_payloads_are_constructed_for_every_supported_port():
    for port in range(1, 65):
        for level in (0, 1, 50, 100):
            command = port_rgb_command(port, Color(12, 34, 56), level)
            payload = json.loads(command.split("'", 2)[1])
            expected = f"{port} 0c2238 {level}" if level else f"{port} 000000 100"
            assert payload == {"port_rgb": expected}
            assert command.startswith("ubus call etherlight.mcu set '")


def test_atomic_effect_change_clears_only_selected_group_pins(config):
    with TestClient(create_app(config, autoconnect=False), base_url="http://localhost") as client:
        client.post("/api/session/login", json={"host": "demo"})
        client.put("/api/zones", json={"zones": [
            {"id": "left", "ports": [1, 2], "effect": {"id": "static"}},
            {"id": "right", "ports": [3, 4], "effect": {"id": "static"}},
        ]})
        client.put("/api/ports", json={"ports": {"1": {"color": "white"}, "3": {"color": "red"}}})
        rejected = client.put("/api/zones/left/effect", json={"id": "missing", "clear_overrides": True})
        assert rejected.status_code == 400
        assert set(client.get("/api/state").json()["overrides"]) == {"1", "3"}
        applied = client.put("/api/zones/left/effect", json={"id": "breathing", "clear_overrides": True})
        assert applied.status_code == 200
        assert set(applied.json()["overrides"]) == {"3"}
        assert client.put("/api/output", json={"max_commands": 201}).status_code == 422
        assert client.put("/api/output", json={"max_commands": 200}).json()["stats"]["command_budget"] <= 30


@pytest.mark.parametrize("effect,key,start,changed", [
    ("breathing", "period", 6, 4), ("ripple", "speed", 0.5, 1),
    ("rainbow", "speed", 0.1, 0), ("gradient", "speed", 0.1, 0),
])
def test_speed_change_keeps_the_current_phase(monkeypatch, effect, key, start, changed):
    clock = [100.0]
    monkeypatch.setattr(engine_module.timing, "monotonic", lambda: clock[0])
    engine = make_engine(effect=effect, effect_params={key: start}, transition=0)
    clock[0] += 7.25
    before = engine.compose()[0]
    engine.update_params({key: changed})
    assert engine.compose()[0] == before
    if changed == 0:
        clock[0] += 3
        assert engine.compose()[0] == before
        engine.update_params({key: start})
        assert engine.compose()[0] == before


@pytest.mark.anyio
async def test_start_is_idempotent_and_stop_leaves_no_writer():
    engine = make_engine()
    await engine.start()
    tasks = tuple(engine._tasks)
    await engine.start()
    assert tuple(engine._tasks) == tasks
    await engine.stop()
    assert all(task.done() for task in tasks)


@pytest.mark.anyio
async def test_release_waits_for_inflight_write_then_remains_idle():
    started, complete = asyncio.Event(), asyncio.Event()
    sim = SimulatedTransport(latency=0)

    class DelayedSwitch(EtherlightSwitch):
        async def write(self, outputs, **kwargs):
            started.set()
            await complete.wait()
            return await super().write(outputs, **kwargs)

    engine = Engine(DelayedSwitch(sim), rules=[])
    await engine.start()
    try:
        await asyncio.wait_for(started.wait(), 1)
        engine.release()
        pending = asyncio.create_task(engine.wait_for_output_idle())
        await asyncio.sleep(0)
        assert not pending.done()
        complete.set()
        await asyncio.wait_for(pending, 1)
        commands = sim.command_count
        await asyncio.sleep(0.2)
        assert sim.command_count == commands
    finally:
        complete.set()
        await engine.stop()


@pytest.mark.anyio
async def test_malformed_cpu_response_is_ignored():
    class ShortCpu(SimulatedTransport):
        async def run(self, command, **kwargs):
            return "cpu 1\n0.5 0.2 0.1\n"

    assert await EtherlightSwitch(ShortCpu()).read_cpu() is None


@pytest.mark.anyio
async def test_timing_measurement_is_paced_and_batches_are_bounded(monkeypatch):
    tick = [100.0]
    events = []

    class Recording(SimulatedTransport):
        async def run(self, command, **kwargs):
            events.append((tick[0], command.count('"port_rgb"')))
            return '{"set":"ok"}'

    async def sleep(delay, wake=None):
        tick[0] += delay

    monkeypatch.setattr(switch_module.timing, "monotonic", lambda: tick[0])
    monkeypatch.setattr(switch_module.timing, "wait", sleep)
    switch = EtherlightSwitch(Recording())
    await switch.benchmark(count=9)
    batches = []
    for at, size in events:
        if batches and batches[-1][0] == at:
            batches[-1] = (at, batches[-1][1] + size)
        else:
            batches.append((at, size))
    assert max(size for _, size in batches) <= 8
    for (at, size), (following, _) in zip(batches, batches[1:], strict=False):
        assert following - at >= size / 30 - 1e-9
    with pytest.raises(ValueError):
        await switch.benchmark(count=61)
    with pytest.raises(ValueError):
        await switch.benchmark(port=9)


@pytest.mark.anyio
async def test_capability_probe_failure_closes_connection_without_led_writes(config):
    class Unsupported(SimulatedTransport):
        async def run(self, command, **kwargs):
            assert kwargs.get("channel") != "leds"
            assert command == switch_module.MCU_SCHEMA_COMMAND
            return "'etherlight.mcu'\n\"get\":{}\n" + f"\n{switch_module.MCU_SCHEMA_EXIT}0\n"

    transport = Unsupported()
    sessions = SessionManager(config, transport_factory=lambda **kwargs: transport)
    with pytest.raises(LoginError, match="no Etherlighting"):
        await sessions.connect("demo")
    assert not transport.connected
    assert sessions.engine is None


@pytest.mark.anyio
@pytest.mark.parametrize("reply", [
    'device-"port_rgb"\n',
    'device\n\'etherlight.mcu\'\n"get":{"port_rgb":"String"}',
    'device\n\'etherlight.mcu\'\n"set":{"port_rgb":"Integer"}',
])
async def test_probe_requires_the_actual_set_command_signature(reply):
    class Unrecognized(SimulatedTransport):
        async def run(self, command, **kwargs):
            assert command == switch_module.MCU_SCHEMA_COMMAND
            return reply + f"\n{switch_module.MCU_SCHEMA_EXIT}0\n"

    assert not (await EtherlightSwitch(Unrecognized()).probe())["port_rgb"]


@pytest.mark.parametrize("reply", [
    "'etherlight.mcu' @abc01234\n\t\"set\":{\"port_rgb\":\"String\"}\n",
    "fixture-device\n'etherlight.mcu' @abc01234\n\t\"set\":{\"port_rgb\":\"String\"}\n",
    '\r\n"etherlight.mcu" @a0\r\n "set" : {\r\n "port_rgb": "string"\r\n }\r\n',
    "fixture-device\n'etherlight.mcu' @a0\n\"set\":{\"extra\":{\"type\":\"String\"},"
    '\"port_rgb\":\"String\"}\n',
])
def test_probe_accepts_named_schema_with_or_without_hostname(reply):
    result = parse_mcu_probe(reply)
    assert result["etherlight_mcu"] and result["port_rgb"]
    assert result["hostname"] in {"", "fixture-device"}


@pytest.mark.parametrize("reply", [
    "fixture-device\n'elsewhere' @a0\n\"set\":{\"port_rgb\":\"String\"}\n",
    "'etherlight.mcu' @a0\n\"get\":{\"port_rgb\":\"String\"}\n",
    "'etherlight.mcu' @a0\n\"set\":{\"port_rgb\":\"Integer\"}\n",
    "'etherlight.mcu' @a0\n\"set\":{\"port_rgb\":\"String\"\n",
    "'etherlight.mcu' @a0\n\"get\":{}\n'elsewhere' @b0\n\"set\":{\"port_rgb\":\"String\"}\n",
    'fixture-device etherlight.mcu "set":{"port_rgb":"String"}',
    "'etherlight.mcu' @a0\n\"get\":{\n\"set\":{\"port_rgb\":\"String\"}\n}\n",
])
def test_probe_does_not_accept_wrong_object_method_type_or_malformed_schema(reply):
    assert not parse_mcu_probe(reply)["port_rgb"]


@pytest.mark.anyio
async def test_atomic_effect_change_publishes_only_the_unpinned_state():
    engine = make_engine()
    engine.set_override(1, "white")
    messages = []
    engine._publish = messages.append
    engine._subscribers.add(object())
    engine.set_effect("breathing", clear_overrides=True)
    assert len(messages) == 1
    assert messages[0]["state"]["overrides"] == {}


@pytest.mark.anyio
async def test_shutdown_cancels_pending_autoconnect_before_it_can_start_engine(config, monkeypatch):
    started, cancelled = asyncio.Event(), asyncio.Event()

    async def delayed(self):
        started.set()
        try:
            await asyncio.Future()
        finally:
            cancelled.set()

    monkeypatch.setattr(SessionManager, "auto_connect", delayed)
    app = create_app(config)
    async with app.router.lifespan_context(app):
        await asyncio.wait_for(started.wait(), 1)
    assert cancelled.is_set()
    assert app.state.sessions.engine is None


@pytest.mark.anyio
async def test_probe_transport_error_closes_unowned_connection(config):
    class FailedProbe(SimulatedTransport):
        async def run(self, command, **kwargs):
            raise TransportError("probe failed")

    transport = FailedProbe()
    sessions = SessionManager(config, transport_factory=lambda **kwargs: transport)
    with pytest.raises(LoginError):
        await sessions.connect("demo")
    assert not transport.connected


@pytest.mark.anyio
async def test_changed_host_key_is_rejected_before_authentication(monkeypatch, tmp_path):
    import asyncssh

    store = HostKeyStore(tmp_path / "keys.json")
    store.verify_or_pin("switch.invalid", 22, "SHA256:original")
    authenticated = False

    async def connect(**kwargs):
        nonlocal authenticated
        assert kwargs["known_hosts"] is not None
        client = kwargs["client_factory"]()
        key = SimpleNamespace(get_fingerprint=lambda _: "SHA256:replacement")
        if not client.validate_host_public_key("switch.invalid", "127.0.0.1", 22, key):
            raise asyncssh.HostKeyNotVerifiable("rejected")
        authenticated = True
        raise AssertionError("credentials should never reach this server")

    monkeypatch.setattr(asyncssh, "connect", connect)
    transport = SSHTransport("switch.invalid", host_keys=store, username="test", password="fixture")
    with pytest.raises(HostKeyMismatchError):
        await transport.connect()
    assert not authenticated and not transport.connected


@pytest.mark.anyio
async def test_ssh_read_timeout_covers_the_whole_response():
    class Trickle:
        async def readline(self):
            await asyncio.sleep(0.02)
            return "still busy\n"

    class Process:
        stdin = SimpleNamespace(write=lambda _: None, write_eof=lambda: None)
        stdout = Trickle()
        exit_status = None
        exit_signal = None

        async def wait_closed(self):
            self.exit_status = 0

        def close(self):
            pass

    transport = SSHTransport("switch.invalid")
    transport._conn = SimpleNamespace(close=lambda: None, wait_closed=lambda: asyncio.sleep(0))
    transport._channels["main"] = _Channel(Process())
    started = asyncio.get_running_loop().time()
    with pytest.raises(TransportError, match="did not answer"):
        await asyncio.wait_for(transport.run("read-only-fixture", timeout=0.06), 0.5)
    assert asyncio.get_running_loop().time() - started < 0.3
    assert not transport.connected


def test_animation_slows_under_cpu_backoff_without_changing_clock_based_effects(monkeypatch):
    clock = [100.0]
    monkeypatch.setattr(engine_module.timing, "monotonic", lambda: clock[0])
    engine = make_engine(effect="rainbow", transition=0)
    engine.compose()
    clock[0] += 1
    engine.compose()
    assert engine._animation_time == 101
    engine._adjust_throttle(0.05)
    clock[0] += 1
    engine.compose()
    assert engine._animation_time == pytest.approx(101.5)
    engine.set_effect("daylight")
    noon = datetime(2026, 1, 1, 12)
    evening = datetime(2026, 1, 1, 20)
    assert engine.compose(now=noon)[0] != engine.compose(now=evening)[0]


def test_large_switch_motion_uses_the_same_number_of_samples(monkeypatch):
    clock = [100.0]
    monkeypatch.setattr(engine_module.timing, "monotonic", lambda: clock[0])
    engine = Engine(EtherlightSwitch(SimulatedTransport(), num_ports=64), rules=[])
    clock[0] += 64 / 30
    engine.compose()
    assert engine._animation_time - 100 == pytest.approx(8 / 30)


def test_effect_color_parameter_crossfades_and_manual_color_remains_instant(monkeypatch):
    clock = [100.0]
    monkeypatch.setattr(engine_module.timing, "monotonic", lambda: clock[0])
    engine = make_engine(effect="static", transition=1)
    before = engine.compose()[0]
    engine.update_params({"color": "red"})
    assert engine.compose()[0] == before
    clock[0] += 0.5
    middle = engine.compose()[0][0]
    assert middle.r == 255 and 0 < middle.g < 255
    clock[0] += 0.5
    assert engine.compose()[0][0] == Color(255, 0, 0)
    engine.set_override(1, "blue")
    assert engine.compose()[0][0] == Color(0, 0, 255)


def test_corrupt_host_key_store_does_not_silently_trust_a_new_key(tmp_path):
    path = tmp_path / "keys.json"
    path.write_text("not-json", "utf-8")
    with pytest.raises(TransportError):
        HostKeyStore(path).get("switch.invalid", 22)


@pytest.mark.anyio
async def test_real_ssh_handshake_checks_key_before_password(tmp_path):
    """An ephemeral loopback server tests the installed SSH library, with fixture credentials only."""
    import asyncssh

    authentications = []

    class Server(asyncssh.SSHServer):
        def begin_auth(self, username):
            return True

        def password_auth_supported(self):
            return True

        def validate_password(self, username, password):
            authentications.append(username)
            return True

    key = asyncssh.generate_private_key("ssh-ed25519")
    replacement = asyncssh.generate_private_key("ssh-ed25519")
    store = HostKeyStore(tmp_path / "keys.json")
    server = await asyncssh.create_server(Server, "127.0.0.1", 0, server_host_keys=[key])
    port = server.get_port()
    try:
        for _ in range(2):
            transport = SSHTransport("127.0.0.1", port=port, host_keys=store,
                                     username="fixture", password="fixture")
            await transport.connect()
            await transport.close()
        assert len(authentications) == 2
    finally:
        server.close()
        await server.wait_closed()
    server = await asyncssh.create_server(Server, "127.0.0.1", port, server_host_keys=[replacement])
    try:
        transport = SSHTransport("127.0.0.1", port=port, host_keys=store,
                                 username="fixture", password="fixture")
        with pytest.raises(HostKeyMismatchError):
            await transport.connect()
        assert len(authentications) == 2
    finally:
        server.close()
        await server.wait_closed()


@pytest.mark.anyio
async def test_large_frame_stops_after_the_inflight_port_on_release():
    ready, complete, stop = asyncio.Event(), asyncio.Event(), asyncio.Event()
    chunks = []

    class Recording(SimulatedTransport):
        async def run(self, command, **kwargs):
            chunks.append(command.count('"port_rgb"'))
            ready.set()
            await complete.wait()
            return '{"set":"ok"}'

    switch = EtherlightSwitch(Recording(), num_ports=64)
    pending = asyncio.create_task(switch.write([PortOutput(WHITE, 100)] * 64, max_commands=5, stop=stop))
    await asyncio.wait_for(ready.wait(), 1)
    stop.set()
    complete.set()
    assert await asyncio.wait_for(pending, 0.2) == 1
    assert chunks == [1]
    assert len(switch._last) == 1
    assert switch.stats.attempted_commands == 1


@pytest.mark.anyio
async def test_slow_transport_slows_animation_instead_of_skipping_frames():
    sim = SimulatedTransport(latency=0)

    class SlowSwitch(EtherlightSwitch):
        async def write(self, outputs, **kwargs):
            await asyncio.sleep(0.4)
            return await super().write(outputs, **kwargs)

    engine = Engine(SlowSwitch(sim), effect="rainbow", rules=[], transition=0)
    await sim.connect()
    engine.connected = True
    times = []
    render = engine.zones[0].effect.render

    def observe(t, ctx):
        times.append(t)
        return render(t, ctx)

    engine.zones[0].effect.render = observe
    task = asyncio.create_task(engine._render_loop())
    try:
        async with asyncio.timeout(2):
            while len(times) < 3:
                await asyncio.sleep(0.02)
        assert all(b - a <= 0.29 for a, b in zip(times, times[1:], strict=False))
    finally:
        task.cancel()
        with pytest.raises(asyncio.CancelledError):
            await task
        await sim.close()


def test_data_effect_uses_wall_time_while_decorative_clock_slows(monkeypatch):
    clock = [100.0]
    monkeypatch.setattr(engine_module.timing, "monotonic", lambda: clock[0])
    engine = make_engine(effect="traffic")
    engine._set_command_budget(7.5)
    times = []
    engine.zones[0].effect.render = lambda t, ctx: times.append(t) or [WHITE] * 8
    engine.compose()
    clock[0] += 2
    engine.compose()
    assert times == [0, 2]
    assert engine._animation_time == 100.5
