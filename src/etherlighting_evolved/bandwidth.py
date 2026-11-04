"""Measure directional uplink throughput from the existing port-counter samples."""

from __future__ import annotations

import math
import time
from collections.abc import Iterable

from .links import PortLink


class UplinkBandwidth:
    INTERVAL = 5.0
    STALE_AFTER = 10.0
    HISTORY_SECONDS = 60.0

    def __init__(self, physical_ports: Iterable[int]) -> None:
        self._physical_ports = frozenset(physical_ports)
        self._sequence = 0
        self.reset("waiting")

    def reset(self, status: str = "unavailable") -> None:
        self._recent: list[tuple[float, int, int]] = []
        self._status = status
        self._ports: tuple[int, ...] = ()
        self._baseline: tuple[float, dict[int, tuple[int, int]]] | None = None
        self._latest: tuple[float, dict[int, tuple[int, int]]] | None = None
        self._previous: dict[int, tuple[int, int]] = {}
        self._last_poll_at: float | None = None
        self._sampled_at: float | None = None
        self._measurement: dict | None = None
        self._generation: object = None
        self._generation_known = False

    def set_physical_ports(self, ports: Iterable[int]) -> None:
        ports = frozenset(ports)
        if ports != self._physical_ports:
            self._physical_ports = ports
            self.reset("waiting")

    def update(self, links: dict[int, PortLink], *, now: float | None = None, generation: object = None) -> None:
        now = time.monotonic() if now is None else now
        if not math.isfinite(now):
            raise ValueError("sample time must be finite")
        if self._generation_known and generation is not self._generation:
            self.reset("waiting")
        self._generation, self._generation_known = generation, True

        physical = {port: link for port, link in links.items() if port in self._physical_ports}
        uplinks = {port: link for port, link in physical.items()
                   if link.uplink and link.admin_up and link.link_up}
        ports = tuple(sorted(uplinks))
        valid = bool(physical) and all(link.counters_valid and all(type(value) is int and value >= 0
                                      for value in (link.rx_bytes, link.tx_bytes)) for link in uplinks.values())
        if not valid or not ports:
            self.reset("unavailable" if not valid else "no_uplink")
            self._ports = ports
            self._last_poll_at = now
            self._generation, self._generation_known = generation, True
            return

        counters = {port: (link.rx_bytes, link.tx_bytes) for port, link in uplinks.items()}
        interrupted = (self._last_poll_at is not None
                       and (now <= self._last_poll_at or now - self._last_poll_at > self.STALE_AFTER))
        reset_counter = any(port in self._previous and any(value < previous
                            for value, previous in zip(values, self._previous[port], strict=True))
                            for port, values in counters.items())
        if ports != self._ports or interrupted or reset_counter:
            self._recent.clear()
            self._baseline = self._measurement = None
            self._sampled_at = None
            self._status = "waiting"
        self._ports, self._previous, self._last_poll_at = ports, counters, now
        self._latest = now, counters
        if self._baseline is None:
            self._baseline = now, counters
            self._status = "waiting"

    def publish(self, *, now: float | None = None, connected: bool = True) -> bool:
        """Publish the latest measured interval when the engine's refresh deadline arrives."""
        now = time.monotonic() if now is None else now
        if not math.isfinite(now):
            raise ValueError("publication time must be finite")
        if not connected or self._baseline is None or self._latest is None:
            return False
        captured, counters = self._latest
        started, baseline = self._baseline
        elapsed = captured - started
        if elapsed <= 0 or now < captured or now - captured > self.STALE_AFTER:
            return False

        received = sum(values[0] - baseline[port][0] for port, values in counters.items())
        transmitted = sum(values[1] - baseline[port][1] for port, values in counters.items())
        self._measurement = {"upload_bps": transmitted * 8 / elapsed,
                             "download_bps": received * 8 / elapsed,
                             "sample_period_ms": elapsed * 1000}
        self._recent = [sample for sample in self._recent if captured - sample[0] < self.HISTORY_SECONDS]
        self._recent.append((captured, round(self._measurement["upload_bps"]),
                             round(self._measurement["download_bps"])))
        # Offsets are relative to this measurement, so all frames can reuse the same series.
        self._measurement["history"] = [[round((t - captured) * 1000), up, down] for t, up, down in self._recent]
        self._baseline, self._sampled_at = self._latest, captured
        self._sequence += 1
        self._status = "ready"
        return True

    def snapshot(self, *, now: float | None = None, connected: bool = True) -> dict:
        now = time.monotonic() if now is None else now
        status = self._status if connected else "unavailable"
        age = None if self._sampled_at is None else max(0.0, now - self._sampled_at)
        if connected and age is not None and (now < self._sampled_at or age > self.STALE_AFTER):
            status = "stale"
        if connected and self._last_poll_at is not None and now - self._last_poll_at > self.STALE_AFTER:
            status = "stale" if self._measurement is not None else "unavailable"
        measurement = self._measurement if status == "ready" else None
        return {"status": status, "ports": list(self._ports) if connected else [],
                "upload_bps": None if measurement is None else measurement["upload_bps"],
                "download_bps": None if measurement is None else measurement["download_bps"],
                "history": measurement["history"] if measurement else [],
                "sample_period_ms": None if measurement is None else round(measurement["sample_period_ms"], 1),
                "sample_age_ms": None if age is None or not connected else round(age * 1000, 1),
                "sequence": self._sequence, "refresh_interval_ms": round(self.INTERVAL * 1000)}
