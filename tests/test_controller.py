import asyncio
import hashlib
import json
import ssl

import pytest

from etherlighting_evolved.controller import (
    MAX_RESPONSE,
    ControllerClient,
    ControllerError,
    ControllerManager,
    controller_origin,
    match_switch,
    port_diagnostics,
    port_metadata,
)

TARGET = "02:00:00:00:00:01"
CHILD = "02:00:00:00:00:02"
PARENT = "02:00:00:00:00:03"


def topology():
    switch = {"ip": "192.0.2.1", "mac": TARGET, "name": "Lighting switch", "state": 1,
              "port_table": [{"port_idx": n} for n in range(1, 11)],
              "downlink_table": [{"mac": CHILD, "port_idx": 2}],
              "uplink": {"uplink_mac": PARENT, "port_idx": 5},
              "port_overrides": [{"port_idx": 9, "name": "Spare fiber"}]}
    return [switch, {"mac": CHILD, "name": "Access point", "state": 1},
            {"mac": PARENT, "name": "Gateway", "state": 1}]


def client(mac, port, **changes):
    return {"mac": mac, "sw_mac": TARGET, "sw_port": port, "is_wired": True, "name": "Desk", **changes}


def test_names_follow_controller_topology_without_replacing_telemetry():
    devices = topology()
    clients = [client("02:00:00:01:00:01", 1),
               client("02:00:00:01:00:02", 2, name="Behind access point"),
               client("02:00:00:01:00:03", 3, is_wired=False),
               client("02:00:00:01:00:04", 4, sw_mac=CHILD),
               client("02:00:00:01:00:05", 6, name="", hostname="Workstation")]
    mapping = port_metadata(devices[0], devices, clients)
    assert {p: info["name"] for p, info in mapping.items()} == {
        1: "Desk", 2: "Access point", 5: "Gateway", 6: "Workstation", 9: "Spare fiber"}
    assert mapping[2]["controller"]["kind"] == "device"
    assert not any("mac" in field or "ip" in field for row in mapping.values() for field in row)


def test_multiple_clients_never_pick_an_arbitrary_device_name():
    devices = topology()
    records = [client("02:00:00:01:00:01", 1), client("02:00:00:01:00:02", 1, name="Other"),
               client("02:00:00:01:00:01", 1), client("02:00:00:01:00:03", 11),
               client("02:00:00:01:00:04", True), client("02:00:00:01:00:05", 4, is_downstream=True)]
    mapping = port_metadata(devices[0], devices, records)
    assert mapping[1]["name"] == "2 clients"
    assert mapping[1]["controller"]["count"] == 2
    assert mapping[1]["controller"]["clients"] == [{"name": "Desk", "name_source": "assigned"},
                                                {"name": "Other", "name_source": "assigned"}]
    assert 4 not in mapping and 11 not in mapping


def test_multiple_client_details_keep_real_names_without_ranking_a_primary():
    devices = topology()
    records = [client("02:00:00:01:00:01", 3, name="Host", hostname="Host", sw_depth=1,
                      last_seen=1000, uptime=5),
               client("02:00:00:01:00:02", 3, name="Guest", sw_depth=1, last_seen=1000, uptime=20),
               client("02:00:00:01:00:03", 3, name="", hostname="host"),
               client("02:00:00:01:00:04", 3, name=TARGET)]
    mapping = port_metadata(devices[0], devices, records)
    assert mapping == port_metadata(devices[0], devices, list(reversed(records)))
    assert mapping[3]["name"] == "4 clients"
    assert mapping[3]["controller"] == {"source": "unifi", "kind": "multiple", "count": 4,
                                       "clients": [{"name": "Guest", "name_source": "assigned"},
                                                   {"name": "Host", "name_source": "assigned"},
                                                   {"name": "host", "name_source": "hostname"},
                                                   {"name": None, "name_source": None}],
                                       "clients_truncated": False}
    assert TARGET not in json.dumps(mapping)


def test_assigned_name_and_hostname_keep_distinct_sources_through_rename():
    devices = topology()
    records = [client("02:00:00:01:00:01", 10, name="Travel router"),
               client("02:00:00:01:00:02", 10, name="", hostname="server")]
    details = port_metadata(devices[0], devices, records)[10]["controller"]
    assert details["count"] == 2
    assert details["clients"] == [{"name": "Travel router", "name_source": "assigned"},
                                  {"name": "server", "name_source": "hostname"}]
    records[0].update(name="", hostname="travel-router")
    details = port_metadata(devices[0], devices, records)[10]["controller"]
    assert details["clients"] == [{"name": "server", "name_source": "hostname"},
                                  {"name": "travel-router", "name_source": "hostname"}]


