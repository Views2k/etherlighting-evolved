"""What the switch says about each port, read from `swctrl port show`.

A typical table (the leading "U" marks the uplink):

                             Packets                  Bytes
    Port  Link  Rate    Receive   Transmit     Receive     Transmit   state    STP     1x Ctrl Mode  1x Status    Anomaly      LAG
    ----  ---- ------ ---------- ---------- ------------ ------------ ----- ---------- ------------ ------------ ------------ --------
       1  U/U  10000F   94178966   87651127  17674367305  33333228102     J forwarding unknown      disabled     0x0
     U 5  U/U  10000F   57404777  153980064  76376354767  64803791643     J forwarding unknown      disabled     0x0
       9  U/D      0H          0          0            0            0  N  J disabled   unknown                   0x0

Link is admin/operational state: U/U has a link, U/D is enabled with nothing plugged in, D/D is disabled.
"""

from __future__ import annotations

import re
import time
from dataclasses import asdict, dataclass

_LINK = re.compile(r"^[UD]/[UD]$")
_RATE = re.compile(r"^(\d+)([FH])$")
_HEX = re.compile(r"^0x[0-9a-fA-F]+$")

STP_STATES = ("forwarding", "blocking", "learning", "listening", "discarding", "disabled", "broken")


@dataclass(frozen=True, slots=True)
class PortLink:
    port: int
    admin_up: bool
    link_up: bool
    speed_mbps: int
    full_duplex: bool
    rx_packets: int = 0
    tx_packets: int = 0
    rx_bytes: int = 0
    tx_bytes: int = 0
    uplink: bool = False
    stp_state: str | None = None
    anomaly: int = 0
    lag: str | None = None
    counters_valid: bool = True

    def to_dict(self) -> dict:
        data = asdict(self)
        data["anomaly"] = hex(self.anomaly)
        return data


def parse_port_show(text: str) -> dict[int, PortLink]:
    ports: dict[int, PortLink] = {}
    for line in text.splitlines():
        tokens = line.split()
        start = next(
            (i for i in range(len(tokens) - 1) if tokens[i].isdigit() and _LINK.match(tokens[i + 1])),
            None,
        )
        if start is None:
            continue

        port = int(tokens[start])
        admin, oper = tokens[start + 1].split("/")
        rest = tokens[start + 2 :]

        speed, full = 0, False
        if rest and (m := _RATE.match(rest[0])):
            speed, full = int(m.group(1)), m.group(2) == "F"
            rest = rest[1:]

        counters = []
        while rest and rest[0].isdigit() and len(counters) < 4:
            counters.append(int(rest.pop(0)))
        counters_valid = len(counters) == 4
        counters += [0] * (4 - len(counters))

        stp = next((tok for tok in rest if tok in STP_STATES), None)
        anomaly_at = next((i for i, tok in enumerate(rest) if _HEX.match(tok)), None)
        anomaly = int(rest[anomaly_at], 16) if anomaly_at is not None else 0
        lag = rest[anomaly_at + 1] if anomaly_at is not None and anomaly_at + 1 < len(rest) else None

        ports[port] = PortLink(
            port=port,
            admin_up=admin == "U",
            link_up=oper == "U",
            speed_mbps=speed if oper == "U" else 0,
            full_duplex=full,
            rx_packets=counters[0],
            tx_packets=counters[1],
            rx_bytes=counters[2],
            tx_bytes=counters[3],
            uplink="U" in tokens[:start],
            stp_state=stp,
            anomaly=anomaly,
            lag=lag,
            counters_valid=counters_valid,
        )
    return ports


class TrafficMeter:
    """Turns cumulative byte counters into bits per second (receive + transmit) per port."""

    def __init__(self) -> None:
        self._previous: dict[int, tuple[float, int]] = {}
        self.valid: set[int] = set()

    def update(self, links: dict[int, PortLink], now: float | None = None) -> dict[int, float]:
        now = time.monotonic() if now is None else now
        rates = {}
        self.valid = set()
        self._previous = {port: value for port, value in self._previous.items() if port in links}
        for port, link in links.items():
            total = link.rx_bytes + link.tx_bytes
            before = self._previous.get(port)
            if not link.counters_valid:
                self._previous.pop(port, None)
                continue
            if before and now > before[0] and total >= before[1]:
                rates[port] = (total - before[1]) * 8 / (now - before[0])
                self.valid.add(port)
            else:
                rates[port] = 0.0
            self._previous[port] = (now, total)
        return rates
