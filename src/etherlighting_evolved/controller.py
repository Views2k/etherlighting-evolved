"""Read-only names and topology from a configured UniFi Network controller."""

from __future__ import annotations

import asyncio
import hashlib
import hmac
import http.client
import ipaddress
import json
import re
import socket
import ssl
import time
from collections import deque
from datetime import UTC, datetime
from http.cookies import CookieError, SimpleCookie
from urllib.parse import urlsplit

MAX_RESPONSE = 4 * 1024 * 1024
STALE_AFTER = 45.0
_SITE = re.compile(r"^[A-Za-z0-9_-]{1,64}$")
_MAC = re.compile(r"^[0-9a-f]{12}$")
CONTROLLER_KINDS = {"self_hosted", "unifi_os", "cloud_connector"}


class ControllerError(Exception):
    def __init__(self, code: str, message: str, status: int = 400, **details) -> None:
        super().__init__(message)
        self.code, self.message, self.status, self.details = code, message, status, details

    def detail(self) -> dict:
        return {"code": self.code, "message": self.message, **self.details}


def controller_origin(value: str) -> str:
    try:
        parsed = urlsplit(value.strip())
        port = parsed.port
        if (parsed.scheme != "https" or not parsed.hostname or parsed.username or parsed.password
                or parsed.path not in ("", "/") or parsed.query or parsed.fragment
                or any(ord(c) <= 32 for c in value) or (port is not None and not 1 <= port <= 65535)):
            raise ValueError
        host = parsed.hostname.encode("idna").decode("ascii").lower()
        if ":" in host:
            host = f"[{host}]"
        return f"https://{host}" + (f":{port}" if port is not None else "")
    except (AttributeError, UnicodeError, ValueError) as exc:
        raise ControllerError("invalid_url", "Enter the controller's HTTPS address, including its port.") from exc


def certificate_pin(value: str | None) -> str | None:
    if not value:
        return None
    pin = value.replace(":", "").lower()
    if not re.fullmatch(r"[0-9a-f]{64}", pin):
        raise ControllerError("invalid_certificate", "The certificate fingerprint is invalid.")
    return pin


def validate_controller_kind(value: str) -> str:
    if not isinstance(value, str) or value not in CONTROLLER_KINDS:
        raise ControllerError("invalid_controller_kind", "Choose the controller type.")
    return value


def controller_access(kind: str, method: str, origin: str, console_id: str | None) -> str | None:
    validate_controller_kind(kind)
    if (not isinstance(method, str) or method not in {"password", "api_key"}
            or kind == "self_hosted" and method != "password"):
        raise ControllerError("invalid_auth_method", "Choose a supported controller sign-in method.")
    if kind == "cloud_connector":
        if method != "api_key":
            raise ControllerError("invalid_auth_method", "Use a UniFi API key for the cloud connection.")
        if origin != "https://api.ui.com":
            raise ControllerError("invalid_cloud_origin", "Use the UniFi cloud connection address.")
        if not isinstance(console_id, str) or not re.fullmatch(r"[A-Za-z0-9_-]{1,128}(?::[0-9]{1,20})?", console_id):
            raise ControllerError("invalid_console_id", "Enter the console ID from UniFi Site Manager.")
    elif console_id not in (None, ""):
        raise ControllerError("invalid_console_id", "A console ID is only used for the cloud connection.")
    return console_id or None


def validate_api_key(value: str) -> str:
    if not value:
        raise ControllerError("api_key_required", "Enter your UniFi API key.")
    if not isinstance(value, str) or not re.fullmatch(r"[\x21-\x7e]{1,4096}", value):
        raise ControllerError("invalid_api_key", "Enter the API key exactly as it was created in UniFi.")
    return value


