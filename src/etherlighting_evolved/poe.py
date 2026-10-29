"""PoE status, read from `swctrl poe show`.

The parser is modeled on what other UniFi switches print:

    Port  OpMode      HpMode    PwrLimit   Class   PoEPwr  PwrGood  Power(W)  Voltage(V)  Current(mA)
                                  (mW)
    ----  ------  ------------  --------  -------  ------  -------  --------  ----------  -----------
       1    Auto  Dot3bt-Type3     30500  Class 4      On     Good      3.43       53.75        64.00
       2    Auto  Dot3bt-Type3     30500  Unknown     Off      Bad      0.00        0.00         0.00

It hasn't been verified on every model. Lines that don't parse are skipped, so with an unknown format the PoE
values stay empty and PoE rules don't match; the LEDs keep working.

The PoE type comes from the class requested by the powered device:
class 0-3 is 802.3af (PoE), 4 is 802.3at (PoE+), 5-6 is 802.3bt type 3 (PoE++), 7-8 is 802.3bt type 4 (PoE+++).
"""

from __future__ import annotations

import re
from dataclasses import asdict, dataclass

_CLASS = re.compile(r"(?i)\bclass\s*(\d)\b")
_FLOAT = re.compile(r"^-?\d+\.\d+$")


def tier_for_class(pd_class: int | None) -> str | None:
    if pd_class is None:
        return None
    if pd_class <= 3:
        return "poe"
    if pd_class == 4:
        return "poe+"
    if pd_class <= 6:
        return "poe++"
    return "poe+++"


@dataclass(frozen=True, slots=True)
class PoePort:
    port: int
    enabled: bool
    delivering: bool
    pd_class: int | None = None
    power_w: float = 0.0
    voltage_v: float | None = None
    current_ma: float | None = None
    mode: str | None = None

    @property
    def tier(self) -> str:
        if not self.delivering:
            return "off"
        return tier_for_class(self.pd_class) or "poe"

    def to_dict(self) -> dict:
        return {**asdict(self), "tier": self.tier}


def parse_poe_show(text: str) -> dict[int, PoePort]:
    ports: dict[int, PoePort] = {}
    for line in text.splitlines():
        tokens = line.split()
        if len(tokens) < 4 or not tokens[0].isdigit():
            continue
        lowered = [t.lower() for t in tokens]
        if "on" not in lowered and "off" not in lowered:
            continue

        port = int(tokens[0])
        op_mode = lowered[1]
        powered = "on" in lowered
        m = _CLASS.search(line)
        pd_class = int(m.group(1)) if m else None
        mode = next((t for t in tokens if t.lower().startswith(("dot3", "passive", "pasv"))), None)

        floats = [float(t) for t in tokens if _FLOAT.match(t)]
        power = voltage = current = None
        if len(floats) >= 3:
            power, voltage, current = floats[-3:]
        elif floats:
            power = floats[0]

        ports[port] = PoePort(
            port=port,
            enabled=op_mode not in ("off", "disabled"),
            delivering=powered and (power is None or power > 0),
            pd_class=pd_class,
            power_w=power or 0.0,
            voltage_v=voltage,
            current_ma=current,
            mode=mode,
        )
    return ports
