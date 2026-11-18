"""Read-only UniFi Network Integration API access on a selected console and site."""

from __future__ import annotations

import http.client
import ipaddress
import json
import re
import time
from collections import deque
from dataclasses import dataclass, field
from urllib.parse import quote
from uuid import UUID

from .controller import (
    MAX_RESPONSE,
    ControllerClient,
    ControllerError,
    clean_name,
    controller_access,
    normalize_mac,
    validate_api_key,
)

PAGE_LIMIT = 200
MAX_PAGES = 5
REQUESTS_PER_MINUTE = 40
_ID = r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}"
_PAGE = re.compile(rf"/v1/sites(?:/{_ID}/devices)?\?offset=([0-9]{{1,4}})&limit=200")
_DETAIL = re.compile(rf"/v1/sites/{_ID}/devices/{_ID}")


def _uuid(value):
    if not isinstance(value, str) or not re.fullmatch(_ID, value):
        raise ControllerError("invalid_response", "The controller returned an invalid identifier.", 502)
    return str(UUID(value))


def _address(value):
    try:
        return ipaddress.ip_address(value) if isinstance(value, str) else None
    except ValueError:
        return None


def _hardware_address(value):
    mac = normalize_mac(value)
    return mac if mac and mac not in {"0" * 12, "f" * 12} and not int(mac[:2], 16) & 1 else None


def _integer(value, minimum=0, maximum=100_000_000):
    return isinstance(value, int) and not isinstance(value, bool) and minimum <= value <= maximum


@dataclass
class RequestLimit:
    """Start times of the requests in the last minute, and the earliest time the next request may start."""

    requests: deque = field(default_factory=deque)
    not_before: float = 0.0