class ControllerClient:
    """Each request stays on one approved HTTPS origin, with a narrow endpoint allowlist."""

    def __init__(self, url: str, username: str, password: str, certificate_sha256: str | None = None,
                 *, kind: str = "self_hosted") -> None:
        self.url = controller_origin(url)
        self.kind = validate_controller_kind(kind)
        if self.kind == "cloud_connector":
            raise ControllerError("invalid_auth_method", "Use a UniFi API key for the cloud connection.")
        self.auth_method = "password"
        self.console_id = None
        parsed = urlsplit(self.url)
        self.host, self.port = parsed.hostname, parsed.port or 443
        self.username, self.password = username, password
        self.pin = certificate_pin(certificate_sha256)
        self.cookies: dict[str, str] = {}
        self.authenticated = False
        self.two_factor_required = False
        self.csrf_token: str | None = None

    def _connection(self, *, verified: bool = True):
        context = ssl.create_default_context() if verified else ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        context.minimum_version = ssl.TLSVersion.TLSv1_2
        if not verified:
            context.check_hostname = False
            context.verify_mode = ssl.CERT_NONE
        return http.client.HTTPSConnection(self.host, self.port, timeout=8, context=context)

    @staticmethod
    def _fingerprint(connection) -> str:
        return hashlib.sha256(connection.sock.getpeercert(binary_form=True)).hexdigest()

    def _connect(self):
        connection = self._connection(verified=self.pin is None)
        try:
            connection.connect()
        except ssl.SSLCertVerificationError as exc:
            connection.close()
            probe = self._connection(verified=False)
            try:
                probe.connect()
                fingerprint = self._fingerprint(probe)
            finally:
                probe.close()
            raise ControllerError(
                "certificate_untrusted", "Verify this controller certificate before signing in.",
                certificate_sha256=fingerprint,
            ) from exc
        if self.pin and not hmac.compare_digest(self.pin, self._fingerprint(connection)):
            fingerprint = self._fingerprint(connection)
            connection.close()
            raise ControllerError(
                "certificate_changed", "The controller certificate changed. Verify it before signing in.",
                certificate_sha256=fingerprint,
            )
        return connection

    def request(self, method: str, path: str, payload: dict | None = None) -> dict:
        allowed = ((method, path) == ("POST", "/api/login")
                   or (method == "GET" and (path == "/api/self/sites"
                       or re.fullmatch(r"/api/s/[A-Za-z0-9_-]{1,64}/stat/(?:device|sta)", path))))
        if not allowed or (method == "GET" and payload is not None):
            raise ControllerError("invalid_request", "This controller request is not available.")
        connection = None
        is_login = path == "/api/login"
        verifying = is_login and bool(payload and payload.get("ubic_2fa_token"))
        wire_path = (("/api/auth/login" if is_login else "/proxy/network" + path)
                     if self.kind == "unifi_os" else path)
        try:
            connection = self._connect()
            headers = {"Accept": "application/json", "Accept-Encoding": "identity"}
            if self.cookies:
                headers["Cookie"] = "; ".join(f"{key}={value}" for key, value in self.cookies.items())
            if self.csrf_token:
                headers["X-CSRF-Token"] = self.csrf_token
            body = None
            if payload is not None:
                body = json.dumps(payload).encode("utf-8")
                headers["Content-Type"] = "application/json"
            connection.request(method, wire_path, body=body, headers=headers)
            response = connection.getresponse()
            if 300 <= response.status < 400:
                raise ControllerError("redirect_blocked", "Use the controller's direct HTTPS address.")
            if is_login and self.kind == "unifi_os" and response.status == 499:
                raise ControllerError("controller_local_account_required",
                                      "Use a local UniFi account with Network access.", 401)
            if not is_login:
                self._check_status(response.status)
            data = response.read(MAX_RESPONSE + 1)
            if len(data) > MAX_RESPONSE:
                raise ControllerError("response_too_large", "The controller response is too large.", 502)
            try:
                result = json.loads(data)
            except (ValueError, UnicodeError) as exc:
                self._check_status(response.status, verifying=verifying)
                raise ControllerError(
                    "invalid_response", "The controller returned an unexpected response.", 502) from exc
            if not isinstance(result, dict):
                self._check_status(response.status, verifying=verifying)
                raise ControllerError("invalid_response", "The controller returned an unexpected response.", 502)
            meta = result.get("meta")
            if is_login and isinstance(meta, dict) and meta.get("msg") == "api.err.Ubic2faTokenRequired":
                if self.kind == "unifi_os":
                    raise ControllerError("controller_local_account_required",
                                          "Use a local UniFi account with Network access.", 401)
                self.authenticated = False
                self.two_factor_required = True
                self._remember_response_cookies(response)
                challenge = result.get("data")
                token = challenge.get("mfa_cookie") if isinstance(challenge, dict) else None
                if isinstance(token, str) and 0 < len(token) <= 2048 and not any(ord(c) < 32 for c in token):
                    cookie = SimpleCookie()
                    cookie["UBIC_2FA"] = token
                    self._remember_cookie(cookie.output(header="").strip())
                raise self._verification_error() if verifying else self._two_factor_error()
            self._check_status(response.status, verifying=verifying)
            os_login = is_login and self.kind == "unifi_os"
            valid_result = ((isinstance(meta, dict) and meta.get("rc") == "ok")
                            or (os_login and meta is None and not result.get("errorCode")
                                and not result.get("error")))
            if not valid_result:
                if is_login:
                    self.authenticated = False
                    if verifying:
                        self.two_factor_required = True
                        raise self._verification_error()
                    raise ControllerError(
                        "authentication_failed", "Check the controller account and its Network access.", 401)
                raise ControllerError("controller_error", "The controller did not accept the request.", 502)
            self._remember_response_cookies(response)
            if os_login and not self.cookies:
                raise ControllerError("invalid_response", "The controller did not establish a login session.", 502)
            self._remember_csrf(response)
            return result
        except ControllerError:
            raise
        except (OSError, http.client.HTTPException) as exc:
            raise ControllerError("unreachable", "Could not reach the controller over HTTPS.", 503) from exc
        finally:
            if connection is not None:
                connection.close()

    @staticmethod
    def _two_factor_error() -> ControllerError:
        return ControllerError("two_factor_required", "Enter the verification code from your authenticator app.", 401)

    @staticmethod
    def _verification_error() -> ControllerError:
        return ControllerError("verification_failed", "The verification code was not accepted. Enter a new code.", 401)

    def _check_status(self, status: int, *, verifying: bool = False) -> None:
        if status in (401, 403) or (verifying and status == 400):
            self.authenticated = False
            if verifying:
                self.two_factor_required = True
                raise self._verification_error()
            raise ControllerError("authentication_failed", "Check the controller account and its Network access.", 401)
        if status == 429:
            raise ControllerError("rate_limited", "The controller is busy. Try again shortly.", 503)
        if status == 404:
            raise ControllerError("unsupported_controller", "Check the controller type and its direct HTTPS address.")
        if status != 200:
            raise ControllerError("controller_error", "The controller could not complete the request.", 502)

    def _remember_response_cookies(self, response) -> None:
        for key, value in response.getheaders():
            if key.lower() == "set-cookie":
                self._remember_cookie(value)

    def _remember_csrf(self, response) -> None:
        tokens = [value for key, value in response.getheaders() if key.lower() == "x-csrf-token"]
        if not tokens:
            return
        if (len(tokens) != 1 or not isinstance(tokens[0], str) or not 1 <= len(tokens[0]) <= 4096
                or any(ord(char) < 33 or ord(char) > 126 for char in tokens[0])):
            raise ControllerError("invalid_response", "The controller returned an invalid login session.", 502)
        self.csrf_token = tokens[0]

    def _remember_cookie(self, header: str) -> None:
        if len(header) > 8192:
            return
        cookie = SimpleCookie()
        try:
            cookie.load(header)
        except CookieError:
            return
        for name, morsel in cookie.items():
            domain = morsel["domain"].lstrip(".").lower()
            if domain and domain != self.host:
                continue
            value = morsel.coded_value
            if len(name) <= 128 and len(value) <= 2048 and not any(c in value for c in "\r\n"):
                if not morsel.value or morsel["max-age"] == "0":
                    self.cookies.pop(name, None)
                elif len(self.cookies) < 16 or name in self.cookies:
                    self.cookies[name] = value

    def login(self, verification_code: str | None = None) -> None:
        if not self.username or not self.password:
            raise ControllerError("credentials_required", "Enter your local UniFi controller username and password.")
        if self.two_factor_required and not verification_code:
            raise self._two_factor_error()
        if not self.two_factor_required:
            self.cookies.clear()
            self.csrf_token = None
        payload = {"username": self.username, "password": self.password,
                   "rememberMe" if self.kind == "unifi_os" else "remember": False}
        if verification_code and self.kind == "unifi_os":
            raise ControllerError("controller_local_account_required",
                                  "Use a local UniFi account with Network access.", 401)
        if verification_code:
            payload["ubic_2fa_token"] = verification_code
        self.authenticated = False
        self.request("POST", "/api/login", payload)
        self.authenticated = True
        self.two_factor_required = False

    def get(self, path: str) -> dict:
        if not self.authenticated:
            self.login()
        try:
            return self.request("GET", path)
        except ControllerError as exc:
            if exc.code != "authentication_failed":
                raise
            self.login()
            return self.request("GET", path)

    def clear(self) -> None:
        self.cookies.clear()
        self.csrf_token = None
        self.username = self.password = ""
        self.authenticated = False
        self.two_factor_required = False


