"""The controller connection behind the workspace: one active client, an optional candidate and cached topology."""

from __future__ import annotations

import asyncio
import re
import time
from datetime import UTC, datetime

from .config import DEMO_HOSTS
from .controller import (
    ControllerClient,
    ControllerError,
    certificate_pin,
    clean_name,
    controller_access,
    controller_origin,
    match_switch,
    port_metadata,
    response_records,
    validate_api_key,
    validate_controller_kind,
)
from .controller_official import ConsoleClient, IntegrationClient, RequestLimit

STALE_AFTER = 45.0
_SITE = re.compile(r"^[A-Za-z0-9_-]{1,64}$")


class ControllerManager:
    def __init__(self, *, client_factory=ControllerClient, clock=time.monotonic) -> None:
        self._factory, self._clock = client_factory, clock
        self._client: ControllerClient | None = None
        self._pending: ControllerClient | None = None
        self._pending_sites: list[dict] = []
        self._lock = asyncio.Lock()
        self._sites: list[dict] = []
        self._site: str | None = None
        self._ports: dict[int, dict] = {}
        self._switch_host: str | None = None
        self._switch_name: str | None = None
        self._last_success: float | None = None
        self._updated_at: str | None = None
        self._error: dict | None = None
        # Discovery requests share one limit, so a new request or API key cannot bypass a Retry-After.
        self._console_limit = RequestLimit()

    def status(self) -> dict:
        stale = self._last_success is not None and self._clock() - self._last_success > STALE_AFTER
        client = self._client or self._pending
        pending_choice = ({"url": self._pending.url,
                           "controller_kind": self._pending.kind,
                           "auth_method": self._pending.auth_method,
                           "console_id": self._pending.console_id,
                           "capabilities": {"client_mapping": self._pending.auth_method == "password",
                                            "port_metadata": True},
                           "sites": self._pending_sites}
                          if self._pending is not None and self._pending_sites else None)
        return {"connected": bool(self._client and self._client.authenticated and self._last_success is not None
                                  and not self._error and not stale),
                "configured": client is not None, "url": client.url if client else None,
                "supported_auth_methods": {"self_hosted": ["password"], "unifi_os": ["password", "api_key"],
                                           "cloud_connector": ["api_key"]},
                "console_discovery_supported": True,
                "controller_kind": client.kind if client else None,
                "auth_method": client.auth_method if client else None,
                "console_id": client.console_id if client else None,
                "capabilities": {"client_mapping": bool(client and client.auth_method == "password"),
                                 "port_metadata": bool(client)},
                "site": self._site, "sites": self._sites, "switch_name": self._switch_name,
                "pending_site_choice": pending_choice,
                "updated_at": self._updated_at, "stale": stale or bool(self._error), "error": self._error,
                "mapped_ports": len(self._ports) if not stale and not self._error else 0}

    async def discover_consoles(self, api_key: str) -> dict:
        async with self._lock:
            client = ConsoleClient(validate_api_key(api_key), clock=self._clock, limit=self._console_limit)
            try:
                return {"consoles": await asyncio.to_thread(client.consoles)}
            finally:
                client.clear()

    def can_save_login(self, switch_host: str | None) -> bool:
        client = self._client
        return bool(switch_host and switch_host == self._switch_host and self._site
                    and not self._pending_sites and client
                    and (client.api_key if client.auth_method == "api_key" else client.username and client.password)
                    and self.status()["connected"])

    async def login_for_saving(self, switch_host: str | None) -> dict:
        async with self._lock:
            if not self.can_save_login(switch_host):
                raise ControllerError("controller_login_required",
                                      "Connect your controller before enabling automatic reconnect.", 409)
            client = self._client
            if client.auth_method == "api_key":
                return {"url": client.url, "auth_method": "api_key", "api_key": client.api_key,
                        "console_id": client.console_id, "site": self._site,
                        "certificate_sha256": client.pin, "controller_kind": client.kind}
            return {"url": client.url, "username": client.username, "password": client.password,
                    "site": self._site, "certificate_sha256": client.pin, "controller_kind": client.kind}

    async def connect(self, *, url: str, username: str = "", password: str = "", switch_host: str | None = None,
                      site: str | None = None, certificate_sha256: str | None = None,
                      verification_code: str | None = None, controller_kind: str = "self_hosted",
                      auth_method: str = "password", api_key: str = "", console_id: str | None = None) -> dict:
        kind = validate_controller_kind(controller_kind)
        origin, pin = controller_origin(url), certificate_pin(certificate_sha256)
        console_id = controller_access(kind, auth_method, origin, console_id)
        if (auth_method == "api_key" and (username or password or verification_code)
                or auth_method == "password" and api_key):
            raise ControllerError("invalid_auth_method", "Send only the credentials for the selected sign-in method.")
        if kind == "cloud_connector" and pin is not None:
            raise ControllerError("invalid_certificate",
                                  "The UniFi cloud connection requires a publicly trusted certificate.")
        verification_code = verification_code.strip() if verification_code else None
        if verification_code and not re.fullmatch(r"[0-9]{6,8}", verification_code):
            raise ControllerClient.verification_error()
        if site and not _SITE.fullmatch(site):
            raise ControllerError("invalid_site", "Choose a valid controller site.")
        async with self._lock:
            previous = next((saved for saved in (self._pending, self._client)
                             if saved is not None and saved.url == origin and saved.kind == kind
                             and saved.auth_method == auth_method and saved.console_id == console_id), None)
            if not username and not password and previous and previous.url == origin:
                username, password = previous.username, previous.password
            if pin is None and previous and previous.url == origin:
                pin = previous.pin
            if auth_method == "api_key":
                api_key = validate_api_key(api_key or (previous.api_key if previous else ""))
                same_identity = previous is not None and previous.api_key == api_key and previous.pin == pin
                client = (previous if same_identity else IntegrationClient(
                    origin, api_key, pin, kind=kind, console_id=console_id))
            else:
                same_identity = (previous is not None and previous.username == username
                                 and previous.password == password and previous.pin == pin)
                client = (previous if same_identity else self._factory(origin, username, password, pin, kind=kind))
            before = (self._client, self._site, self._switch_host, self._switch_name, self._ports,
                      self._sites, self._last_success, self._updated_at, self._error)
            if self._pending is not None and self._pending not in (client, self._client):
                self._pending.clear()
            if self._pending is not client:
                self._pending_sites = []
            self._pending = client
            try:
                if not client.authenticated:
                    if verification_code:
                        await asyncio.to_thread(client.login, verification_code)
                    else:
                        await asyncio.to_thread(client.login)
                records = response_records(await asyncio.to_thread(client.get, "/api/self/sites"))
                sites = [{"id": row["name"], "name": clean_name(row.get("desc")) or row["name"]}
                         for row in records if isinstance(row.get("name"), str) and _SITE.fullmatch(row["name"])]
                if not sites:
                    raise ControllerError("no_sites", "This controller account has no accessible Network sites.", 403)
                if not site and len(sites) == 1:
                    site = sites[0]["id"]
                if not site:
                    self._pending_sites = sites
                    if self._client is None:
                        self._sites = sites
                        self._error = {"code": "site_required", "message": "Choose the site containing your switch."}
                    return self.status()
                if site not in {item["id"] for item in sites}:
                    self._pending_sites = sites
                    raise ControllerError("site_not_found", "Choose a site available to this controller account.", 404)
                topology = await self._read_topology(client, site, switch_host)
                self._client, self._pending = client, None
                self._pending_sites = []
                self._sites, self._site = sites, site
                self._switch_host = switch_host
                (self._ports, self._switch_name,
                 self._last_success, self._updated_at, self._error) = topology
                if before[0] is not None and before[0] is not client:
                    before[0].clear()
                return self.status()
            except ControllerError as exc:
                self._pending = client
                (self._client, self._site, self._switch_host, self._switch_name, self._ports,
                 self._sites, self._last_success, self._updated_at, self._error) = before
                if (self._client is None or self._client is client
                        and (site == before[1] or not client.authenticated)):
                    self._error = exc.detail()
                raise

    async def refresh(self, switch_host: str | None) -> dict:
        async with self._lock:
            if self._client is not None and self._site is not None:
                try:
                    await self._refresh(switch_host)
                except ControllerError as exc:
                    self._ports = {}
                    self._error = exc.detail()
            return self.status()

    async def _refresh(self, switch_host: str | None) -> None:
        if switch_host != self._switch_host:
            self._ports = {}
            self._switch_name = None
        self._switch_host = switch_host
        (self._ports, self._switch_name,
         self._last_success, self._updated_at, self._error) = await self._read_topology(
             self._client, self._site, switch_host)

    async def _read_topology(self, client, site, switch_host):
        if not switch_host or switch_host.lower() in DEMO_HOSTS:
            return ({}, None, None, None,
                    {"code": "switch_required", "message": "Connect your switch to match controller devices."})
        if client.auth_method == "api_key":
            ports, name = await asyncio.to_thread(client.topology, site, switch_host)
            return ports, name, self._clock(), datetime.now(UTC).isoformat(), None
        devices = response_records(await asyncio.to_thread(client.get, f"/api/s/{site}/stat/device"))
        clients = response_records(await asyncio.to_thread(client.get, f"/api/s/{site}/stat/sta"))
        switch = await asyncio.to_thread(match_switch, devices, switch_host)
        return (port_metadata(switch, devices, clients),
                clean_name(switch.get("name")), self._clock(), datetime.now(UTC).isoformat(), None)

    def enrich_ports(self, ports: list[dict], switch_host: str | None) -> list[dict]:
        status = self.status()
        if not status["connected"] or switch_host != self._switch_host:
            return ports
        return [{**port, **self._ports.get(port.get("port"), {})} for port in ports]

    async def disconnect(self) -> dict:
        async with self._lock:
            for client in (self._client, self._pending):
                if client is not None:
                    client.clear()
            self._client = self._pending = None
            self._pending_sites = []
            self._site = self._switch_host = self._switch_name = None
            self._sites, self._ports = [], {}
            self._last_success = self._updated_at = self._error = None
            return self.status()

    async def cancel(self) -> dict:
        async with self._lock:
            if self._pending is not None and self._pending is not self._client:
                self._pending.clear()
            self._pending = None
            self._pending_sites = []
            if self._client is None:
                self._sites = []
                self._error = None
            return self.status()
