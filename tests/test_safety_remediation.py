import argparse
import asyncio
from contextlib import nullcontext

import pytest
from fastapi.testclient import TestClient
from starlette.websockets import WebSocketDisconnect

from etherlighting_evolved import api, cli
from etherlighting_evolved import engine as engine_module
from etherlighting_evolved.engine import Engine
from etherlighting_evolved.ownership import OutputOwnershipError
from etherlighting_evolved.session import LoginError, SessionManager
from etherlighting_evolved.switch import (
    BOARD_LIGHTING_INFO,
    PORT_FIRMWARE_HASH_COMMAND,
    RUNNING_DAEMON_HASH,
    EtherlightSwitch,
    LightingCapabilityError,
    parse_mcu_probe,
)
from etherlighting_evolved.transport import (
    AuthenticationError,
    HostKeyMismatchError,
    SimulatedTransport,
    TransportError,
    UnreachableError,
)


@pytest.fixture(autouse=True)
def isolated_app_lock(monkeypatch):
    monkeypatch.setattr(cli, "AppInstanceLock", nullcontext)


@pytest.mark.parametrize("host", ["0.0.0.0", "::", "192.0.2.10"])
@pytest.mark.parametrize("token", ["", "short", " " * 32, "x" * 31])
def test_network_bind_rejected_before_session_or_saved_login_access(config, monkeypatch, host, token):
    config.api.host, config.api.token = host, token
    monkeypatch.setattr(api, "SessionManager", lambda *a, **k: pytest.fail("must validate before accessing state"))
    with pytest.raises(ValueError, match="token"):
        api.create_app(config)


@pytest.mark.parametrize("token", [None, 123, "line\nbreak", "non-ascii-\u00e9"])
def test_invalid_tokens_fail_before_app_creation(config, token):
    config.api.token = token
    with pytest.raises(ValueError, match="token"):
        api.create_app(config, autoconnect=False)


def test_network_api_and_websocket_require_the_configured_token(config):
    config.api.host = "0.0.0.0"
    config.api.token = "fixture-token-with-at-least-32-characters"
    authorized = {"Authorization": f"Bearer {config.api.token}"}
    with TestClient(api.create_app(config, autoconnect=False), base_url="http://ee.example:8787") as client:
        for headers in ({}, {"Authorization": "Bearer wrong"}):
            assert client.get("/api/session", headers=headers).status_code == 401
            assert client.post("/api/session/restore", headers=headers).status_code == 401
            assert client.put("/api/brightness", json={"value": 5}, headers=headers).status_code == 401
        assert client.post("/api/session/login", json={"host": "demo"}, headers=authorized).status_code == 200
        assert client.put("/api/brightness", json={"value": 40}, headers=authorized).status_code == 200
        assert client.get("/api/state", headers=authorized).json()["brightness"] == 40
        with pytest.raises(WebSocketDisconnect) as rejected, client.websocket_connect("/api/ws"):
            pass
        assert rejected.value.code == 4401
        with client.websocket_connect("/api/ws", headers=authorized) as websocket:
            assert websocket.receive_json()["type"] == "session"


@pytest.mark.parametrize("configured,override", [("127.0.0.1", "0.0.0.0"), ("0.0.0.0", "127.0.0.1")])
def test_cli_uses_one_effective_bind_for_server_and_request_guard(config, monkeypatch, configured, override):
    import uvicorn

    config.api.host = configured
    config.api.token = "fixture-token-with-at-least-32-characters"
    monkeypatch.setattr(cli, "load_config", lambda path: config)
    captured = {}

    def create(candidate, **kwargs):
        captured["guard_host"] = candidate.api.host
        captured["guard_port"] = candidate.api.port
        return object()

    monkeypatch.setattr(api, "create_app", create)
    monkeypatch.setattr(uvicorn, "run", lambda app, **kwargs: captured.update(kwargs))
    args = argparse.Namespace(config=None, host=override, port=9999, log_level="error",
                              demo=False, no_autoconnect=True)
    assert cli._serve(args) == 0
    assert captured["host"] == captured["guard_host"] == override
    assert captured["port"] == captured["guard_port"] == 9999


def test_cli_remote_override_cannot_bypass_authentication_requirement(config, monkeypatch):
    import uvicorn

    monkeypatch.setattr(cli, "load_config", lambda path: config)
    monkeypatch.setattr(uvicorn, "run", lambda *a, **k: pytest.fail("must not open a listener"))
    args = argparse.Namespace(config=None, host="0.0.0.0", port=None, log_level="error",
                              demo=False, no_autoconnect=True)
    with pytest.raises(ValueError, match="Network access requires"):
        cli._serve(args)