def _records(response: dict) -> list[dict]:
    data = response.get("data")
    if not isinstance(data, list) or len(data) > 10000 or any(not isinstance(item, dict) for item in data):
        raise ControllerError("invalid_response", "The controller returned an unexpected response.", 502)
    return data


def _mac(value) -> str | None:
    if not isinstance(value, str):
        return None
    normalized = value.replace(":", "").replace("-", "").lower()
    return normalized if _MAC.fullmatch(normalized) else None


def _name(value) -> str | None:
    if not isinstance(value, str):
        return None
    value = " ".join(value.split())[:160]
    return value if value and not _mac(value) else None


def _port(value) -> int | None:
    if isinstance(value, bool):
        return None
    if isinstance(value, str) and value.isdecimal():
        value = int(value)
    return value if isinstance(value, int) and 1 <= value <= 256 else None


def _rows(value) -> list[dict]:
    return [item for item in value if isinstance(item, dict)] if isinstance(value, list) else []


def _host_addresses(host: str) -> set[str]:
    try:
        return {str(ipaddress.ip_address(host))}
    except ValueError:
        try:
            return {str(ipaddress.ip_address(item[4][0]))
                    for item in socket.getaddrinfo(host, None, type=socket.SOCK_STREAM)}
        except OSError as exc:
            raise ControllerError(
                "switch_not_found", "Could not match the connected switch in this site.", 409) from exc