class IntegrationClient(ControllerClient):
    def __init__(
        self, url, api_key, certificate_sha256=None, *, kind="unifi_os", console_id=None, clock=time.monotonic,
        limit=None,
    ):
        super().__init__(url, "", "", certificate_sha256, kind="unifi_os")
        self.console_id = controller_access(kind, "api_key", self.url, console_id)
        if kind == "cloud_connector" and self.pin is not None:
            raise ControllerError(
                "invalid_certificate", "The UniFi cloud connection requires a publicly trusted certificate."
            )
        self.kind, self.auth_method = kind, "api_key"
        self.api_key = validate_api_key(api_key)
        self._clock = clock
        self._limit = limit or RequestLimit()
        prefix = f"/v1/connector/consoles/{console_id}" if kind == "cloud_connector" else ""
        self._prefix = prefix + "/proxy/network/integration"

    def login(self):
        validate_api_key(self.api_key)
        # The sites read validates the key; this adapter has no credential POST.
        self.authenticated = True

    def clear(self):
        super().clear()
        self.api_key = ""

    def request(self, method, path, payload=None):
        page = _PAGE.fullmatch(path) if isinstance(path, str) else None
        if (
            method != "GET"
            or payload is not None
            or not isinstance(path, str)
            or not (page or _DETAIL.fullmatch(path))
            or page
            and (int(page[1]) % PAGE_LIMIT or int(page[1]) >= PAGE_LIMIT * MAX_PAGES)
        ):
            raise ControllerError("invalid_request", "This controller request is not available.")
        return self._request_get(self._prefix + path)

    def _request_get(self, path):
        now = self._clock()
        requests = self._limit.requests
        while requests and requests[0] <= now - 60:
            requests.popleft()
        wait_until = max(self._limit.not_before, requests[0] + 60 if len(requests) >= REQUESTS_PER_MINUTE else 0)
        if now < wait_until:
            raise ControllerError(
                "rate_limited",
                "The controller is busy. Try again shortly.",
                503,
                retry_after_seconds=max(1, int(wait_until - now) + 1),
            )
        connection = None
        try:
            connection = self._connect()
            requests.append(self._clock())
            connection.request(
                "GET",
                path,
                body=None,
                headers={"Accept": "application/json", "Accept-Encoding": "identity", "X-API-Key": self.api_key},
            )
            response = connection.getresponse()
            if 300 <= response.status < 400:
                raise ControllerError("redirect_blocked", "Use the controller's direct HTTPS address.")
            if response.status == 429:
                waits = [value for key, value in response.getheaders() if key.lower() == "retry-after"]
                delay = min(int(waits[0]), 3600) if len(waits) == 1 and re.fullmatch(r"[0-9]{1,6}", waits[0]) else 60
                self._limit.not_before = self._clock() + max(1, delay)
                raise ControllerError(
                    "rate_limited", "The controller is busy. Try again shortly.", 503, retry_after_seconds=max(1, delay)
                )
            if response.status == 404:
                raise ControllerError(
                    "integration_unavailable", "Check the selected console and its Network integration access.", 409
                )
            self._check_status(response.status)
            data = response.read(MAX_RESPONSE + 1)
            if len(data) > MAX_RESPONSE:
                raise ControllerError("response_too_large", "The controller response is too large.", 502)
            try:
                result = json.loads(data)
            except (ValueError, UnicodeError) as exc:
                raise ControllerError(
                    "invalid_response", "The controller returned an unexpected response.", 502
                ) from exc
            if not isinstance(result, dict):
                raise ControllerError("invalid_response", "The controller returned an unexpected response.", 502)
            return result
        except ControllerError:
            raise
        except (OSError, http.client.HTTPException) as exc:
            raise ControllerError("unreachable", "Could not reach the controller over HTTPS.", 503) from exc
        finally:
            if connection is not None:
                connection.close()

    def _pages(self, path):
        rows, ids, total = [], set(), None
        for page in range(MAX_PAGES):
            offset = page * PAGE_LIMIT
            result = self.request("GET", f"{path}?offset={offset}&limit={PAGE_LIMIT}")
            data = result.get("data")
            count, reported_total = result.get("count"), result.get("totalCount")
            if (
                not isinstance(data, list)
                or not _integer(count, maximum=PAGE_LIMIT)
                or count != len(data)
                or result.get("offset") != offset
                or isinstance(result.get("offset"), bool)
                or result.get("limit") != PAGE_LIMIT
                or isinstance(result.get("limit"), bool)
                or not _integer(reported_total, maximum=PAGE_LIMIT * MAX_PAGES)
                or total is not None
                and reported_total != total
            ):
                raise ControllerError(
                    "api_response_incomplete",
                    "The controller returned an incomplete device list. Refresh and try again.",
                    502,
                )
            total = reported_total
            for row in data:
                identifier = _uuid(row.get("id")) if isinstance(row, dict) else None
                if not identifier or identifier in ids:
                    raise ControllerError(
                        "api_response_incomplete", "The controller returned an ambiguous device list.", 502
                    )
                ids.add(identifier)
                rows.append(row)
            if len(rows) == total:
                return rows
            if count != PAGE_LIMIT or len(rows) > total:
                break
        raise ControllerError(
            "api_response_incomplete", "The controller returned an incomplete device list. Refresh and try again.", 502
        )

    def get(self, path):
        if path != "/api/self/sites":
            raise ControllerError("invalid_request", "This controller request is not available.")
        sites = self._pages("/v1/sites")
        self.authenticated = True
        return {
            "meta": {"rc": "ok"},
            "data": [{"name": _uuid(site["id"]), "desc": clean_name(site.get("name"))} for site in sites],
        }

    def topology(self, site, switch_host):
        site = _uuid(site)
        address = _address(switch_host)
        if address is None:
            raise ControllerError(
                "switch_identity_required", "Reconnect the switch using its IP address to match this integration.", 409
            )
        devices = self._pages(f"/v1/sites/{site}/devices")
        matches = [item for item in devices if _address(item.get("ipAddress")) == address]
        if len(matches) != 1:
            raise ControllerError(
                "switch_not_found", "Could not uniquely match the connected switch in this site.", 409
            )
        selected = matches[0]
        identifier, mac = _uuid(selected["id"]), _hardware_address(selected.get("macAddress"))
        if not mac or sum(_hardware_address(item.get("macAddress")) == mac for item in devices) != 1:
            raise ControllerError(
                "switch_not_found", "Could not uniquely match the connected switch in this site.", 409
            )
        detail = self.request("GET", f"/v1/sites/{site}/devices/{identifier}")
        ports = detail.get("interfaces", {}).get("ports") if isinstance(detail.get("interfaces"), dict) else None
        if (
            detail.get("id") != selected["id"]
            or _hardware_address(detail.get("macAddress")) != mac
            or _address(detail.get("ipAddress")) != address
            or not isinstance(ports, list)
            or not 1 <= len(ports) <= 256
        ):
            raise ControllerError(
                "switch_not_found", "The controller device details do not match the selected switch.", 409
            )
        metadata = {}
        for port in ports:
            index = port.get("idx") if isinstance(port, dict) else None
            if not _integer(index, 1, 256) or index in metadata or port.get("state") not in ("UP", "DOWN", "UNKNOWN"):
                raise ControllerError("invalid_response", "The controller returned invalid port information.", 502)
            value = {"source": "unifi_integration", "kind": "port", "client_mapping": False, "state": port["state"]}
            if name := clean_name(port.get("name")):
                value["port_name"] = name
            if _integer(port.get("speedMbps")):
                value["speed_mbps"] = port["speedMbps"]
            poe = port.get("poe")
            if isinstance(poe, dict):
                value["poe"] = {
                    key: poe[key]
                    for key, allowed in (
                        ("enabled", (True, False)),
                        ("standard", ("802.3af", "802.3at", "802.3bt")),
                        ("state", ("UP", "DOWN", "LIMITED", "UNKNOWN")),
                        ("type", (1, 2, 3, 4)),
                    )
                    if key in poe
                    and poe[key] in allowed
                    and (key != "enabled" or isinstance(poe[key], bool))
                    and (key != "type" or not isinstance(poe[key], bool))
                }
            metadata[index] = {"controller": value}
        return metadata, clean_name(detail.get("name"))


