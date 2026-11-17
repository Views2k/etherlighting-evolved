"""Identifying one port, which flashes white and blue for ten seconds so it can be found on the switch.

An identification stays outside the saved scene. A new one can start only after the current one stops.
"""

from __future__ import annotations

import asyncio
import secrets
from collections.abc import Callable

from . import timing
from .color import Color


class Identification:
    def __init__(self) -> None:
        self._current: dict | None = None
        self.base: tuple[Color, float] | None = None  # what the Ethernet port shows without the flash
        self.timer: asyncio.TimerHandle | None = None

    def start(self, port: int, expire: Callable[[str], None]) -> None:
        """Flash `port`; `expire` receives the identification's token when its ten seconds end."""
        if self._current is not None:
            raise ValueError("Another port is being identified. Stop it before starting another.")
        loop = asyncio.get_running_loop()
        token = secrets.token_hex(16)
        self._current = {"id": token, "port": port, "started": timing.monotonic(),
                         "expires": timing.monotonic() + 10.0}
        self.timer = loop.call_later(10.0, expire, token)

    def stop(self, token: str | None = None) -> int | None:
        """End the identification with this token, or any one without a token, and return its port."""
        current = self._current
        if current is None:
            return None
        if token is not None and token != current["id"]:
            raise ValueError("The identification changed. Refresh before stopping it.")
        self._current = None
        self.base = None
        if self.timer is not None:
            self.timer.cancel()
            self.timer = None
        return current["port"]

    def state(self) -> dict | None:
        current = self._current
        if current is None:
            return None
        return {"id": current["id"], "port": current["port"],
                "seconds_remaining": max(0, round(current["expires"] - timing.monotonic(), 1))}

    def flashing(self, port: int, output_running: bool) -> bool:
        current = self._current
        return bool(current is not None and current["port"] == port and output_running
                    and timing.monotonic() < current["expires"])

    def color(self) -> Color:
        phase = (timing.monotonic() - self._current["started"]) % 1.0
        return Color(255, 255, 255) if phase < 0.5 else Color(0, 128, 255)