class ReconnectSwitch(EtherlightSwitch):
    def __init__(self, failures, *, supported=True):
        super().__init__(SimulatedTransport(latency=0))
        self.failures = iter(failures)
        self.supported = supported
        self.attempts = self.closed = self.initialized = 0

    async def connect(self):
        self.attempts += 1
        error = next(self.failures, None)
        if error is not None:
            raise error

    async def probe(self):
        return {"port_rgb": self.supported}

    async def initialize_output(self):
        self.initialized += 1

    async def close(self):
        self.closed += 1


@pytest.mark.anyio
@pytest.mark.parametrize("error", [AuthenticationError("fixture"), HostKeyMismatchError("fixture"),
                                  OutputOwnershipError("fixture"), ValueError("invalid configuration"),
                                  RuntimeError("unexpected failure")])
async def test_permanent_reconnect_failure_stops_and_requires_manual_connection(error):
    switch = ReconnectSwitch([error])
    engine = Engine(switch, rules=[])
    await engine._keep_connected()
    assert switch.attempts == switch.closed == 1
    assert switch.initialized == 0 and not engine.connected
    assert engine.state()["reconnect_required"] and engine._output_halt.is_set()
    with pytest.raises(TransportError, match="Reconnect"):
        engine.resume()
    await engine._keep_connected()
    assert switch.attempts == 1


@pytest.mark.anyio
async def test_reconnect_refused_for_a_changed_key_explains_the_next_step():
    engine = Engine(ReconnectSwitch([HostKeyMismatchError("fixture")]), rules=[])
    await engine._keep_connected()
    assert engine.error == engine_module.HOST_KEY_CHANGED_ERROR


@pytest.mark.anyio
async def test_removed_rgb_capability_does_not_initialize_or_retry():
    switch = ReconnectSwitch([], supported=False)
    engine = Engine(switch, rules=[])
    await engine._keep_connected()
    assert switch.attempts == switch.closed == 1
    assert switch.initialized == 0 and engine.state()["reconnect_required"]
    assert "Per-port lighting is unavailable" in engine.error


def lost_connection(engine):
    engine.connected = True
    engine._lost(UnreachableError("fixture"))


@pytest.mark.anyio
async def test_missing_lighting_right_after_a_lost_connection_is_retried(monkeypatch):
    # A restarting switch can answer SSH before its lighting service runs.
    switch = ReconnectSwitch([], supported=False)
    engine = Engine(switch, rules=[])
    lost_connection(engine)
    waits, states = [], []

    async def sleep(seconds):
        waits.append(seconds)
        if engine.connected:
            raise asyncio.CancelledError
        states.append((engine.state()["reconnect_required"], engine.error))
        if len(waits) == 2:
            switch.supported = True

    monkeypatch.setattr(engine_module.asyncio, "sleep", sleep)
    monkeypatch.setattr(engine_module.random, "uniform", lambda low, high: 1.0)
    with pytest.raises(asyncio.CancelledError):
        await engine._keep_connected()
    assert waits == [3, 6, 1]
    assert states == [(False, engine_module.SWITCH_STARTING_ERROR)] * 2
    assert switch.attempts == 3 and switch.initialized == 1
    assert engine.connected and engine.error is None and engine._lost_at is None


@pytest.mark.anyio
async def test_missing_lighting_long_after_a_lost_connection_stops():
    switch = ReconnectSwitch([], supported=False)
    engine = Engine(switch, rules=[])
    lost_connection(engine)
    engine._lost_at -= Engine.STARTUP_GRACE
    await engine._keep_connected()
    assert switch.attempts == 1 and switch.initialized == 0
    assert engine.state()["reconnect_required"] and "Per-port lighting is unavailable" in engine.error


def test_only_missing_lighting_is_retried_while_a_switch_starts():
    engine = Engine(ReconnectSwitch([]), rules=[])
    capability = LightingCapabilityError("fixture capabilities changed")
    unavailable = ValueError("Per-port lighting is unavailable on this switch connection")
    # Before any connection was lost, missing lighting is a configuration problem.
    assert not engine._retry_allowed(capability) and not engine._retry_allowed(unavailable)
    lost_connection(engine)
    assert engine._retry_allowed(capability) and engine._retry_allowed(unavailable)
    for error in (OutputOwnershipError("fixture owner changed"), AuthenticationError("fixture"),
                  HostKeyMismatchError("fixture"), RuntimeError("fixture")):
        assert not engine._retry_allowed(error)
    engine._lost_at -= Engine.STARTUP_GRACE
    assert not engine._retry_allowed(capability) and not engine._retry_allowed(unavailable)


