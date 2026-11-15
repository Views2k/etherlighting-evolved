"""Switch identity, physical topology and qualified lighting boundaries."""

from __future__ import annotations

import re
from dataclasses import dataclass
from types import MappingProxyType

MIPS_DAEMON = "67fb1ca03ef7d6325dc1eed8628e82c93462c50d52c15e5778a1483bae50a91c"
MIPS_MCU = "cb99c14250ed28c77a00d4df46bf699db6b87f5a1094d0b10cfc79f2d5832a8c"
_FIRMWARE = {
    "USWED72": (MIPS_DAEMON, MIPS_MCU),
    "USWED42": ("338b23b02714e08fb2bac6f852565caa5768ed7c030a653c6bda1fa95ba17ce3",
                "8c79c0050ba28fa933f3b1a71d530665ef560775793f133fae17a85482508322"),
    "USWF001": ("326439ee93734c968d600363354084f1d79926f300e81197b06c8cad16d3b75e",
                "8c79c0050ba28fa933f3b1a71d530665ef560775793f133fae17a85482508322"),
    "USWF004": ("36f84073384f271008f83dd37ac900b2c59e68e8a210de30fb9fa755d1d49ec3",
                "9d8740e2767cf676822648573d00ec82d07f5903e9ebad197684f5be17267224"),
}
# Retain the original pair above because saved scene identities include it.
_ADDITIONAL_FIRMWARE = {
    "USWED42": (("9de90875f77a65c5d3006308ab4e707a79d420cca8f4c4994a3de2896df59109",
                  "8c79c0050ba28fa933f3b1a71d530665ef560775793f133fae17a85482508322"),),
    "USWF001": (("4599de216f558d4425782518a7cfd8fe234bc2d4eba3967c9802951debdb4220",
                  "8c79c0050ba28fa933f3b1a71d530665ef560775793f133fae17a85482508322"),),
}


@dataclass(frozen=True, slots=True)
class HardwareProfile:
    key: str
    model: str
    sysid: str
    family: str
    copper_ports: tuple[int, ...]
    optical_ports: tuple[int, ...]
    stack_ports: tuple[int, ...]
    protocol: str
    output_verified: bool = False
    daemon_sha256: str = ""
    mcu_sha256: str = ""
    sfp_masks: tuple[tuple[int, str], ...] = ()

    @property
    def firmware_pairs(self) -> tuple[tuple[str, str], ...]:
        if PROFILES.get(self.key) is not self or not self.output_verified:
            return ()
        return ((self.daemon_sha256, self.mcu_sha256), *_ADDITIONAL_FIRMWARE.get(self.family, ()))

    def public_dict(self) -> dict:
        return {
            "id": self.key, "model": self.model, "family": self.family,
            "copper_ports": list(self.copper_ports), "optical_ports": list(self.optical_ports),
            "stack_ports": list(self.stack_ports), "protocol": self.protocol,
            "output_verified": self.output_verified,
            "verification": "hardware" if self.key == "USPXG8P" else "firmware" if self.output_verified else "pending",
        }

    def require_output(self) -> None:
        if PROFILES.get(self.key) is not self or not self.output_verified:
            raise ValueError("Lighting output has not been qualified for this hardware profile")

    def validate_port(self, port: int, *, optical: bool = False) -> None:
        self.require_output()
        ports = self.optical_ports if optical else self.copper_ports
        if type(port) is not int or port not in ports:
            kind = "optical" if optical else "copper"
            raise ValueError(f"Port is outside the qualified {kind} lighting layout")

    def sfp_mask(self, port: int) -> str:
        self.validate_port(port, optical=True)
        masks = dict(self.sfp_masks)
        if port not in masks:
            raise ValueError("Scoped control is not qualified for this optical port")
        return masks[port]


def _profile(key, model, sysid, family, copper, optical, stack=0):
    protocol = "proc-led" if family == "USPM16" else "sonic-mcu" if family == "USWF066" else "ubus-mcu"
    masks = {
        "USPXG8P": ((9, "100"), (10, "200")),
        "USPXG10P": ((11, "400"), (12, "800")),
        "USPH24": ((25, "1000000"), (26, "2000000"), (27, "4000000"), (28, "8000000")),
        "USPH24P": ((25, "1000000"), (26, "2000000"), (27, "4000000"), (28, "8000000")),
        "USWPXG24": ((25, "1000000"), (26, "2000000")),
        "USWPXG24P": ((25, "1000000"), (26, "2000000")),
        "USWPXG48": tuple((p, f"{1 << (p - 1):x}") for p in range(49, 53)),
        "USWPXG48P": tuple((p, f"{1 << (p - 1):x}") for p in range(49, 53)),
        "USWPXGAGG": tuple((p, f"{1 << (p - 1):x}") for p in range(1, 33)),
        "EAS24P": ((25, "1000000"), (26, "2000000")),
        "EAS48P": tuple((p, f"{1 << (p - 1):x}") for p in range(49, 53)),
        "ECS24S": tuple((p, f"{1 << (p - 1):x}") for p in range(25, 29)),
        "ECS24SP": tuple((p, f"{1 << (p - 1):x}") for p in range(25, 29)),
        "ECS48S": tuple((p, f"{1 << (p - 1):x}") for p in range(49, 53)),
        "ECS48SP": tuple((p, f"{1 << (p - 1):x}") for p in range(49, 53)),
        "EAV24P": tuple((p, f"{1 << (p - 1):x}") for p in range(25, 29)),
        "EAVAGG": tuple((p, f"{1 << (p - 1):x}") for p in range(1, 23)),
    }
    verified = key in masks
    daemon, mcu = _FIRMWARE[family] if verified else ("", "")
    return HardwareProfile(
        key, model, sysid, family, tuple(range(1, copper + 1)),
        tuple(range(copper + 1, copper + optical + 1)),
        tuple(range(copper + optical + 1, copper + optical + stack + 1)), protocol,
        verified, daemon, mcu,
        masks.get(key, ()),
    )


