"""Decode captured SONiC MCU port state without issuing device commands."""

from __future__ import annotations

import struct
from dataclasses import dataclass
from types import MappingProxyType

from .hardware import PROFILES, HardwareProfile

_MCU_SHA256 = "5bc7567ef4bbd07bc8bc150c73662699e115d992141ed5b2f66d9b03616fb6a0"


@dataclass(frozen=True, slots=True)
class SonicLayout:
    profile_key: str
    board_id: int
    port_count: int


# MCU 1.20.6 table at 0x174c4: 135-byte rows, two internal slots per board.
_LAYOUTS = MappingProxyType({
    "ECSAGG": SonicLayout("ECSAGG", 8, 54),
    "ECSCORE": SonicLayout("ECSCORE", 17, 32),
})


def sonic_layout(profile: HardwareProfile, *, board_id: int, mcu_sha256: str) -> SonicLayout:
    """Match a captured board and MCU to a canonical SONiC endpoint layout."""
    if not isinstance(profile, HardwareProfile) or PROFILES.get(profile.key) is not profile:
        raise ValueError("A canonical hardware profile is required")
    layout = _LAYOUTS.get(profile.key)
    if (layout is None or profile.protocol != "sonic-mcu" or profile.family != "USWF066"
            or profile.copper_ports or profile.stack_ports
            or profile.optical_ports != tuple(range(1, layout.port_count + 1))):
        raise ValueError("The hardware profile has no decoded SONiC endpoint layout")
    if type(board_id) is not int or board_id != layout.board_id or mcu_sha256 != _MCU_SHA256:
        raise ValueError("The captured MCU identity does not match the decoded firmware")
    return layout


@dataclass(frozen=True, slots=True)
class SonicPortSnapshot:
    """MCU software channel values; this observation grants no output ownership."""

    port: int
    channels: tuple[int, int, int, int]


def decode_port_snapshot(
    layout: SonicLayout, port: int, registers: bytes, *, selector_after: int,
) -> SonicPortSnapshot:
    """Decode register bytes 0x10..0x18 and compare a subsequent selector echo.

    The selector handler copies active RGBW16 into snapshot registers, high byte
    first (MCU 0xacfa, 0xbb58). These are captured register values, not a wire
    request format. Equal selector echoes cannot establish freshness, exclude an
    ABA selector change, or prove that the controller has yielded ownership.
    """
    if not isinstance(layout, SonicLayout) or _LAYOUTS.get(layout.profile_key) is not layout:
        raise ValueError("A decoded SONiC layout is required")
    if type(port) is not int or not 1 <= port <= layout.port_count:
        raise ValueError("Port is outside the decoded SONiC endpoint layout")
    if type(registers) is not bytes or len(registers) != 9:
        raise ValueError("The snapshot must contain exactly nine captured register bytes")
    if type(selector_after) is not int or not 0 <= selector_after <= 255:
        raise ValueError("The subsequent selector echo must be one byte")
    selector = port - 1
    if registers[0] != selector or selector_after != selector:
        raise ValueError("The snapshot selector changed or names another port")
    return SonicPortSnapshot(port, struct.unpack(">4H", registers[1:]))