def match_switch(devices: list[dict], switch_host: str) -> dict:
    addresses = _host_addresses(switch_host)
    matches = []
    for device in devices:
        try:
            address = str(ipaddress.ip_address(device.get("ip", "")))
        except ValueError:
            continue
        if address in addresses and _mac(device.get("mac")) and isinstance(device.get("port_table"), list):
            matches.append(device)
    if len(matches) != 1:
        raise ControllerError("switch_not_found", "Could not uniquely match the connected switch in this site.", 409)
    return matches[0]


def port_metadata(switch: dict, devices: list[dict], clients: list[dict]) -> dict[int, dict]:
    """Only attach names backed by an explicit topology port; never guess from list order."""
    target_mac = _mac(switch.get("mac"))
    if target_mac is None:
        return {}
    valid_ports = {_port(port.get("port_idx")) for port in _rows(switch.get("port_table"))}
    valid_ports.discard(None)
    metadata: dict[int, dict] = {}
    device_by_mac = {_mac(device.get("mac")): device for device in devices if _mac(device.get("mac"))}
    connected_devices: dict[int, dict[str, dict]] = {}

    def add_device(port, device):
        mac = _mac(device.get("mac"))
        if port in valid_ports and mac and mac != target_mac and device.get("state") == 1:
            connected_devices.setdefault(port, {})[mac] = device

    for entry in _rows(switch.get("downlink_table")):
        if device := device_by_mac.get(_mac(entry.get("mac"))):
            add_device(_port(entry.get("port_idx")), device)
    for device in devices:
        uplink = device.get("uplink")
        if isinstance(uplink, dict) and _mac(uplink.get("uplink_mac")) == target_mac:
            add_device(_port(uplink.get("uplink_remote_port")), device)

    # The switch's own uplink can identify its upstream device without naming a downstream client there.
    uplink = switch.get("uplink")
    if isinstance(uplink, dict) and (upstream := device_by_mac.get(_mac(uplink.get("uplink_mac")))):
        add_device(_port(uplink.get("port_idx")), upstream)

    grouped: dict[int, dict[str, dict]] = {}
    for client in clients:
        port, mac = _port(client.get("sw_port")), _mac(client.get("mac"))
        if (client.get("is_wired") is not True or _mac(client.get("sw_mac")) != target_mac
                or port not in valid_ports or not mac or client.get("is_downstream") is True):
            continue
        grouped.setdefault(port, {})[mac] = client

    for port, records in connected_devices.items():
        if len(records) == 1:
            record = next(iter(records.values()))
            name = _name(record.get("name"))
            if name:
                metadata[port] = {"name": name, "controller": {"source": "unifi", "kind": "device", "count": 1}}
        else:
            metadata[port] = {"name": f"{len(records)} devices",
                              "controller": {"source": "unifi", "kind": "multiple", "count": len(records)}}
    for port, records in grouped.items():
        if port in connected_devices:
            continue
        client_details = []
        for record in records.values():
            assigned_name = _name(record.get("name"))
            hostname = _name(record.get("hostname"))
            client_details.append({"name": assigned_name or hostname,
                                   "name_source": "assigned" if assigned_name else "hostname" if hostname else None})
        client_details.sort(key=lambda item: (item["name_source"] != "assigned", item["name"] is None,
                                              (item["name"] or "").casefold(), item["name"] or ""))
        details = {"source": "unifi", "kind": "client" if len(records) == 1 else "multiple",
                   "count": len(records), "clients": client_details[:64],
                   "clients_truncated": len(client_details) > 64}
        if len(records) == 1:
            name = client_details[0]["name"]
            if name:
                metadata[port] = {"name": name, "controller": details}
        else:
            metadata[port] = {"name": f"{len(records)} clients", "controller": details}
    for override in _rows(switch.get("port_overrides")):
        port, name = _port(override.get("port_idx")), _name(override.get("name"))
        if port in valid_ports and port not in metadata and name:
            metadata[port] = {"name": name, "controller": {"source": "unifi", "kind": "port_label", "count": 0}}
    return metadata