def test_assigned_name_survives_client_list_bound():
    devices = topology()
    records = [client(f"02:00:00:01:00:{index:02x}", 10, name="", hostname=f"host-{index}")
               for index in range(70)]
    records.append(client("02:00:00:02:00:01", 10, name="Z router"))
    details = port_metadata(devices[0], devices, records)[10]["controller"]
    assert details["clients"][0] == {"name": "Z router", "name_source": "assigned"}
    assert details["count"] == 71
    assert len(details["clients"]) == 64
    assert details["clients_truncated"] is True


def test_client_detail_list_is_bounded_and_adopted_peer_keeps_precedence():
    devices = topology()
    records = [client(f"02:00:00:01:00:{index:02x}", 3) for index in range(70)]
    records.append(client("02:00:00:02:00:01", 2, name="Downstream device"))
    mapping = port_metadata(devices[0], devices, records)
    assert mapping[3]["controller"]["count"] == 70
    assert len(mapping[3]["controller"]["clients"]) == 64
    assert mapping[3]["controller"]["clients_truncated"] is True
    assert mapping[2]["name"] == "Access point"
    assert "clients" not in mapping[2]["controller"]


def test_device_remote_port_is_not_confused_with_its_own_port():
    devices = topology()
    devices[0]["downlink_table"] = []
    devices[1]["uplink"] = {"uplink_mac": TARGET, "uplink_remote_port": 8, "port_idx": 1}
    mapping = port_metadata(devices[0], devices, [])
    assert mapping[8]["name"] == "Access point"
    assert 1 not in mapping


def test_bad_optional_shapes_and_mac_fallback_are_not_names():
    switch = {"mac": TARGET, "port_table": [{"port_idx": 1}], "downlink_table": None,
              "port_overrides": "invalid"}
    assert port_metadata(switch, [], [client("02:00:00:01:00:01", 1, name=TARGET)]) == {}
    assert port_metadata({**switch, "mac": None}, [], []) == {}


def test_diagnostics_report_only_matches_and_ages_without_identifying_values():
    switch = {"mac": TARGET, "port_table": [{"port_idx": 3, "name": "Private host",
              "peer_mac": "02:00:00:01:00:01", "mac_table": [{"mac": "02:00:00:01:00:02"}]}],
              "port_overrides": [{"port_idx": 3, "name": "Private host"}],
              "lldp_table": [{"local_port_idx": 3, "chassis_id": "02:00:00:01:00:01",
                              "system_name": "Private host"}]}
    clients = [client("02:00:00:01:00:01", 3, name="Private host", ip="192.0.2.30",
                      hostname="Private hostname", last_seen=990, uptime=1234, sw_depth=2,
                      last_uplink_mac=TARGET, is_direct=True),
               client("02:00:00:01:00:02", 3, name="Private guest", hostname="", is_downstream=True,
                      sw_depth=2, last_seen=995, uptime=234, is_virtual=True)]
    result = port_diagnostics(switch, clients, observed_at=1000)
    row = result[0]
    assert row["candidate_count"] == 2 and row["port"] == 3
    assert row["port_label_matches"] == row["configured_label_matches"] == [0]
    assert row["peer_identity_fields"] == [{"field": "peer_mac", "candidates": [0]}]
    assert row["mac_table_candidates"] == [1]
    assert row["lldp"] == [{"chassis_candidates": [0],
                            "name_fields": [{"field": "system_name", "candidates": [0]}]}]
    assert row["candidates"][0]["last_seen_age_seconds"] == 10
    assert row["candidates"][0]["last_uplink_matches_switch"] is True
    assert row["candidates"][1]["included_by_mapping"] is False
    serialized = json.dumps(result)
    assert all(value not in serialized for value in
               ("Private", "192.0.2.30", TARGET, "02:00:00:01:00:01", "02:00:00:01:00:02"))


