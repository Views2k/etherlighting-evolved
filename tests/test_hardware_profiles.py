"""Offline identity, address and ownership checks for qualified firmware profiles."""

from dataclasses import replace

import pytest
from test_native_switch import MemoryTransport, clock  # noqa: F401

from etherlighting_evolved.color import Color
from etherlighting_evolved.hardware import (
    MIPS_DAEMON,
    MIPS_MCU,
    PRO_XG8,
    PROFILES,
    USWED72_7_6_11_DAEMON,
    USWED72_SET_FIELDS,
    identify_profile,
)
from etherlighting_evolved.switch import EtherlightSwitch, PortOutput
from etherlighting_evolved.ubus import _precise_port_data, _sfp_mode_data, _sfp_precise_data


def identity(profile):
    return (f"board.name={profile.model}\nboard.shortname={profile.key}\n"
            f"board.sysid=0x{profile.sysid}\n")


QUALIFIED = tuple(p for p in PROFILES.values() if p.output_verified)
QUALIFIED_FIRMWARE = tuple(
    pytest.param(profile, daemon, mcu, id=f"{profile.key}-{daemon[:8]}")
    for profile in QUALIFIED for daemon, mcu in profile.firmware_pairs
)


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
@pytest.mark.parametrize("profile,daemon,mcu", QUALIFIED_FIRMWARE)
async def test_detected_layout_native_output_and_scoped_cleanup(profile, daemon, mcu):
    device = MemoryTransport()
    device.board = identity(profile)
    device.daemon = daemon + "  /proc/123/exe"
    device.firmware = mcu + "  /usr/share/firmware/port_led_fw.bin"
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
@pytest.mark.parametrize("profile,daemon,mcu", QUALIFIED_FIRMWARE)
@pytest.mark.parametrize("fault", ["daemon", "firmware", "identity", "topology", "schema", "connection"])
async def test_profile_output_gate_revokes_previous_qualification_on_mismatch(profile, daemon, mcu, fault):
    device = MemoryTransport()
    device.board = identity(profile)
    device.daemon = daemon + "  /proc/123/exe"
    device.firmware = mcu + "  /usr/share/firmware/port_led_fw.bin"
    switch = EtherlightSwitch(device, auto_detect=True)
    await switch.connect()
    await switch.probe()
    switch.require_output_profile()
    assert device.profile is profile

    if fault in {"daemon", "firmware"}:
        setattr(device, fault, "0" * 64 + "  " + getattr(device, fault).split()[1])
    elif fault == "identity":
        device.board += "board.sysid=0xffff\n"
    elif fault == "topology":
        switch.auto_detect = False
        switch.num_ports = len(profile.copper_ports) + 1
    elif fault == "schema":
        device.precise_rgb_supported = device.sfp_mode_supported = False
    elif fault == "connection":
        from etherlighting_evolved.switch import PORT_FIRMWARE_HASH_COMMAND

        async def replace_connection(command):
            if command == PORT_FIRMWARE_HASH_COMMAND:
                device.lighting_generation = object()

        device.read_hook = replace_connection

    await switch.probe()
    with pytest.raises(ValueError):
        switch.require_output_profile()
    assert device.profile is None
    assert not device.events and not switch.sfp_ports
    assert not switch.native_breathing and not switch.scoped_sfp

    device.board = identity(profile)
    device.daemon = daemon + "  /proc/123/exe"
    device.firmware = mcu + "  /usr/share/firmware/port_led_fw.bin"
    device.precise_rgb_supported = device.sfp_mode_supported = True
    device.read_hook = None
    switch.auto_detect = True
    await switch.probe()
    switch.require_output_profile()
    assert device.profile is profile
    await switch.close()
    assert device.profile is None and not device.events


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


@pytest.mark.anyio
@pytest.mark.parametrize("profile,daemon,mcu", QUALIFIED_FIRMWARE)
@pytest.mark.parametrize("fault", ["foreign_daemon", "foreign_mcu", "daemon_path", "mcu_path"])
async def test_firmware_admission_rejects_cross_family_hashes_and_wrong_paths(profile, daemon, mcu, fault):
    other = next(p for p in QUALIFIED if p.family != profile.family and p.mcu_sha256 != mcu)
    device = MemoryTransport()
    device.board = identity(profile)
    device.daemon = daemon + "  /proc/123/exe"
    device.firmware = mcu + "  /usr/share/firmware/port_led_fw.bin"
    if fault == "foreign_daemon":
        device.daemon = other.daemon_sha256 + "  /proc/123/exe"
    elif fault == "foreign_mcu":
        device.firmware = other.mcu_sha256 + "  /usr/share/firmware/port_led_fw.bin"
    elif fault == "daemon_path":
        device.daemon = daemon + "  /bin/ubntbox"
    else:
        device.firmware = mcu + "  /tmp/port_led_fw.bin"
    switch = EtherlightSwitch(device, auto_detect=True)
    await switch.connect()
    await switch.probe()
    with pytest.raises(ValueError):
        switch.require_output_profile()
    assert device.profile is None and not device.events
    assert not switch.native_breathing and not switch.scoped_sfp
    await switch.close()


