import pytest

from etherlighting_evolved.session import LoginError, SessionManager
from etherlighting_evolved.switch import (
    HOSTNAME_COMMAND,
    MCU_SCHEMA_COMMAND,
    MCU_SCHEMA_EXIT,
    EtherlightSwitch,
    parse_mcu_probe,
    parse_mcu_schema_reply,
)
from etherlighting_evolved.transport import ETHERLIGHT_MCU_SIGNATURE, SimulatedTransport, TransportError


def framed(schema=ETHERLIGHT_MCU_SIGNATURE, status="0"):
    return schema + f"\n{MCU_SCHEMA_EXIT}{status}\n"


def test_schema_read_requires_a_successful_status_without_loosening_schema_validation():
    assert parse_mcu_schema_reply(framed())["port_rgb"]
    assert not parse_mcu_schema_reply(framed("'other.object'\n\"set\":{\"port_rgb\":\"String\"}"))["port_rgb"]
    assert not parse_mcu_schema_reply(framed("'etherlight.mcu'\n\"get\":{\"port_rgb\":\"String\"}"))["port_rgb"]


@pytest.mark.parametrize("reply", [
    ETHERLIGHT_MCU_SIGNATURE,
    framed(status="1"), framed(status="127"), framed(status="255"),
    framed(status="256"), framed(status="-1"), framed(status="00"), framed(status=""),
    framed(status="ok"), framed(status="0 extra"), framed() + "late diagnostic\n",
    framed().rstrip("\n"), framed() + f"\n{MCU_SCHEMA_EXIT}0\n",
    framed(""), framed(" \n\t"),
])
def test_missing_failed_empty_or_malformed_schema_result_is_a_read_failure(reply):
    with pytest.raises(TransportError, match="capabilities could not be read"):
        parse_mcu_schema_reply(reply)


def test_interleaved_hostname_diagnostic_cannot_be_accepted_as_a_valid_schema():
    header, methods = ETHERLIGHT_MCU_SIGNATURE.split("\n", 1)
    contaminated = header + "\nsh: hostname: not found\n" + methods
    assert parse_mcu_probe(ETHERLIGHT_MCU_SIGNATURE)["port_rgb"]
    assert not parse_mcu_schema_reply(framed(contaminated))["port_rgb"]
    assert "hostname" not in MCU_SCHEMA_COMMAND
    assert HOSTNAME_COMMAND == "hostname 2>/dev/null"


@pytest.mark.anyio
@pytest.mark.parametrize("hostname", ["", "sh: hostname: not found\n", "fixture-device\n"])
async def test_optional_hostname_is_read_separately_and_cannot_damage_schema(hostname):
    class Device(SimulatedTransport):
        def __init__(self):
            super().__init__(latency=0)
            self.reads = []

        async def run(self, command, **kwargs):
            self.reads.append(command)
            if command == HOSTNAME_COMMAND:
                return hostname
            return await super().run(command, **kwargs)

    device = Device()
    switch = EtherlightSwitch(device)
    await switch.connect()
    try:
        info = await switch.probe()
        assert info["port_rgb"] and info["sfp_direct"]
        assert info["hostname"] == ("fixture-device" if hostname == "fixture-device\n" else "")
        assert device.reads[:2] == [MCU_SCHEMA_COMMAND, HOSTNAME_COMMAND]
        assert device.command_count == 0
    finally:
        await switch.close()


@pytest.mark.anyio
async def test_failed_schema_clears_prior_capabilities_before_any_negotiation():
    class Device(SimulatedTransport):
        async def run(self, command, **kwargs):
            assert command == MCU_SCHEMA_COMMAND
            return framed(status="1")

        async def prepare_rgb(self):
            pytest.fail("A failed schema read must not negotiate lighting")

    device = Device(latency=0)
    switch = EtherlightSwitch(device)
    switch._precise_generation = switch._native_generation = switch._sfp_generation = object()
    switch.sfp_ports = (9, 10)
    with pytest.raises(TransportError):
        await switch.probe()
    assert not switch.precise_rgb and not switch.native_breathing and not switch.scoped_sfp
    assert switch.sfp_ports == () and device.command_count == 0


@pytest.mark.anyio
async def test_failed_read_closes_candidate_without_claiming_unsupported_hardware(config):
    class Device(SimulatedTransport):
        async def run(self, command, **kwargs):
            assert command == MCU_SCHEMA_COMMAND
            return framed(status="127")

    device = Device(latency=0)
    sessions = SessionManager(config, transport_factory=lambda **kwargs: device)
    with pytest.raises(LoginError) as error:
        await sessions.connect("demo")
    assert error.value.code == "ssh_error" and error.value.status == 502
    assert sessions.engine is None and not device.connected and device.command_count == 0