@pytest.mark.anyio
@pytest.mark.parametrize("error", [AuthenticationError("fixture"), HostKeyMismatchError("fixture")])
async def test_rejected_login_right_after_a_lost_connection_still_stops(error):
    switch = ReconnectSwitch([error])
    engine = Engine(switch, rules=[])
    lost_connection(engine)
    await engine._keep_connected()
    assert switch.attempts == 1 and engine.state()["reconnect_required"]


@pytest.mark.anyio
@pytest.mark.parametrize("error_type", [UnreachableError, TransportError, OSError])
async def test_transient_failures_keep_retrying_with_a_capped_delay(monkeypatch, error_type):
    # A restarting switch can stay away longer than five attempts and their backoff.
    switch = ReconnectSwitch([error_type("fixture") for _ in range(8)])
    engine = Engine(switch, rules=[])
    waits, states = [], []

    async def sleep(seconds):
        waits.append(seconds)
        if engine.connected:
            raise asyncio.CancelledError
        states.append((engine.state()["reconnect_required"], engine.error, switch.initialized))

    monkeypatch.setattr(engine_module.asyncio, "sleep", sleep)
    monkeypatch.setattr(engine_module.random, "uniform", lambda low, high: 1.1)
    with pytest.raises(asyncio.CancelledError):
        await engine._keep_connected()
    assert waits == pytest.approx([3.3, 6.6, 13.2, 26.4, 52.8, 60, 60, 60, 1])
    assert states == [(False, "fixture", 0)] * 8
    assert switch.attempts == 9 and switch.closed == 8 and switch.initialized == 1
    assert engine.connected and engine.error is None and not engine.state()["reconnect_required"]


@pytest.mark.parametrize("interval,expected", [(3.0, 60.0), (300.0, 300.0)])
def test_retry_delay_stays_finite_after_a_long_outage(monkeypatch, interval, expected):
    engine = Engine(ReconnectSwitch([]), rules=[], reconnect_delay=interval)
    monkeypatch.setattr(engine_module.random, "uniform", lambda low, high: high)
    engine._reconnect_failures = 100_000
    assert engine._retry_delay() == expected


@pytest.mark.anyio
async def test_successful_reconnect_resets_retry_budget(monkeypatch):
    switch = ReconnectSwitch([UnreachableError("fixture"), UnreachableError("fixture"), None])
    engine = Engine(switch, rules=[])
    waits = []

    async def sleep(seconds):
        waits.append(seconds)
        if engine.connected:
            raise asyncio.CancelledError

    monkeypatch.setattr(engine_module.asyncio, "sleep", sleep)
    monkeypatch.setattr(engine_module.random, "uniform", lambda low, high: 1.0)
    with pytest.raises(asyncio.CancelledError):
        await engine._keep_connected()
    assert waits == [3, 6, 1]
    assert switch.attempts == 3 and switch.initialized == 1
    assert engine.connected and engine.error is None
    assert engine._reconnect_failures == 0 and not engine.state()["reconnect_required"]


@pytest.mark.anyio
async def test_cancel_during_retry_delay_never_attempts_another_connection(monkeypatch):
    switch = ReconnectSwitch([UnreachableError("fixture")])
    engine = Engine(switch, rules=[])

    async def canceled_sleep(seconds):
        raise asyncio.CancelledError

    monkeypatch.setattr(engine_module.asyncio, "sleep", canceled_sleep)
    with pytest.raises(asyncio.CancelledError):
        await engine._keep_connected()
    assert switch.attempts == switch.closed == 1 and switch.initialized == 0


@pytest.mark.parametrize("prefix,expected", [
    ("fixture-device\n", "fixture-device"), ("fixture_device.local\n", "fixture_device.local"),
    ("sh: hostname: not found\n", ""), ("hostname: permission denied\n", ""),
    ("warning\nfixture-device\n", ""), ("\x1b[31merror\n", ""), ("x" * 254 + "\n", ""), ("", ""),
])
def test_probe_hostname_does_not_accept_diagnostics_or_ambiguous_output(prefix, expected):
    schema = '\'etherlight.mcu\' @1234\n\t"set":{"port_rgb":"String"}'
    result = parse_mcu_probe(prefix + schema)
    assert result["hostname"] == expected
    assert result["etherlight_mcu"] and result["port_rgb"]


