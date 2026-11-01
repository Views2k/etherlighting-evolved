"""Offline identity, address and ownership checks for qualified firmware profiles."""

from dataclasses import replace

import pytest
from test_native_switch import MemoryTransport, clock  # noqa: F401

from etherlighting_evolved.color import Color
from etherlighting_evolved.hardware import PRO_XG8, PROFILES, identify_profile
from etherlighting_evolved.switch import EtherlightSwitch, PortOutput
from etherlighting_evolved.ubus import _precise_port_data, _sfp_mode_data, _sfp_precise_data


def identity(profile):
    return (f"board.name={profile.model}\nboard.shortname={profile.key}\n"
            f"board.sysid=0x{profile.sysid}\n")


QUALIFIED = tuple(p for p in PROFILES.values() if p.output_verified)


@pytest.mark.parametrize("profile", PROFILES.values(), ids=lambda p: p.key)
def test_catalog_identity_and_disjoint_physical_layout(profile):
    assert identify_profile(identity(profile), strict=True) is profile
    copper, optical, stack = map(set, (profile.copper_ports, profile.optical_ports, profile.stack_ports))
    assert not copper & optical and not copper & stack and not optical & stack
    assert copper | optical | stack == set(range(1, len(copper | optical | stack) + 1))
    assert identify_profile(identity(profile) + "board.sysid=0xffff\n", strict=True) is None
    assert identify_profile(identity(profile).replace(profile.sysid, "ffff"), strict=True) is None
    assert identify_profile(identity(profile).replace(profile.model, "Other"), strict=True) is None


def test_partial_identity_cannot_qualify_output_and_copy_cannot_widen_addresses():
    assert identify_profile("board.shortname=USPXG8P") is PRO_XG8
    assert identify_profile("board.shortname=USPXG8P", strict=True) is None
    forged = replace(PRO_XG8, copper_ports=tuple(range(1, 65)))
    with pytest.raises(ValueError):
        _precise_port_data(64, (1, 2, 3), profile=forged)


@pytest.mark.parametrize("profile", QUALIFIED, ids=lambda p: p.key)
def test_encoded_port_commands_use_only_explicit_profile_membership(profile):
    for port in profile.copper_ports:
        assert f"{port} 65280 257 1 0".encode() in _precise_port_data(port, (65280, 257, 1), profile=profile)
        with pytest.raises(ValueError):
            _sfp_mode_data(port, True, profile=profile)
    for port, mask in profile.sfp_masks:
        bits = int(mask, 16)
        assert bits == 1 << (port - 1)
        words = f"{bits >> 32:x} {bits & 0xffffffff:x}"
        assert f"soc_port_map steady {words}".encode() in _sfp_mode_data(port, True, profile=profile)
        assert f"blink_port_map steady {words}".encode() in _sfp_mode_data(port, False, profile=profile)
        assert f"{port} 1 2 3 0".encode() in _sfp_precise_data(port, (1, 2, 3), profile=profile)
        with pytest.raises(ValueError):
            _precise_port_data(port, (1, 2, 3), profile=profile)
    for port in (False, True, -1, 0, 65, max(profile.optical_ports) + 1):
        with pytest.raises(ValueError):
            _precise_port_data(port, (1, 2, 3), profile=profile)
        with pytest.raises(ValueError):
            _sfp_mode_data(port, True, profile=profile)


@pytest.mark.parametrize("profile", [p for p in PROFILES.values() if not p.output_verified], ids=lambda p: p.key)
def test_catalog_presence_does_not_grant_output(profile):
    for port in (*profile.copper_ports, *profile.optical_ports, *profile.stack_ports):
        with pytest.raises(ValueError):
            _precise_port_data(port, (1, 2, 3), profile=profile)
        with pytest.raises(ValueError):
            _sfp_mode_data(port, True, profile=profile)


@pytest.mark.anyio
@pytest.mark.usefixtures("clock")
@pytest.mark.parametrize("profile", QUALIFIED, ids=lambda p: p.key)
async def test_detected_layout_native_output_and_scoped_cleanup(profile):
    device = MemoryTransport()
    device.board = identity(profile)
    device.daemon = profile.daemon_sha256 + "  /proc/123/exe"
    device.firmware = profile.mcu_sha256 + "  /usr/share/firmware/port_led_fw.bin"
    switch = EtherlightSwitch(device, auto_detect=True)
    await switch.connect()
    info = await switch.probe()
    assert not device.events
    assert switch.hardware_profile is profile
    assert switch.num_ports == len(profile.copper_ports)
    assert switch.sfp_ports == profile.optical_ports
    assert info["native_breathing"] and info["sfp_native_gradient"]
    assert device.profile is profile
    switch.require_output_profile()
    await switch.initialize_output()
    color = Color(0, 0, 255)
    native = PortOutput(color, 100, (0, 0, 65280), "breath_1s")
    steady = PortOutput(color, 100, (0, 0, 65280))
    await switch.write([native] * switch.num_ports, sfp={p: steady for p in profile.optical_ports})
    assert {e[1] for e in device.events if e[0] == "pwm"} == set(profile.copper_ports)
    assert {e[1] for e in device.events if e[0] == "mode"} == set(profile.optical_ports)
    gradient = PortOutput(color, 100, (0, 0, 65280), gradient="gradient_1s", transition_id=1)
    await switch.write([native] * switch.num_ports, sfp={p: gradient for p in profile.optical_ports})
    assert all(switch.sfp_transition(p) is not None for p in profile.optical_ports)
    await switch.quiesce_native([steady] * switch.num_ports)
    await switch.quiesce_sfp()
    assert not switch.native_uncertain and not switch._sfp_owned and not switch._native_active
    assert [e for e in device.events if e[0] == "mode" and e[2] is False] == [
        ("mode", p, False) for p in profile.optical_ports]
    await switch.close()


@pytest.mark.anyio
@pytest.mark.parametrize("fault", ["daemon", "firmware", "identity", "topology"])
async def test_profile_output_gate_refuses_mismatch(fault):
    profile = PROFILES["USPH24"]
    device = MemoryTransport()
    device.board = identity(profile)
    if fault in {"daemon", "firmware"}:
        setattr(device, fault, "0" * 64 + "  " + getattr(device, fault).split()[1])
    if fault == "identity":
        device.board += "board.name=USW-Pro-XG-8-PoE\n"
    switch = EtherlightSwitch(device, auto_detect=fault != "topology")
    await switch.connect()
    await switch.probe()
    with pytest.raises(ValueError):
        switch.require_output_profile()
    assert not device.events and not switch.sfp_ports


@pytest.mark.anyio
@pytest.mark.parametrize("profile", [p for p in PROFILES.values() if not p.output_verified], ids=lambda p: p.key)
async def test_unqualified_family_discovery_is_read_only(profile):
    device = MemoryTransport()
    device.board = identity(profile)
    switch = EtherlightSwitch(device, auto_detect=True)
    await switch.connect()
    info = await switch.probe()
    assert switch.num_ports == len(profile.copper_ports)
    assert info["hardware_profile"]["id"] == profile.key
    assert not switch.sfp_ports and not device.events
    with pytest.raises(ValueError):
        switch.require_output_profile()
