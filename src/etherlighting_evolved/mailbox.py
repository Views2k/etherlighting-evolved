"""What's waiting to be sent to one WebSocket client."""

from __future__ import annotations

import asyncio
from collections import deque


class Mailbox:
    """Messages waiting to be sent to one client.

    Only the newest frame and the newest state are kept: a new one replaces an unsent one of the same kind, so a
    slow client skips old frames instead of falling further behind. Other messages (acks, errors, session
    changes) are kept in order and sent first.
    """

    LATEST_ONLY = ("state", "frame")

    def __init__(self, limit: int = 256) -> None:
        self._messages: deque[dict] = deque(maxlen=limit)
        self._latest: dict[str, dict] = {}
        self._ready = asyncio.Event()

    def put(self, message: dict) -> None:
        kind = message.get("type")
        if kind in self.LATEST_ONLY:
            self._latest[kind] = message
        else:
            self._messages.append(message)
        self._ready.set()

    async def get(self) -> dict:
        while True:
            if self._messages:
                return self._messages.popleft()
            for kind in self.LATEST_ONLY:
                if kind in self._latest:
                    return self._latest.pop(kind)
            self._ready.clear()
            await self._ready.wait()

    def __len__(self) -> int:
        return len(self._messages) + len(self._latest)
