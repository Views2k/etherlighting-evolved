"""Read-only names and topology from a configured UniFi Network controller."""

from __future__ import annotations

import hashlib
import hmac
import http.client
import ipaddress
import json
import re
import socket
import ssl
from http.cookies import CookieError, SimpleCookie
from urllib.parse import urlsplit

MAX_RESPONSE = 4 * 1024 * 1024
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
        self.api_key = ""
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
            if isinstance(meta, dict) and meta.get("rc") == "error":
                reason = meta.get("msg")
                if not is_login and reason == "api.err.LoginRequired":
                    self.authenticated = False
                    raise ControllerError("authentication_failed", "The controller session has expired.", 401)
                if (is_login and self.kind == "self_hosted" and not verifying and response.status == 400
                        and reason == "api.err.Invalid"):
                    raise ControllerError(
                        "authentication_failed", "Check the local controller account and password.", 401)
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
        except ControllerError as exc:
            if exc.code == "controller_error":
                exc.details["operation"] = {"login": "login", "sites": "sites", "device": "devices", "sta": "clients"}[
                    path.rsplit("/", 1)[-1]
                ]
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
            raise ControllerError("controller_error", "The controller could not complete the request.", 502,
                                  upstream_status=status)

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
        if self.kind == "self_hosted":
            payload["strict"] = True
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
        if port in valid_ports and name:
            item = metadata.setdefault(port, {"name": name, "controller": {
                "source": "unifi", "kind": "port_label", "count": 0}})
            item["controller"]["port_name"] = name
    return metadata
