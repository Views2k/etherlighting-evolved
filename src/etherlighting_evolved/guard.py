"""Keeps other websites from using the API through the browser, and limits the size of request bodies.

A page on another site can send requests to http://localhost:8787 from the user's browser. Three checks stop that:

- Requests that change something (anything but GET, HEAD and OPTIONS) and WebSocket connections are refused when
  their Origin header names a different site, unless that origin is listed in `cors_origins`.
- Requests to /api/ that the browser marks as coming from another site (the Sec-Fetch-Site header) are refused
  unless their Origin is allowed. Browsers send no Origin with requests such as <img src>, and some GET routes
  have side effects. Pages outside /api/, such as /docs, still open from links on other sites.
- When the server only listens on this machine, requests are refused unless their Host header is a local name.
  This blocks DNS rebinding, where a site makes its own hostname resolve to 127.0.0.1.

Requests without these headers (curl, scripts, eectl, the desktop app) are not affected.

Request bodies larger than MAX_BODY are refused with 413 before the API reads them.
"""

from __future__ import annotations

import json
from urllib.parse import urlsplit

LOCAL_HOSTS = {"localhost", "127.0.0.1", "::1"}
LOOPBACK_BINDS = {"127.0.0.1", "localhost", "::1"}
SAFE_METHODS = {"GET", "HEAD", "OPTIONS"}
SAME_SITE_FETCHES = {"same-origin", "none"}
MAX_BODY = 1024 * 1024


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
        allowed = bool(origin) and self._origin_allowed(origin, host)
        changes = scope["type"] == "websocket" or scope.get("method", "GET") not in SAFE_METHODS
        cross_site = headers.get("sec-fetch-site", "none") not in SAME_SITE_FETCHES
        if ((origin and changes) or (cross_site and scope["path"].startswith("/api/"))) and not allowed:
            await self._refuse(scope, receive, send, "requests from other websites are not allowed")
            return
        if scope["type"] == "http":
            if int(headers.get("content-length") or 0) > MAX_BODY:
                await self._refuse(scope, receive, send, "The request is too large.", 413, "too_large")
                return
            if "transfer-encoding" in headers:
                # A chunked body has no declared length, so it is read here, within the limit, and replayed.
                body = await self._read_body(receive)
                if body is None:
                    await self._refuse(scope, receive, send, "The request is too large.", 413, "too_large")
                    return
                receive = _replay(body, receive)
        await self.app(scope, receive, send)

    def _origin_allowed(self, origin: str, host: str) -> bool:
        origin = origin.rstrip("/").lower()
        if "*" in self.allowed_origins or origin in self.allowed_origins:
            return True
        return urlsplit(origin).netloc == host.lower()

    @staticmethod
    async def _read_body(receive) -> bytes | None:
        """The whole request body, or None when it exceeds MAX_BODY or the client disconnects first."""
        chunks, size = [], 0
        while True:
            message = await receive()
            if message["type"] != "http.request":
                return None
            chunks.append(message.get("body", b""))
            size += len(chunks[-1])
            if size > MAX_BODY:
                return None
            if not message.get("more_body", False):
                return b"".join(chunks)

    @staticmethod
    async def _refuse(scope, receive, send, message: str, status: int = 403, code: str = "forbidden") -> None:
        if scope["type"] == "websocket":
            await receive()  # the websocket.connect event
            await send({"type": "websocket.close", "code": 4403})
            return
        body = json.dumps({"detail": {"code": code, "message": message}}).encode()
        await send({"type": "http.response.start", "status": status,
                    "headers": [(b"content-type", b"application/json"), (b"content-length", str(len(body)).encode())]})
        await send({"type": "http.response.body", "body": body})


def _replay(body: bytes, receive):
    """A receive callable that returns the body already read once, then defers to the server."""
    pending = [{"type": "http.request", "body": body, "more_body": False}]

    async def replay():
        return pending.pop() if pending else await receive()

    return replay