def test_existing_frames_include_shared_health_without_switch_reads(monkeypatch):
    simulator = SimulatedTransport(latency=0)
    engine = Engine(EtherlightSwitch(simulator), rules=[])
    monkeypatch.setattr(engine_module.timing, "monotonic", lambda: 100.0)
    engine.cpu_idle, engine.cpu_load, engine.budget.cpu_updated_at = 0.8, 0.2, 97.0
    engine.error, engine._reconnect_blocked = "Reconnect your switch", True
    state, frame = engine.state(), engine.frame_message()
    assert frame["stats"] == state["stats"]
    assert frame["stats"]["cpu_sample_age_ms"] == 3000
    assert frame["error"] == state["error"] and frame["reconnect_required"]
    assert simulator.command_count == 0


class ProfileTransport:
    """A real-session classification with only an in-memory simulated device behind it."""

    def __init__(self, fault=None):
        self.host_key_fingerprint = "SHA256:profile-fixture-authenticated-peer"
        self.simulator = SimulatedTransport(latency=0)
        self.fault = fault
        self.writes = []

    def __getattr__(self, key):
        return getattr(self.simulator, key)

    async def run(self, command, **kwargs):
        if command == BOARD_LIGHTING_INFO and self.fault == "model":
            return "board.shortname=Unqualified\nboard.sysid=0x1234"
        if command == RUNNING_DAEMON_HASH and self.fault == "daemon":
            return "0" * 64 + "  /proc/123/exe"
        if command == PORT_FIRMWARE_HASH_COMMAND and self.fault == "firmware":
            return "0" * 64 + "  /usr/share/firmware/port_led_fw.bin"
        if '"port_rgb":' in command:
            self.writes.append(command)
        return await self.simulator.run(command, **kwargs)

    async def write_behavior(self, behavior):
        self.writes.append(behavior)
        await self.simulator.write_behavior(behavior)


@pytest.mark.anyio
@pytest.mark.parametrize("fault", ["model", "daemon", "firmware"])
async def test_unqualified_real_session_never_starts_engine_or_writes_output(config, monkeypatch, fault):
    transport = ProfileTransport(fault)
    sessions = SessionManager(config, transport_factory=lambda **kwargs: transport)

    async def forbidden_start(self):
        pytest.fail("Unqualified hardware must not start an engine")

    monkeypatch.setattr(Engine, "start", forbidden_start)
    with pytest.raises(LoginError) as error:
        await sessions.connect("fixture.invalid", "fixture", "fixture")
    assert error.value.code == "compatibility_verification_required" and error.value.status == 422
    assert sessions.engine is None and not transport.connected and not transport.writes


@pytest.mark.anyio
@pytest.mark.parametrize("configured_count", [8, 24, 48])
async def test_qualified_existing_model_profile_keeps_normal_session_start(config, configured_count):
    config.switch.num_ports = configured_count
    transport = ProfileTransport()
    sessions = SessionManager(config, transport_factory=lambda **kwargs: transport)
    try:
        result = await sessions.connect("fixture.invalid", "fixture", "fixture")
        pending = result["pending_scene_choice"]
        result = await sessions.choose_scene(pending["connection_id"], "start_new", pending["legacy_revision"])
        assert result["connected"] and not result["simulated"]
        assert sessions.engine.num_ports == 8 and transport.writes[0] == "steady"
        assert sessions.engine.switch.sfp_ports == (9, 10)
    finally:
        await sessions.close()


@pytest.mark.anyio
async def test_unqualified_model_on_reconnect_stops_before_initialization():
    transport = ProfileTransport("model")
    engine = Engine(EtherlightSwitch(transport), rules=[])
    await engine._keep_connected()
    assert engine.state()["reconnect_required"] and not engine.connected
    assert not transport.connected and not transport.writes
    assert "compatibility verification" in engine.error


@pytest.mark.anyio
@pytest.mark.parametrize("action", [cli._set, cli._benchmark])
async def test_cli_output_cannot_bypass_model_qualification(monkeypatch, action):
    transport = ProfileTransport("model")
    monkeypatch.setattr(cli, "_switch", lambda args: EtherlightSwitch(transport))
    args = argparse.Namespace(port=1, ports=8, color="ffffff", level=100, count=20)
    with pytest.raises(ValueError, match="compatibility verification"):
        await action(args)
    assert not transport.connected and not transport.writes