def test_diagnostics_bound_candidates_and_handle_missing_malformed_topology():
    switch = {"mac": TARGET, "port_table": [{"port_idx": 1, "name": None, "lldp_table": "invalid"}],
              "lldp_table": None, "port_overrides": None}
    clients = [client(f"02:00:00:01:00:{index:02x}", 1, last_seen="invalid", sw_depth=True,
                      uptime=float("inf"), is_direct="unknown") for index in range(100)]
    result = port_diagnostics(switch, clients, observed_at=1000)
    assert result[0]["candidate_count"] == 100
    assert result[0]["candidates_truncated"] and len(result[0]["candidates"]) == 64
    assert result[0]["lldp"] == []
    assert all(row["last_seen_age_seconds"] is None and row["sw_depth"] is None
               and row["uptime_seconds"] is None and row["flags"]["is_direct"] is None
               for row in result[0]["candidates"])
    assert port_diagnostics({"mac": None}, clients, observed_at=1000) == []


def test_diagnostics_preserve_boolean_field_variants_and_collection_shapes_only():
    switch = {"mac": TARGET, "port_table": [None, {"port_idx": 3, "sta": ["private-token"],
              "connected_client": {"mac": CHILD, "name": "Private host"}, "mac_table": None}],
              "station_table": [{"name": "Private host"}, "private-token"]}
    records = [client(CHILD, 3, name="Private host", virtual=True, is_virtual=False,
                      wired=True, is_wireless=False, is_direct="private-token")]
    row = port_diagnostics(switch, records, observed_at=1000)[0]
    assert row["port_table_row_indexes"] == [1]
    assert row["port_collection_shapes"] == {
        "sta": {"type": "list", "count": 1, "object_count": 0},
        "connected_client": {"type": "object"}, "mac_table": {"type": "null"}}
    assert row["switch_collection_shapes"] == {
        "station_table": {"type": "list", "count": 2, "object_count": 1}}
    assert row["candidates"][0]["flags"] == {"is_wired": True, "wired": True, "is_wireless": False,
                                              "is_virtual": False, "virtual": True, "is_direct": None}
    serialized = json.dumps(row)
    assert all(value not in serialized for value in ("Private host", "private-token", CHILD, TARGET))


def test_switch_match_requires_one_actual_address_and_switch_ports():
    devices = topology()
    assert match_switch(devices, "192.0.2.1") is devices[0]
    with pytest.raises(ControllerError, match="uniquely match"):
        match_switch([*devices, devices[0].copy()], "192.0.2.1")
    with pytest.raises(ControllerError, match="uniquely match"):
        match_switch(devices, "192.0.2.99")


@pytest.mark.parametrize("url", ["http://controller.test", "https://user:secret@controller.test", "https://a/?key=x",
                                 "https://a/#x", "https://a/network", "https://a:99999", "https://a\r\nInjected: x"])
def test_unsafe_origins_rejected(url):
    with pytest.raises(ControllerError) as error:
        controller_origin(url)
    assert error.value.code == "invalid_url"
    assert url not in str(error.value)


class FakeSocket:
    def getpeercert(self, binary_form=False):
        assert binary_form
        return b"test certificate"


class FakeResponse:
    def __init__(self, status=200, data=None, headers=None):
        self.status = status
        self.data = data if data is not None else b'{"meta":{"rc":"ok"},"data":[]}'
        self.headers = headers or []

    def read(self, size):
        return self.data[:size]

    def getheaders(self):
        return self.headers


class FakeConnection:
    def __init__(self, response=None, error=None):
        self.sock = FakeSocket()
        self.response = response or FakeResponse()
        self.error, self.calls, self.closed = error, [], False

    def connect(self):
        if self.error:
            raise self.error

    def request(self, method, path, body, headers):
        self.calls.append((method, path, body, headers))

    def getresponse(self):
        return self.response

    def close(self):
        self.closed = True


def test_certificate_failure_never_sends_credentials_and_returns_only_fingerprint(monkeypatch):
    client = ControllerClient("https://controller.test", "local-user", "private-password")
    bad = FakeConnection(error=ssl.SSLCertVerificationError("private diagnostic"))
    probe = FakeConnection()
    monkeypatch.setattr(client, "_connection", lambda verified=True: bad if verified else probe)
    with pytest.raises(ControllerError) as error:
        client.login()
    assert error.value.code == "certificate_untrusted"
    assert error.value.details == {"certificate_sha256": hashlib.sha256(b"test certificate").hexdigest()}
    assert not bad.calls and not probe.calls
    assert bad.closed and probe.closed
    assert "private" not in str(error.value)