def port_diagnostics(switch: dict, clients: list[dict], *, observed_at: float) -> list[dict]:
    """Describe topology matches without retaining client identifiers or names."""
    target_mac = _mac(switch.get("mac"))
    if target_mac is None:
        return []
    port_rows = {_port(row.get("port_idx")): row for row in _rows(switch.get("port_table"))}
    port_rows.pop(None, None)
    labels = {_port(row.get("port_idx")): _name(row.get("name"))
              for row in _rows(switch.get("port_overrides"))}
    grouped: dict[int, dict[str, dict]] = {}
    for client in clients:
        port, mac = _port(client.get("sw_port")), _mac(client.get("mac"))
        if port in port_rows and mac and _mac(client.get("sw_mac")) == target_mac:
            grouped.setdefault(port, {})[mac] = client

    def number(value):
        return (value if isinstance(value, (int, float)) and not isinstance(value, bool)
                and 0 <= value <= 1e15 else None)

    def age(value):
        stamp = number(value)
        return max(0, round(observed_at - stamp)) if stamp is not None else None

    def flag(record, key):
        value = record.get(key)
        return value if isinstance(value, bool) else None

    flag_fields = ("is_wired", "wired", "is_wireless", "is_downstream", "is_direct", "direct",
                   "is_virtual", "virtual", "is_uplink")
    collection_fields = ("mac_table", "sta", "sta_table", "station_table", "clients", "client_table",
                         "connected_client", "connected_clients")

    def shape(value):
        if isinstance(value, list):
            return {"type": "list", "count": len(value),
                    "object_count": sum(isinstance(item, dict) for item in value)}
        kind = ("object" if isinstance(value, dict) else "boolean" if isinstance(value, bool)
                else "string" if isinstance(value, str) else "number" if isinstance(value, (int, float))
                else "null" if value is None else "other")
        return {"type": kind}

    known_fields = (*flag_fields,
                    "sw_depth", "sw_port", "sw_mac", "last_uplink_mac", "last_uplink_name",
                    "uplink_mac", "parent_mac", "parent_id", "uplink_device_id", "connection_type",
                    "connection_state", "hostname_source", "last_seen", "_last_seen_by_usw", "uptime")
    result = []
    for port, row in sorted(port_rows.items()):
        records = list(grouped.get(port, {}).values())[:64]

        def mac_matches(value, candidates=records):
            mac = _mac(value)
            return [index for index, client in enumerate(candidates) if mac and mac == _mac(client.get("mac"))]

        def name_matches(value, candidates=records):
            name = _name(value)
            return [index for index, client in enumerate(candidates) if name and any(
                name.casefold() == candidate.casefold() for key in ("name", "hostname")
                if (candidate := _name(client.get(key))) is not None)]

        neighbors = [entry for entry in _rows(switch.get("lldp_table"))
                     if _port(entry.get("local_port_idx")) == port]
        neighbors.extend(_rows(row.get("lldp_table")))
        result.append({
            "port": port,
            "port_table_row_indexes": [index for index, entry in enumerate(switch["port_table"])
                                       if isinstance(entry, dict) and _port(entry.get("port_idx")) == port],
            "port_collection_shapes": {key: shape(row[key]) for key in collection_fields if key in row},
            "switch_collection_shapes": {key: shape(switch[key]) for key in collection_fields if key in switch},
            "port_label_present": _name(row.get("name")) is not None,
            "configured_label_present": labels.get(port) is not None,
            "port_label_matches": name_matches(row.get("name")),
            "configured_label_matches": name_matches(labels.get(port)),
            "candidate_count": len(grouped.get(port, {})),
            "candidates_truncated": len(grouped.get(port, {})) > len(records),
            "mac_table_candidates": sorted({index for entry in _rows(row.get("mac_table"))
                                             for index in mac_matches(entry.get("mac"))}),
            "peer_identity_fields": [{"field": key, "candidates": mac_matches(row[key])}
                                     for key in ("client_mac", "peer_mac", "connected_mac", "mac") if key in row],
            "lldp": [{"chassis_candidates": mac_matches(entry.get("chassis_id")),
                      "name_fields": [{"field": key, "candidates": name_matches(entry[key])}
                                      for key in ("system_name", "sys_name", "name") if key in entry]}
                     for entry in neighbors[:16]],
            "candidates": [{
                "index": index,
                "named": _name(client.get("name")) is not None,
                "hostname_present": _name(client.get("hostname")) is not None,
                "included_by_mapping": client.get("is_wired") is True and client.get("is_downstream") is not True,
                "flags": {key: flag(client, key) for key in flag_fields if key in client},
                "sw_depth": number(client.get("sw_depth")),
                "last_seen_age_seconds": age(client.get("last_seen")),
                "switch_last_seen_age_seconds": age(client.get("_last_seen_by_usw")),
                "uptime_seconds": number(client.get("uptime")),
                "last_uplink_matches_switch": (_mac(client.get("last_uplink_mac")) == target_mac
                                               if _mac(client.get("last_uplink_mac")) else None),
                "topology_fields": [key for key in known_fields if key in client],
            } for index, client in enumerate(records)],
        })
    return result


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
        self._diagnostic_ports: list[dict] = []
        self._switch_host: str | None = None
        self._switch_name: str | None = None
        self._last_success: float | None = None
        self._updated_at: str | None = None
        self._error: dict | None = None
        self._console_requests = deque()
        self._console_next_due = 0.0

    def status(self) -> dict:
        stale = self._last_success is not None and self._clock() - self._last_success > STALE_AFTER
        client = self._client or self._pending
        pending_choice = ({"url": self._pending.url,
                           "controller_kind": getattr(self._pending, "kind", "self_hosted"),
                           "auth_method": getattr(self._pending, "auth_method", "password"),
                           "console_id": getattr(self._pending, "console_id", None),
                           "capabilities": {"client_mapping":
                                            getattr(self._pending, "auth_method", "password") == "password",
                                            "port_metadata": True},
                           "sites": self._pending_sites}
                          if self._pending is not None and self._pending_sites else None)
        return {"connected": bool(self._client and self._client.authenticated and self._last_success is not None
                                  and not self._error and not stale),
                "configured": client is not None, "url": client.url if client else None,
                "supported_auth_methods": {"self_hosted": ["password"], "unifi_os": ["password", "api_key"],
                                           "cloud_connector": ["api_key"]},
                "console_discovery_supported": True,
                "controller_kind": getattr(client, "kind", "self_hosted") if client else None,
                "auth_method": getattr(client, "auth_method", "password") if client else None,
                "console_id": getattr(client, "console_id", None),
                "capabilities": {"client_mapping": bool(client and
                                                       getattr(client, "auth_method", "password") == "password"),
                                 "port_metadata": bool(client)},
                "site": self._site, "sites": self._sites, "switch_name": self._switch_name,
                "pending_site_choice": pending_choice,
                "updated_at": self._updated_at, "stale": stale or bool(self._error), "error": self._error,
                "mapped_ports": len(self._ports) if not stale and not self._error else 0}

    def diagnostics(self, switch_host: str | None) -> dict:
        available = self.status()["connected"] and switch_host == self._switch_host
        return {"available": bool(available), "updated_at": self._updated_at if available else None,
                "ports": self._diagnostic_ports if available else []}

    async def discover_consoles(self, api_key: str) -> dict:
        from .controller_official import ConsoleClient
        async with self._lock:
            client = ConsoleClient(validate_api_key(api_key), clock=self._clock)
            client._requests = self._console_requests
            client._not_before = self._console_next_due
            try:
                return {"consoles": await asyncio.to_thread(client.consoles)}
            finally:
                self._console_next_due = max(self._console_next_due, client._not_before)
                client.clear()

    def can_save_login(self, switch_host: str | None) -> bool:
        client = self._client
        return bool(switch_host and switch_host == self._switch_host and self._site
                    and not self._pending_sites and client
                    and (getattr(client, "api_key", None) if getattr(client, "auth_method", "password") == "api_key"
                         else client.username and client.password)
                    and self.status()["connected"])

    async def login_for_saving(self, switch_host: str | None) -> dict:
        async with self._lock:
            if not self.can_save_login(switch_host):
                raise ControllerError("controller_login_required",
                                      "Connect your controller before enabling automatic reconnect.", 409)
            client = self._client
            if getattr(client, "auth_method", "password") == "api_key":
                return {"url": client.url, "auth_method": "api_key", "api_key": client.api_key,
                        "console_id": client.console_id, "site": self._site,
                        "certificate_sha256": client.pin, "controller_kind": client.kind}
            return {"url": client.url, "username": client.username, "password": client.password,
                    "site": self._site, "certificate_sha256": client.pin,
                    "controller_kind": getattr(client, "kind", "self_hosted")}

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
            raise ControllerClient._verification_error()
        if site and not _SITE.fullmatch(site):
            raise ControllerError("invalid_site", "Choose a valid controller site.")
        async with self._lock:
            previous = next((saved for saved in (self._pending, self._client)
                             if saved is not None and saved.url == origin
                             and getattr(saved, "kind", "self_hosted") == kind
                             and getattr(saved, "auth_method", "password") == auth_method
                             and getattr(saved, "console_id", None) == console_id), None)
            if not username and not password and previous and previous.url == origin:
                username, password = previous.username, previous.password
            if pin is None and previous and previous.url == origin:
                pin = previous.pin
            if auth_method == "api_key":
                from .controller_official import IntegrationClient
                api_key = validate_api_key(api_key or (getattr(previous, "api_key", "") if previous else ""))
                same_identity = previous is not None and previous.api_key == api_key and previous.pin == pin
                client = (previous if same_identity else IntegrationClient(
                    origin, api_key, pin, kind=kind, console_id=console_id))
            else:
                same_identity = (previous is not None and previous.username == username
                                 and previous.password == password and previous.pin == pin)
                client = (previous if same_identity else self._factory(origin, username, password, pin, kind=kind))
            before = (self._client, self._site, self._switch_host, self._switch_name, self._ports,
                      self._sites, self._last_success, self._updated_at, self._error, self._diagnostic_ports)
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
                records = _records(await asyncio.to_thread(client.get, "/api/self/sites"))
                sites = [{"id": row["name"], "name": _name(row.get("desc")) or row["name"]}
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
                (self._ports, self._diagnostic_ports, self._switch_name,
                 self._last_success, self._updated_at, self._error) = topology
                if before[0] is not None and before[0] is not client:
                    before[0].clear()
                return self.status()
            except ControllerError as exc:
                self._pending = client
                (self._client, self._site, self._switch_host, self._switch_name, self._ports,
                 self._sites, self._last_success, self._updated_at, self._error, self._diagnostic_ports) = before
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
                    self._diagnostic_ports = []
                    self._error = exc.detail()
            return self.status()

    async def _refresh(self, switch_host: str | None) -> None:
        if switch_host != self._switch_host:
            self._ports = {}
            self._diagnostic_ports = []
            self._switch_name = None
        self._switch_host = switch_host
        (self._ports, self._diagnostic_ports, self._switch_name,
         self._last_success, self._updated_at, self._error) = await self._read_topology(
             self._client, self._site, switch_host)

    async def _read_topology(self, client, site, switch_host):
        if not switch_host or switch_host.lower() in {"demo", "sim", "simulator"}:
            return ({}, [], None, None, None,
                    {"code": "switch_required", "message": "Connect your switch to match controller devices."})
        if getattr(client, "auth_method", "password") == "api_key":
            ports, name = await asyncio.to_thread(client.topology, site, switch_host)
            return ports, [], name, self._clock(), datetime.now(UTC).isoformat(), None
        devices = _records(await asyncio.to_thread(client.get, f"/api/s/{site}/stat/device"))
        clients = _records(await asyncio.to_thread(client.get, f"/api/s/{site}/stat/sta"))
        switch = await asyncio.to_thread(match_switch, devices, switch_host)
        return (port_metadata(switch, devices, clients),
                port_diagnostics(switch, clients, observed_at=time.time()),
                _name(switch.get("name")), self._clock(), datetime.now(UTC).isoformat(), None)

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
            self._diagnostic_ports = []
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
