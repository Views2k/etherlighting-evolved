"""Offline contract checks for captured SONiC MCU register observations."""

import struct
from dataclasses import replace

import pytest

from etherlighting_evolved.hardware import PROFILES
from etherlighting_evolved.sonic_lighting import (
    SonicLayout,
    decode_port_snapshot,
    sonic_layout,
)

MCU = "5bc7567ef4bbd07bc8bc150c73662699e115d992141ed5b2f66d9b03616fb6a0"
BOARDS = (("ECSAGG", 8, 54), ("ECSCORE", 17, 32))


def layout_for(key, board):
    return sonic_layout(PROFILES[key], board_id=board, mcu_sha256=MCU)


@pytest.mark.parametrize("key,board,count", BOARDS)
def test_every_user_endpoint_decodes_high_byte_first_without_enabling_output(key, board, count):
    profile = PROFILES[key]
    layout = layout_for(key, board)
    for port in range(1, count + 1):
        values = (0x1234, 0xABCD, 0xFF00, 0x0102)
        snapshot = decode_port_snapshot(
            layout, port, bytes([port - 1]) + struct.pack(">4H", *values), selector_after=port - 1,
        )
        assert snapshot.port == port
        assert snapshot.channels == values
    assert profile.firmware_pairs == ()
    with pytest.raises(ValueError, match="not been qualified"):
        profile.require_output()


@pytest.mark.parametrize("key,board,count", BOARDS)
def test_internal_slots_and_global_port_zero_are_not_user_endpoints(key, board, count):
    layout = layout_for(key, board)
    for port in (False, True, 0, -1, count + 1, count + 2, 61, 256, 1.0, "1"):
        with pytest.raises(ValueError, match="Port"):
            decode_port_snapshot(layout, port, bytes(9), selector_after=0)


@pytest.mark.parametrize("key,board,count", BOARDS)
def test_wrong_board_or_firmware_cannot_select_a_layout(key, board, count):
    for bad_board in (True, False, str(board), float(board), -1, 0, 17 if board == 8 else 8, 18):
        with pytest.raises(ValueError, match="identity"):
            sonic_layout(PROFILES[key], board_id=bad_board, mcu_sha256=MCU)
    for bad_hash in ("", MCU.upper(), "0" * 64, MCU[:-1], None):
        with pytest.raises(ValueError, match="identity"):
            sonic_layout(PROFILES[key], board_id=board, mcu_sha256=bad_hash)


@pytest.mark.parametrize("profile", [p for p in PROFILES.values() if p.key not in {"ECSAGG", "ECSCORE"}])
def test_other_firmware_families_cannot_use_sonic_mapping(profile):
    with pytest.raises(ValueError, match="SONiC"):
        sonic_layout(profile, board_id=8, mcu_sha256=MCU)


def test_copied_profiles_or_layouts_cannot_widen_endpoint_bounds():
    for profile in (replace(PROFILES["ECSAGG"]), replace(PROFILES["ECSAGG"], optical_ports=tuple(range(1, 65)))):
        with pytest.raises(ValueError, match="canonical"):
            sonic_layout(profile, board_id=8, mcu_sha256=MCU)
    for layout in (SonicLayout("ECSAGG", 8, 64), replace(layout_for("ECSAGG", 8))):
        with pytest.raises(ValueError, match="decoded SONiC layout"):
            decode_port_snapshot(layout, 1, bytes(9), selector_after=0)


@pytest.mark.parametrize("length", (0, 1, 8, 10, 4096))
def test_truncated_or_extra_register_data_is_rejected(length):
    with pytest.raises(ValueError, match="exactly nine"):
        decode_port_snapshot(layout_for("ECSAGG", 8), 1, bytes(length), selector_after=0)


@pytest.mark.parametrize("value", (bytearray(9), memoryview(bytes(9)), [0] * 9, "0" * 9, None))
def test_register_snapshot_must_be_immutable_bytes(value):
    with pytest.raises(ValueError, match="exactly nine"):
        decode_port_snapshot(layout_for("ECSAGG", 8), 1, value, selector_after=0)


@pytest.mark.parametrize("selector,after", ((1, 0), (0, 1), (1, 1), (255, 0), (0, 255)))
def test_misaddressed_or_interleaved_selector_observation_is_rejected(selector, after):
    with pytest.raises(ValueError, match="selector changed"):
        decode_port_snapshot(layout_for("ECSCORE", 17), 1, bytes([selector]) + bytes(8), selector_after=after)


@pytest.mark.parametrize("after", (False, True, -1, 256, 0.0, "0", None))
def test_invalid_subsequent_selector_echo_is_rejected(after):
    with pytest.raises(ValueError, match="one byte"):
        decode_port_snapshot(layout_for("ECSCORE", 17), 1, bytes(9), selector_after=after)


def test_snapshot_preserves_full_u16_channels_including_auxiliary():
    raw = b"\x00" + struct.pack(">4H", 0, 65535, 65281, 65535)
    snapshot = decode_port_snapshot(layout_for("ECSCORE", 17), 1, raw, selector_after=0)
    assert snapshot.channels == (0, 65535, 65281, 65535)