def test_pinned_peer_must_match_before_any_request(monkeypatch):
    client = ControllerClient("https://controller.test", "user", "secret", "0" * 64)
    connection = FakeConnection()
    monkeypatch.setattr(client, "_connection", lambda **_: connection)
    with pytest.raises(ControllerError) as error:
        client.login()
    assert error.value.code == "certificate_changed"
    assert connection.calls == []
    assert connection.closed


def test_exact_pin_allows_auth_and_scoped_cookie(monkeypatch):
    pin = hashlib.sha256(b"test certificate").hexdigest()
    client = ControllerClient("https://controller.test", "user", "secret", pin)
    connection = FakeConnection(FakeResponse(headers=[("Set-Cookie", "sid=ok; Secure; Path=/"),
                                                      ("Set-Cookie", "alien=no; Domain=other.test; Path=/")]))
    monkeypatch.setattr(client, "_connection", lambda **_: connection)
    client.login()
    assert client.cookies == {"sid": "ok"}
    assert json.loads(connection.calls[0][2]) == {"username": "user", "password": "secret", "remember": False}
    client.get("/api/self/sites")
    assert connection.calls[-1][3]["Cookie"] == "sid=ok"


@pytest.mark.parametrize("method,path", [("POST", "/api/s/default/cmd/devmgr"), ("PUT", "/api/s/default/rest/device"),
                                         ("GET", "https://other.test/api/self/sites"),
                                         ("GET", "/api/s/../../admin/stat/device")])
def test_client_has_no_configuration_write_or_arbitrary_endpoint(monkeypatch, method, path):
    client = ControllerClient("https://controller.test", "user", "secret")
    monkeypatch.setattr(client, "_connect", lambda: pytest.fail("No network connection allowed"))
    with pytest.raises(ControllerError) as error:
        client.request(method, path)
    assert error.value.code == "invalid_request"


@pytest.mark.parametrize("response,code", [
    (FakeResponse(status=302), "redirect_blocked"), (FakeResponse(status=401), "authentication_failed"),
    (FakeResponse(status=429), "rate_limited"), (FakeResponse(data=b"x" * (MAX_RESPONSE + 1)), "response_too_large"),
    (FakeResponse(data=b"<html>private error</html>"), "invalid_response"),
    (FakeResponse(data=b'{"meta":null}'), "controller_error"),
])
def test_responses_fail_closed_and_never_echo_body(monkeypatch, response, code):
    client = ControllerClient("https://controller.test", "user", "secret")
    connection = FakeConnection(response)
    monkeypatch.setattr(client, "_connect", lambda: connection)
    with pytest.raises(ControllerError) as error:
        client.request("GET", "/api/self/sites")
    assert error.value.code == code
    assert "private" not in str(error.value)
    assert connection.closed


def challenge(status=400):
    return FakeResponse(status=status, data=json.dumps({
        "meta": {"rc": "error", "msg": "api.err.Ubic2faTokenRequired"},
        "data": {"mfa_cookie": "private-challenge-cookie"},
    }).encode(), headers=[("Set-Cookie", "challenge=session; Secure; Path=/"),
                         ("Set-Cookie", "alien=no; Domain=other.test; Path=/")])


def response(data):
    return FakeResponse(data=json.dumps({"meta": {"rc": "ok"}, "data": data}).encode())


def queued_connections(monkeypatch, target, responses):
    connections = [FakeConnection(item) for item in responses]
    pending = iter(connections)
    monkeypatch.setattr(target, "_connect", lambda *args: next(pending))
    return connections


@pytest.mark.parametrize("status", [200, 400, 401])
def test_mfa_challenge_is_recognized_before_http_status_without_secret_echo(monkeypatch, status):
    client = ControllerClient("https://controller.test", "user", "private-password")
    connections = queued_connections(monkeypatch, client, [challenge(status)])
    with pytest.raises(ControllerError) as error:
        client.login()
    assert error.value.code == "two_factor_required"
    assert client.two_factor_required and not client.authenticated
    assert client.cookies == {"challenge": "session", "UBIC_2FA": "private-challenge-cookie"}
    assert "private" not in json.dumps(error.value.detail())
    assert connections[0].closed
    with pytest.raises(ControllerError) as blocked:
        client.get("/api/self/sites")
    assert blocked.value.code == "two_factor_required"


