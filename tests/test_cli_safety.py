import argparse
import asyncio
from contextlib import nullcontext
from types import SimpleNamespace

import pytest

from etherlighting_evolved import cli
from etherlighting_evolved.device_playback import PlaybackRecord
from etherlighting_evolved.ownership import device_identity
from etherlighting_evolved.switch import EtherlightSwitch
from etherlighting_evolved.transport import (
    HostKeyMismatchError,
    HostKeyStore,
    SimulatedTransport,
    TransportError,
)

FP_A = "SHA256:" + "A" * 43
FP_B = "SHA256:" + "B" * 42 + "A"


@pytest.fixture(autouse=True)
def isolated_app_lock(monkeypatch):
    monkeypatch.setattr(cli, "AppInstanceLock", nullcontext)


@pytest.mark.parametrize("arguments", [
    ["benchmark", "switch.invalid", "--count", "0"],
    ["benchmark", "switch.invalid", "--count", "61"],
    ["set", "switch.invalid", "1", "ffffff", "--level", "101"],
    ["set", "switch.invalid", "1", "ffffff", "--ports", "0"],
    ["probe", "switch.invalid", "--ssh-port", "65536"],
])
def test_invalid_values_are_rejected_before_connecting(arguments):
    with pytest.raises(SystemExit) as exc:
        cli.build_parser().parse_args(arguments)
    assert exc.value.code == 2


@pytest.mark.parametrize("action", [cli._set, cli._benchmark])
def test_writes_require_advertised_rgb_capability(monkeypatch, action):
    class UnsupportedSwitch:
        closed = False

        async def connect(self):
            pass

        async def probe(self):
            return {"etherlight_mcu": True, "port_rgb": False}

        async def close(self):
            self.closed = True

        async def set_port(self, *args, **kwargs):
            pytest.fail("must not write to an unsupported switch")

        async def benchmark(self, *args, **kwargs):
            pytest.fail("must not benchmark an unsupported switch")

    switch = UnsupportedSwitch()
    monkeypatch.setattr(cli, "_switch", lambda args: switch)
    args = argparse.Namespace(port=1, ports=8, color="ffffff", level=100, count=20)
    with pytest.raises(TransportError, match="per-port color"):
        asyncio.run(action(args))
    assert switch.closed


@pytest.mark.parametrize("action", [cli._set, cli._benchmark])
def test_outside_supported_port_range_does_not_request_credentials(monkeypatch, action):
    monkeypatch.setattr(cli, "_switch", lambda args: pytest.fail("must validate before connecting"))
    with pytest.raises(ValueError, match="between 1 and 64"):
        asyncio.run(action(argparse.Namespace(port=65, ports=8)))


@pytest.mark.anyio
async def test_cli_uses_detected_bounds_instead_of_default_count(monkeypatch):
    sim = SimulatedTransport(latency=0, lit_ports=24)
    switch = EtherlightSwitch(sim, num_ports=24)
    monkeypatch.setattr(cli, "_switch", lambda args: switch)
    args = argparse.Namespace(port=24, ports=8, color="ffffff", level=100)
    assert await cli._set(args) == 0
    assert sim.leds == {24: ("ffffff", 100)}
    args.port = 25
    with pytest.raises(ValueError, match="numbered 1 to 24"):
        await cli._set(args)
    assert sim.leds == {24: ("ffffff", 100)}


@pytest.mark.anyio
@pytest.mark.parametrize("brightness,expected_blue", [(0, 0), (1, 0), (15, 4), (50, 55), (100, 255)])
async def test_cli_brightness_uses_rgb_encoding_and_valid_native_off(monkeypatch, brightness, expected_blue):
    sim = SimulatedTransport(latency=0)
    switch = EtherlightSwitch(sim)
    monkeypatch.setattr(cli, "_switch", lambda args: switch)
    args = argparse.Namespace(port=1, ports=8, color="0000ff", level=brightness)
    assert await cli._set(args) == 0
    assert sim.leds == {1: (f"0000{expected_blue:02x}", 100)}
    assert not switch.connected