class ConsoleClient(IntegrationClient):
    """Explicit discovery on the fixed Site Manager origin."""

    def __init__(self, api_key, *, clock=time.monotonic, limit=None):
        super().__init__("https://api.ui.com", api_key, clock=clock, limit=limit)

    def request(self, method, path, payload=None):
        raise ControllerError("invalid_request", "This controller request is not available.")

    def consoles(self):
        rows, identifiers, tokens = [], set(), set()
        token = None
        for _ in range(10):
            path = "/v1/hosts?pageSize=100" + ("&nextToken=" + quote(token, safe="") if token else "")
            body = self._request_get(path)
            data = body.get("data")
            if (
                not isinstance(data, list)
                or len(data) > 100
                or body.get("httpStatusCode", 200) != 200
                or body.get("code") not in (None, "ok", "success")
            ):
                raise ControllerError("invalid_response", "UniFi returned an unexpected console list.", 502)
            for row in data:
                identifier = row.get("id") if isinstance(row, dict) else None
                try:
                    controller_access("cloud_connector", "api_key", self.url, identifier)
                except ControllerError as exc:
                    raise ControllerError(
                        "invalid_response", "UniFi returned an invalid console identifier.", 502
                    ) from exc
                if identifier in identifiers:
                    raise ControllerError("api_response_incomplete", "UniFi returned an ambiguous console list.", 502)
                identifiers.add(identifier)
                reported = row.get("reportedState")
                name = (
                    (clean_name(reported.get("hostname")) or clean_name(reported.get("name")))
                    if isinstance(reported, dict)
                    else None
                )
                rows.append(
                    {
                        "id": identifier,
                        "name": name or f"Console · {identifier[-8:]}",
                        "type": clean_name(row.get("type")),
                        "blocked": row.get("isBlocked") is True,
                    }
                )
            token = body.get("nextToken")
            if token in (None, ""):
                if not rows:
                    raise ControllerError("no_consoles", "This API key has no available UniFi consoles.", 403)
                return rows
            if (
                not isinstance(token, str)
                or not re.fullmatch(r"[\x21-\x7e]{1,2048}", token)
                or token in tokens
                or not data
            ):
                break
            tokens.add(token)
        raise ControllerError("api_response_incomplete", "UniFi returned an incomplete console list. Try again.", 502)
