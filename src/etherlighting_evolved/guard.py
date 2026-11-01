"""Keeps other websites from using the API through the browser.

A page on another site can send requests to http://localhost:8787 from the user's browser. Two checks stop that:

- Requests that change something (anything but GET, HEAD and OPTIONS) and WebSocket connections are refused when
  their Origin header names a different site, unless that origin is listed in `cors_origins`.
- When the server only listens on this machine, requests are refused unless their Host header is a local name.
  This blocks DNS rebinding, where a site makes its own hostname resolve to 127.0.0.1.

Requests without an Origin header (curl, scripts, eectl) are not affected.
"""

from __future__ import annotations

import json
from urllib.parse import urlsplit

LOCAL_HOSTS = {"localhost", "127.0.0.1", "::1"}
LOOPBACK_BINDS = {"127.0.0.1", "localhost", "::1"}
SAFE_METHODS = {"GET", "HEAD", "OPTIONS"}


def hostname(host_header: str) -> str:
    """The name part of a Host header, without the port: "127.0.0.1:8787" -> "127.0.0.1", "[::1]:80" -> "::1"."""
    host = host_header.strip().lower()
    if host.startswith("["):
        return host[1:].split("]", 1)[0]
    return host.rsplit(":", 1)[0] if host.count(":") == 1 else host


class RequestGuard:
    def __init__(self, app, *, allowed_origins: list[str], local_only: bool) -> None:
        self.app = app
        self.allowed_origins = {o.rstrip("/").lower() for o in allowed_origins}
        self.local_only = local_only

    async def __call__(self, scope, receive, send) -> None:
        if scope["type"] not in ("http", "websocket"):
            await self.app(scope, receive, send)
            return
        headers = {k.decode("latin-1").lower(): v.decode("latin-1") for k, v in scope.get("headers", [])}
        host = headers.get("host", "")
        if self.local_only and hostname(host) not in LOCAL_HOSTS:
            await self._refuse(scope, receive, send, "requests must use localhost or 127.0.0.1")
            return
        origin = headers.get("origin")
        changes = scope["type"] == "websocket" or scope.get("method", "GET") not in SAFE_METHODS
        if origin and changes and not self._origin_allowed(origin, host):
            await self._refuse(scope, receive, send, "requests from other websites are not allowed")
            return
        await self.app(scope, receive, send)

    def _origin_allowed(self, origin: str, host: str) -> bool:
        origin = origin.rstrip("/").lower()
        if "*" in self.allowed_origins or origin in self.allowed_origins:
            return True
        return urlsplit(origin).netloc == host.lower()

    @staticmethod
    async def _refuse(scope, receive, send, message: str) -> None:
        if scope["type"] == "websocket":
            await receive()  # the websocket.connect event
            await send({"type": "websocket.close", "code": 4403})
            return
        body = json.dumps({"detail": {"code": "forbidden", "message": message}}).encode()
        await send({"type": "http.response.start", "status": 403,
                    "headers": [(b"content-type", b"application/json"), (b"content-length", str(len(body)).encode())]})
        await send({"type": "http.response.body", "body": body})