@pytest.mark.anyio
@pytest.mark.parametrize("schema,qualified,expected", [
    (True, True, 0),
    (True, False, 1),
    (False, True, 1),
])
async def test_probe_reports_output_qualification_before_closing(monkeypatch, capsys, schema, qualified, expected):
    class ProbeSwitch:
        closed = False
        qualification_checks = 0
        num_ports = 2
        hardware_profile = SimpleNamespace(model="Test switch", copper_ports=(1, 2), optical_ports=(3, 4))

        async def connect(self):
            pass

        async def probe(self):
            return {"hostname": "", "etherlight_mcu": schema, "port_rgb": schema}

        def require_output_profile(self):
            assert not self.closed, "Closing revokes the qualified connection"
            self.qualification_checks += 1
            if not qualified:
                raise ValueError("Firmware verification is required")

        async def read_links(self):
            return {}

        async def read_poe(self):
            return {}

        async def close(self):
            self.closed = True

        async def set_port(self, *args, **kwargs):
            pytest.fail("A probe must never write lighting")

        async def initialize_output(self):
            pytest.fail("A probe must never claim lighting")

    switch = ProbeSwitch()
    monkeypatch.setattr(cli, "_switch", lambda args: switch)
    assert await cli._probe(argparse.Namespace()) == expected
    output = capsys.readouterr().out
    assert "Model           Test switch" in output
    assert f"EE output       {'verified' if expected == 0 else 'not verified'}" in output
    assert switch.closed
    assert switch.qualification_checks == int(schema)
    if schema and not qualified:
        assert "Firmware verification is required" in output
    assert "   4  down" in output


class KeyedSimulator(SimulatedTransport):
    """The simulator behind the saved-key check of SSHTransport, built with SSHTransport's arguments."""

    presented = FP_B
    attempts = 0

    def __init__(self, host, *, port=22, host_keys=None, **credentials):
        super().__init__(latency=0)
        self.host, self.port, self.host_keys = host, port, host_keys

    async def connect(self):
        type(self).attempts += 1
        pinned = self.host_keys.get(self.host, self.port)
        if pinned is not None and pinned != self.presented:
            raise HostKeyMismatchError(f"The SSH key of {self.host}:{self.port} changed.", presented=self.presented,
                                       pinned=pinned)
        await super().connect()
        self.host_keys.accept(self.host, self.port, self.presented)


@pytest.fixture
def keyed_cli(tmp_path, monkeypatch):
    monkeypatch.setattr(cli, "data_dir", lambda: tmp_path)
    monkeypatch.setattr(cli, "SSHTransport", KeyedSimulator)
    monkeypatch.setattr(KeyedSimulator, "attempts", 0)
    return HostKeyStore(tmp_path / "known_switches.json")


def probe(*extra):
    return cli.main(["probe", "192.168.1.2", "-u", "fixture", "-p", "fixture-only", *extra])


def test_probe_of_a_new_switch_saves_its_key(keyed_cli, capsys):
    assert probe() in (0, 1)
    assert keyed_cli.get("192.168.1.2", 22) == FP_B and KeyedSimulator.attempts == 1
    assert "--accept-host-key" not in capsys.readouterr().err


def test_probe_prints_a_changed_key_and_keeps_the_saved_one(keyed_cli, capsys):
    keyed_cli.accept("192.168.1.2", 22, FP_A)
    assert probe() == 2
    error = capsys.readouterr().err
    assert FP_B in error and f"--accept-host-key {FP_B}" in error
    assert keyed_cli.get("192.168.1.2", 22) == FP_A and KeyedSimulator.attempts == 1


def test_accept_host_key_flag_replaces_a_changed_key(keyed_cli, capsys):
    keyed_cli.accept("192.168.1.2", 22, FP_A)
    assert probe("--accept-host-key", "SHA256:" + "C" * 43) == 2
    error = capsys.readouterr().err
    assert keyed_cli.get("192.168.1.2", 22) == FP_A and "forget-host" not in error
    assert f"--accept-host-key {FP_B}" in error
    assert probe("--accept-host-key", FP_B) in (0, 1)
    assert keyed_cli.get("192.168.1.2", 22) == FP_B and KeyedSimulator.attempts == 3


def test_accept_host_key_flag_leaves_switch_playback_to_the_app(keyed_cli, tmp_path, capsys):
    keyed_cli.accept("192.168.1.2", 22, FP_A)
    PlaybackRecord(tmp_path / "switch-playback.json").save(
        {"state": "last_playing", "device": device_identity(FP_A), "owner": "a" * 48})
    assert probe("--accept-host-key", FP_B) == 2
    assert "Accept the new key on the Connect page" in capsys.readouterr().err
    assert keyed_cli.get("192.168.1.2", 22) == FP_A


@pytest.mark.parametrize("value", ["SHA256:short", "MD5:" + "A" * 43, FP_A + "A"])
def test_accept_host_key_flag_requires_a_sha256_fingerprint(value):
    with pytest.raises(SystemExit) as exc:
        cli.build_parser().parse_args(["probe", "192.168.1.2", "--accept-host-key", value])
    assert exc.value.code == 2