@pytest.mark.anyio
@pytest.mark.parametrize("profile", [p for p in QUALIFIED if len(p.firmware_pairs) > 1], ids=lambda p: p.key)
async def test_firmware_upgrade_preserves_saved_scene_and_device_identity(profile, config):
    from etherlighting_evolved.session import SessionManager

    manager = SessionManager(config)
    device = MemoryTransport()
    device.board = identity(profile)
    device.host_key_fingerprint = "SHA256:firmware-upgrade-fixture"
    switch = EtherlightSwitch(device, auto_detect=True)
    await switch.connect()
    saved = None
    for daemon, mcu in (*profile.firmware_pairs, profile.firmware_pairs[0]):
        device.daemon = daemon + "  /proc/123/exe"
        device.firmware = mcu + "  /usr/share/firmware/port_led_fw.bin"
        await switch.probe()
        switch.require_output_profile()
        store = manager._device_store(switch)
        if saved is None:
            store.save({"brightness": 37, "name": "Saved settings"})
            saved = store
        else:
            assert store.path == saved.path and store.identity == saved.identity
            assert store.load() == {"brightness": 37, "name": "Saved settings"}
    assert not device.events
    await switch.close()


@pytest.mark.anyio
@pytest.mark.parametrize("daemon_variant,mcu_variant,accepted", [
    (0, 0, True), (1, 1, True), (0, 1, False), (1, 0, False),
])
async def test_firmware_pair_admission_does_not_allow_crossed_pairs(
        daemon_variant, mcu_variant, accepted, monkeypatch):
    from etherlighting_evolved import hardware

    profile = PROFILES["USWPXG24"]
    daemons = (profile.daemon_sha256, "a" * 64)
    mcus = (profile.mcu_sha256, "b" * 64)
    monkeypatch.setattr(hardware, "_ADDITIONAL_FIRMWARE", {
        profile.family: ((daemons[1], mcus[1]),),
    })
    device = MemoryTransport()
    device.board = identity(profile)
    device.daemon = daemons[daemon_variant] + "  /proc/123/exe"
    device.firmware = mcus[mcu_variant] + "  /usr/share/firmware/port_led_fw.bin"
    switch = EtherlightSwitch(device, auto_detect=True)
    await switch.connect()
    await switch.probe()
    if accepted:
        switch.require_output_profile()
        assert device.profile is profile
    else:
        with pytest.raises(ValueError):
            switch.require_output_profile()
        assert device.profile is None
        assert not switch.native_breathing and not switch.scoped_sfp
    assert not device.events
    await switch.close()


async def probe_uswed72(profile, daemon, fields):
    device = MemoryTransport()
    device.board = identity(profile)
    device.daemon = daemon + "  /proc/123/exe"
    device.firmware = MIPS_MCU + "  /usr/share/firmware/port_led_fw.bin"
    device.lighting_set_fields = fields
    switch = EtherlightSwitch(device, auto_detect=True)
    await switch.connect()
    await switch.probe()
    return device, switch


@pytest.mark.anyio
@pytest.mark.parametrize("profile", [p for p in QUALIFIED if p.family == "USWED72"], ids=lambda p: p.key)
async def test_uswed72_7_6_11_is_admitted_only_for_the_pro_xg8(profile):
    device, switch = await probe_uswed72(profile, USWED72_7_6_11_DAEMON, dict(USWED72_SET_FIELDS))
    if profile is PRO_XG8:
        switch.require_output_profile()
        assert device.profile is profile and switch.native_breathing and switch.scoped_sfp
    else:
        with pytest.raises(ValueError, match="needs compatibility verification"):
            switch.require_output_profile()
        assert device.profile is None and not switch.native_breathing and not switch.scoped_sfp
    assert not device.events
    await switch.close()


@pytest.mark.anyio
@pytest.mark.parametrize("change,detail", [
    ({"port_rgb_v2": 3}, "unexpected fields: port_rgb_v2"),
    ({"diag": None}, "missing fields: diag"),
    ({"brightness": 3}, "changed types: brightness"),
    ({"port_pwm": -1}, "changed types: port_pwm"),
    (None, "no set interface was reported"),
])
async def test_uswed72_7_6_11_requires_the_reviewed_set_interface(change, detail):
    if change is None:
        fields = None
    else:
        fields = dict(USWED72_SET_FIELDS)
        for name, kind in change.items():
            if kind is None:
                del fields[name]
            else:
                fields[name] = kind
    device, switch = await probe_uswed72(PRO_XG8, USWED72_7_6_11_DAEMON, fields)
    with pytest.raises(ValueError, match="differs from the one EE was verified with") as refused:
        switch.require_output_profile()
    assert detail in str(refused.value)
    assert device.profile is None and not device.events
    assert not switch.native_breathing and not switch.scoped_sfp and not switch.precise_rgb
    # The reviewed interface is admitted on the next probe, and the difference is no longer reported.
    device.lighting_set_fields = dict(USWED72_SET_FIELDS)
    await switch.probe()
    switch.require_output_profile()
    assert device.profile is PRO_XG8 and switch.interface_difference == ""
    await switch.close()


@pytest.mark.anyio
async def test_uswed72_7_5_15_needs_no_interface_report():
    device, switch = await probe_uswed72(PRO_XG8, MIPS_DAEMON, None)
    switch.require_output_profile()
    assert device.profile is PRO_XG8 and switch.interface_difference == ""
    assert PRO_XG8.required_set_fields(MIPS_DAEMON) is None
    assert PRO_XG8.required_set_fields(USWED72_7_6_11_DAEMON) == USWED72_SET_FIELDS
    assert PROFILES["USPXG10P"].required_set_fields(USWED72_7_6_11_DAEMON) is None
    await switch.close()