def test_verification_continues_cookie_session_and_never_replays_code(monkeypatch):
    client = ControllerClient("https://controller.test", "user", "private-password")
    connections = queued_connections(monkeypatch, client, [
        challenge(), FakeResponse(headers=[("Set-Cookie", "sid=verified; Secure; Path=/")]),
        response([]), FakeResponse(status=401), challenge(),
    ])
    with pytest.raises(ControllerError):
        client.login()
    client.login("123456")
    posted = connections[1].calls[0]
    assert json.loads(posted[2])["ubic_2fa_token"] == "123456"
    assert "UBIC_2FA=private-challenge-cookie" in posted[3]["Cookie"]
    assert client.authenticated and not client.two_factor_required
    assert "123456" not in repr(vars(client))
    client.get("/api/self/sites")
    assert "sid=verified" in connections[2].calls[0][3]["Cookie"]
    with pytest.raises(ControllerError) as expired:
        client.get("/api/self/sites")
    assert expired.value.code == "two_factor_required"
    assert "ubic_2fa_token" not in json.loads(connections[4].calls[0][2])
    for _ in range(3):
        with pytest.raises(ControllerError, match="authenticator"):
            client.get("/api/self/sites")
    assert sum(len(item.calls) for item in connections) == 5


@pytest.mark.parametrize("failure", [
    challenge(400),
    FakeResponse(status=401, data=b'{"meta":{"rc":"error","msg":"private-123456"}}'),
    FakeResponse(status=400, data=b'private-password private-123456'),
    FakeResponse(data=b'{"meta":{"rc":"error","msg":"private-123456"}}'),
])
def test_rejected_code_stays_pending_without_exposing_or_storing_it(monkeypatch, failure):
    client = ControllerClient("https://controller.test", "user", "secret")
    queued_connections(monkeypatch, client, [challenge(), failure])
    with pytest.raises(ControllerError):
        client.login()
    with pytest.raises(ControllerError) as error:
        client.login("123456")
    assert error.value.code == "verification_failed"
    assert "private" not in json.dumps(error.value.detail())
    assert "123456" not in json.dumps(error.value.detail())
    assert "123456" not in repr(vars(client))
    assert client.two_factor_required and not client.authenticated
    with pytest.raises(ControllerError) as retry:
        client.get("/api/self/sites")
    assert retry.value.code == "two_factor_required"


def test_mfa_error_body_is_bounded_and_wrong_challenge_code_is_not_accepted(monkeypatch):
    client = ControllerClient("https://controller.test", "user", "secret")
    queued_connections(monkeypatch, client, [FakeResponse(status=400, data=b"x" * (MAX_RESPONSE + 1)),
                                            FakeResponse(status=401, data=b'{"meta":{"msg":"Ubic2faTokenRequired"}}')])
    with pytest.raises(ControllerError) as oversized:
        client.login()
    assert oversized.value.code == "response_too_large"
    with pytest.raises(ControllerError) as invalid:
        client.login()
    assert invalid.value.code == "authentication_failed"
    assert not client.two_factor_required


def test_manager_preserves_mfa_client_through_code_and_site_selection(monkeypatch):
    sites = [{"name": "default", "desc": "Home"}, {"name": "branch", "desc": "Branch"}]
    connections = queued_connections(monkeypatch, ControllerClient, [
        challenge(), FakeResponse(), response(sites), response(sites), response(topology()),
        response([client("02:00:00:01:00:01", 1)]),
    ])

    async def case():
        manager = ControllerManager()
        with pytest.raises(ControllerError) as required:
            await manager.connect(url="https://controller.test", username="user", password="secret",
                                  switch_host="192.0.2.1")
        assert required.value.code == "two_factor_required"
        candidate = manager._pending
        result = await manager.connect(url="https://controller.test", verification_code="123456",
                                       switch_host="192.0.2.1")
        assert result["error"]["code"] == "site_required"
        assert manager._pending is candidate
        result = await manager.connect(url="https://controller.test", site="branch", switch_host="192.0.2.1")
        assert result["connected"] and manager._client is candidate and manager._pending is None
        assert "123456" not in json.dumps(result)
        assert "123456" not in repr(vars(candidate))
        await manager.disconnect()
        assert not candidate.cookies and not candidate.password and not candidate.two_factor_required

    asyncio.run(case())
    requests = [item.calls[0] for item in connections]
    assert [item[0] for item in requests] == ["POST", "POST", "GET", "GET", "GET", "GET"]
    assert sum(b"ubic_2fa_token" in (item[2] or b"") for item in requests) == 1


