import argparse
import asyncio
from contextlib import nullcontext
from types import SimpleNamespace

import pytest

from etherlighting_evolved import cli
from etherlighting_evolved.switch import EtherlightSwitch
from etherlighting_evolved.transport import SimulatedTransport, TransportError


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