# Physical topology comes from the corresponding firmware's board records.
# Physical addresses alone do not grant lighting output or ownership changes.
_CATALOG = (
    _profile("USPM16", "USW-Pro-Max-16", "ed71", "USPM16", 16, 2),
    _profile("USPM16P", "USW-Pro-Max-16-PoE", "ed70", "USPM16", 16, 2),
    _profile("USPM24", "USW-Pro-Max-24", "ed5c", "USPM16", 24, 2),
    _profile("USPM24P", "USW-Pro-Max-24-PoE", "ed5b", "USPM16", 24, 2),
    _profile("USPM48", "USW-Pro-Max-48", "ed5e", "USPM16", 48, 4),
    _profile("USPM48P", "USW-Pro-Max-48-PoE", "ed5d", "USPM16", 48, 4),
    _profile("USPH24", "USW-Pro-HD-24", "ed73", "USWED72", 24, 4),
    _profile("USPH24P", "USW-Pro-HD-24-PoE", "ed72", "USWED72", 24, 4),
    _profile("USPXG8P", "USW-Pro-XG-8-PoE", "ed76", "USWED72", 8, 2),
    _profile("USPXG10P", "USW-Pro-XG-10-PoE", "ed77", "USWED72", 10, 2),
    _profile("USWPXG24", "USW-Pro-XG-24", "ed45", "USWED42", 24, 2),
    _profile("USWPXG24P", "USW-Pro-XG-24-PoE", "ed44", "USWED42", 24, 2),
    _profile("USWPXG48", "USW-Pro-XG-48", "ed43", "USWED42", 48, 4),
    _profile("USWPXG48P", "USW-Pro-XG-48-PoE", "ed42", "USWED42", 48, 4),
    _profile("USWPXGAGG", "USW-Pro-XG-Aggregation", "f003", "USWED42", 0, 32),
    _profile("EAS24P", "ECS-24-PoE", "f067", "USWED42", 24, 2),
    _profile("EAS48P", "ECS-48-PoE", "f069", "USWED42", 48, 4),
    _profile("ECS24S", "ECS-24S", "f005", "USWF004", 24, 4, 2),
    _profile("ECS24SP", "ECS-24S-PoE", "f004", "USWF004", 24, 4, 2),
    _profile("ECS48S", "ECS-48S", "f007", "USWF004", 48, 4, 2),
    _profile("ECS48SP", "ECS-48S-PoE", "f006", "USWF004", 48, 4, 2),
    _profile("EAV24P", "EAV-XG-24-PoE", "f001", "USWF001", 24, 4),
    _profile("EAVAGG", "EAV-Fiber", "f002", "USWF001", 0, 22),
    _profile("ECSAGG", "ECS-Aggregation", "f066", "USWF066", 0, 54),
    _profile("ECSCORE", "ECS-Core", "f07d", "USWF066", 0, 32),
)
PROFILES = MappingProxyType({profile.key: profile for profile in _CATALOG})
PRO_XG8 = PROFILES["USPXG8P"]


def board_fields(board_info: str) -> dict[str, str]:
    if not isinstance(board_info, str) or len(board_info) > 16384:
        return {}
    fields = {}
    for line in board_info.splitlines():
        key, separator, value = line.partition("=")
        if separator and key in {"board.name", "board.shortname", "board.sysid"}:
            if key in fields or not value.strip():
                return {}
            fields[key] = value.strip()
    if "board.sysid" in fields:
        sysid = fields["board.sysid"]
        if not re.fullmatch(r"(?:0x)?[0-9a-fA-F]{4}", sysid):
            return {}
        fields["board.sysid"] = sysid.removeprefix("0x").lower()
    return fields


def identify_profile(board_info: str, *, strict: bool = False) -> HardwareProfile | None:
    fields = board_fields(board_info)
    if not fields or (strict and not {"board.shortname", "board.sysid"} <= fields.keys()):
        return None
    if not {"board.name", "board.shortname"} & fields.keys():
        return None
    matches = [profile for profile in _CATALOG if all(
        fields.get(key, expected) == expected
        for key, expected in (("board.name", profile.model), ("board.shortname", profile.key),
                              ("board.sysid", profile.sysid))
    )]
    return matches[0] if len(matches) == 1 else None