def test_refresh_stops_network_reauthentication_after_new_mfa_challenge(monkeypatch):
    connections = queued_connections(monkeypatch, ControllerClient, [
        FakeResponse(), response([{"name": "default"}]), response(topology()), response([]),
        FakeResponse(status=401), challenge(),
    ])

    async def case():
        manager = ControllerManager()
        await manager.connect(url="https://controller.test", username="user", password="secret",
                              switch_host="192.0.2.1")
        for _ in range(4):
            result = await manager.refresh("192.0.2.1")
            assert result["error"]["code"] == "two_factor_required"
            assert not result["connected"] and result["mapped_ports"] == 0
        await manager.disconnect()

    asyncio.run(case())
    assert sum(len(item.calls) for item in connections) == 6


class FakeClient:
    instances = []
    sites = [{"name": "default", "desc": "Home"}]
    error = None

    def __init__(self, url, username, password, pin=None, *, kind="self_hosted"):
        self.url, self.username, self.password, self.pin = url, username, password, pin
        self.kind = kind
        self.authenticated = False
        self.calls = []
        self.instances.append(self)

    def login(self):
        self.authenticated = True

    def get(self, path):
        self.calls.append(path)
        if self.error:
            raise self.error
        data = (self.sites if path == "/api/self/sites" else topology() if path.endswith("/device")
                else [client("02:00:00:01:00:01", 1)])
        return {"meta": {"rc": "ok"}, "data": data}

    def clear(self):
        self.username = self.password = ""
        self.authenticated = False


def test_manager_enriches_only_matching_fresh_switch_and_clears_on_failure():
    async def case():
        clock = [10.0]
        manager = ControllerManager(client_factory=FakeClient, clock=lambda: clock[0])
        result = await manager.connect(url="https://controller.test", username="user", password="secret",
                                       switch_host="192.0.2.1")
        assert result["connected"] and result["site"] == "default"
        diagnostics = manager.diagnostics("192.0.2.1")
        assert diagnostics["available"] and diagnostics["updated_at"] == result["updated_at"]
        assert diagnostics["ports"][0]["candidate_count"] == 1
        assert manager.diagnostics("192.0.2.9") == {"available": False, "updated_at": None, "ports": []}
        ports = [{"port": 1, "display_rgb": [1.0, 2.0, 3.0], "poe": {"power_w": 8}, "traffic_bps": 900}]
        enriched = manager.enrich_ports(ports, "192.0.2.1")
        assert enriched[0]["name"] == "Desk"
        assert "name" not in ports[0]
        assert all(enriched[0][key] == value for key, value in ports[0].items())
        assert manager.enrich_ports(ports, "192.0.2.9") == ports
        clock[0] += 46
        assert manager.status()["stale"]
        assert not manager.diagnostics("192.0.2.1")["available"]
        assert manager.enrich_ports(ports, "192.0.2.1") == ports
        await manager.refresh("192.0.2.1")
        assert manager.status()["connected"]
        manager._client.error = ControllerError("unreachable", "Could not reach the controller over HTTPS.", 503)
        await manager.refresh("192.0.2.1")
        assert manager.status()["mapped_ports"] == 0
        assert manager.diagnostics("192.0.2.1")["ports"] == []
        assert manager.enrich_ports(ports, "192.0.2.1") == ports
        await manager.disconnect()
        assert not manager.status()["configured"]
        assert manager.diagnostics("192.0.2.1") == {"available": False, "updated_at": None, "ports": []}
        assert "secret" not in json.dumps(manager.status())
    asyncio.run(case())


def test_login_for_saving_returns_only_current_successful_identity():
    async def case():
        clock = [10.0]
        manager = ControllerManager(client_factory=FakeClient, clock=lambda: clock[0])
        assert not manager.can_save_login("192.0.2.1")
        await manager.connect(url="https://controller.test", username="user", password="private-password",
                              certificate_sha256="1" * 64, switch_host="192.0.2.1")
        assert manager.can_save_login("192.0.2.1")
        record = await manager.login_for_saving("192.0.2.1")
        assert record == {"url": "https://controller.test", "username": "user", "password": "private-password",
                          "site": "default", "certificate_sha256": "1" * 64, "controller_kind": "self_hosted"}
        assert "private-password" not in json.dumps(manager.status())
        assert "private-password" not in json.dumps(manager.diagnostics("192.0.2.1"))
        assert not manager.can_save_login(None)
        assert not manager.can_save_login("192.0.2.9")
        clock[0] += 46
        assert not manager.can_save_login("192.0.2.1")
        with pytest.raises(ControllerError) as stale:
            await manager.login_for_saving("192.0.2.1")
        assert stale.value.code == "controller_login_required" and stale.value.status == 409
        await manager.disconnect()
        assert not manager.can_save_login("192.0.2.1")

    asyncio.run(case())


@pytest.mark.parametrize("field,value", [("authenticated", False), ("username", ""), ("password", "")])
def test_login_for_saving_rejects_incomplete_or_unauthenticated_client(field, value):
    async def case():
        manager = ControllerManager(client_factory=FakeClient)
        await manager.connect(url="https://controller.test", username="user", password="secret",
                              switch_host="192.0.2.1")
        setattr(manager._client, field, value)
        assert not manager.can_save_login("192.0.2.1")
        with pytest.raises(ControllerError) as invalid:
            await manager.login_for_saving("192.0.2.1")
        assert invalid.value.code == "controller_login_required"
        await manager.disconnect()

    asyncio.run(case())


def test_login_for_saving_rechecks_connection_after_waiting_for_lock():
    async def case():
        manager = ControllerManager(client_factory=FakeClient)
        await manager.connect(url="https://controller.test", username="user", password="secret",
                              switch_host="192.0.2.1")
        await manager._lock.acquire()
        pending = asyncio.create_task(manager.login_for_saving("192.0.2.1"))
        await asyncio.sleep(0)
        assert not pending.done()
        manager._error = {"code": "unreachable", "message": "Could not reach the controller over HTTPS."}
        manager._lock.release()
        with pytest.raises(ControllerError) as failed:
            await pending
        assert failed.value.code == "controller_login_required"
        await manager.disconnect()

    asyncio.run(case())


def test_multisite_requires_selection_and_reuses_only_same_origin_credentials(monkeypatch):
    async def case():
        manager = ControllerManager(client_factory=FakeClient)
        result = await manager.connect(url="https://controller.test", username="user", password="secret",
                                       switch_host="192.0.2.1")
        assert result["error"]["code"] == "site_required"
        assert not result["connected"]
        assert manager._pending.calls == ["/api/self/sites"]
        result = await manager.connect(url="https://controller.test", site="branch", switch_host="192.0.2.1")
        assert result["connected"] and result["site"] == "branch"
        assert manager._client.password == "secret"
        assert "/api/s/branch/stat/device" in manager._client.calls
        await manager.disconnect()
    monkeypatch.setattr(FakeClient, "sites", [{"name": "default", "desc": "Home"},
                                            {"name": "branch", "desc": "Branch"}])
    asyncio.run(case())


def test_reconnect_retains_same_origin_pin_and_failed_replacement_keeps_session(monkeypatch):
    async def case():
        manager = ControllerManager(client_factory=FakeClient)
        await manager.connect(url="https://controller.test", username="user", password="secret",
                              certificate_sha256="1" * 64, switch_host="192.0.2.1")
        await manager.connect(url="https://controller.test", site="default", switch_host="192.0.2.1")
        assert manager._client.pin == "1" * 64
        original = manager._client
        def fail_login(self):
            raise ControllerError("authentication_failed", "Check the controller account and its Network access.", 401)
        monkeypatch.setattr(FakeClient, "login", fail_login)
        with pytest.raises(ControllerError):
            await manager.connect(url="https://other.test", username="wrong", password="wrong",
                                  switch_host="192.0.2.1")
        assert manager._client is original
        assert manager.status()["connected"]
        assert manager.status()["url"] == "https://controller.test"
        assert manager.enrich_ports([{"port": 1}], "192.0.2.1")[0]["name"] == "Desk"
        assert manager._pending.pin is None
        await manager.disconnect()
    asyncio.run(case())
