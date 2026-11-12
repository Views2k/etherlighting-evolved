import json
import ssl
from types import SimpleNamespace

import pytest
from test_controller import FakeConnection, FakeResponse, queued_connections
from test_controller_credentials import windows
from test_controller_reconnect import Store

from etherlighting_evolved import controller_credentials as storage
from etherlighting_evolved.controller import ControllerError, ControllerManager
from etherlighting_evolved.controller_api import ControllerIntegration, ControllerLogin
from etherlighting_evolved.controller_credentials import ControllerLoginStore
from etherlighting_evolved.controller_official import ConsoleClient, IntegrationClient
from etherlighting_evolved.credentials import CredentialStoreError

SITE = "11111111-1111-4111-8111-111111111111"
DEVICE = "22222222-2222-4222-8222-222222222222"
SECOND = "33333333-3333-4333-8333-333333333333"
HOST = "192.0.2.1"
KEY = "fixture-api-key-secret"
CONSOLE = "fixture-console:12345"
RECORD = {
    "url": "https://console.invalid",
    "controller_kind": "unifi_os",
    "auth_method": "api_key",
    "api_key": KEY,
    "console_id": None,
    "site": SITE,
    "certificate_sha256": None,
}


def test_unconfigured_controller_advertises_only_implemented_access_methods():
    status = ControllerManager().status()
    assert not status["configured"]
    assert status["supported_auth_methods"] == {
        "self_hosted": ["password"],
        "unifi_os": ["password", "api_key"],
        "cloud_connector": ["api_key"],
    }


def reply(value, *, status=200, headers=None):
    return FakeResponse(status=status, data=json.dumps(value).encode(), headers=headers or [])


def page(data, *, offset=0, total=None):
    return {
        "count": len(data),
        "offset": offset,
        "limit": 200,
        "totalCount": len(data) if total is None else total,
        "data": data,
    }


def device():
    return {"id": DEVICE, "macAddress": "02:00:00:00:00:01", "ipAddress": HOST, "name": "Fixture switch"}


def detail():
    return {
        **device(),
        "interfaces": {
            "ports": [
                {
                    "idx": 1,
                    "state": "UP",
                    "speedMbps": 10000,
                    "poe": {"enabled": True, "type": 3, "standard": "802.3bt", "state": "UP"},
                },
                {"idx": 2, "state": "DOWN"},
            ]
        },
    }


def connected_responses():
    return [reply(page([{"id": SITE, "name": "Site"}])), reply(page([device()])), reply(detail())]


@pytest.mark.anyio
@pytest.mark.parametrize(
    "kind,url,console,prefix",
    [
        ("unifi_os", "https://console.invalid", None, "/proxy/network/integration"),
        (
            "cloud_connector",
            "https://api.ui.com",
            CONSOLE,
            f"/v1/connector/consoles/{CONSOLE}/proxy/network/integration",
        ),
    ],
)
async def test_official_setup_uses_only_documented_gets_and_keeps_ssh_telemetry(
    monkeypatch, kind, url, console, prefix
):
    connections = queued_connections(monkeypatch, IntegrationClient, connected_responses())
    manager = ControllerManager()
    result = await manager.connect(
        url=url, controller_kind=kind, auth_method="api_key", api_key=KEY, console_id=console, switch_host=HOST
    )
    assert result["connected"] and result["auth_method"] == "api_key"
    assert result["capabilities"] == {"client_mapping": False, "port_metadata": True}
    assert result["mapped_ports"] == 2 and result["switch_name"] == "Fixture switch"
    assert KEY not in json.dumps(result)
    original = {"port": 1, "name": "Port 1", "poe_watts": 7, "speed_mbps": 2500, "rgb": "#ff0000"}
    enriched = manager.enrich_ports([original], HOST)[0]
    assert all(enriched[key] == value for key, value in original.items())
    assert enriched["controller"]["speed_mbps"] == 10000
    assert "clients" not in enriched["controller"] and not enriched["controller"]["client_mapping"]
    for connection in connections:
        method, path, body, headers = connection.calls[0]
        assert method == "GET" and path.startswith(prefix + "/v1/sites") and body is None
        assert headers["X-API-Key"] == KEY and "Cookie" not in headers and "Authorization" not in headers
        assert connection.closed
    saved = await manager.login_for_saving(HOST)
    assert saved["api_key"] == KEY and "password" not in saved and "username" not in saved
    client = manager._client
    await manager.disconnect()
    assert not client.api_key and not client.authenticated


@pytest.mark.parametrize(
    "method,path,payload",
    [
        ("POST", "/api/auth/login", None),
        ("POST", f"/v1/sites/{SITE}/devices/{DEVICE}/actions", {}),
        ("GET", "/api/self/sites", None),
        ("GET", "/v1/sites?offset=0&limit=200&extra=1", None),
        ("GET", "/v1/sites?offset=1000&limit=200", None),
        ("GET", "/v1/sites?offset=1&limit=200", None),
        ("GET", "/v1/sites?offset=0&limit=200", {}),
        ("GET", "https://other.invalid/v1/sites", None),
        ("GET", f"/v1/sites/{SITE}/devices/../clients", None),
    ],
)
def test_official_allowlist_rejects_writes_and_unapproved_routes(monkeypatch, method, path, payload):
    client = IntegrationClient(RECORD["url"], KEY)
    monkeypatch.setattr(client, "_connect", lambda: pytest.fail("must reject before TLS"))
    with pytest.raises(ControllerError) as error:
        client.request(method, path, payload)
    assert error.value.code == "invalid_request"


@pytest.mark.parametrize(
    "kwargs,code",
    [
        ({"kind": "self_hosted"}, "invalid_auth_method"),
        ({"kind": "cloud_connector"}, "invalid_cloud_origin"),
        ({"kind": "cloud_connector", "url": "https://api.ui.com", "console_id": "../other"}, "invalid_console_id"),
        (
            {
                "kind": "cloud_connector",
                "url": "https://api.ui.com",
                "console_id": CONSOLE,
                "certificate_sha256": "1" * 64,
            },
            "invalid_certificate",
        ),
        ({"api_key": "bad\r\nheader"}, "invalid_api_key"),
        ({"api_key": ""}, "api_key_required"),
    ],
)
def test_invalid_official_credentials_reject_before_any_request(kwargs, code):
    with pytest.raises(ControllerError) as error:
        IntegrationClient(**{"url": RECORD["url"], "api_key": KEY, **kwargs})
    assert error.value.code == code


def test_local_key_requires_certificate_approval_before_key_header(monkeypatch):
    client = IntegrationClient(RECORD["url"], KEY)
    bad, probe = FakeConnection(error=ssl.SSLCertVerificationError("fixture")), FakeConnection()
    monkeypatch.setattr(client, "_connection", lambda verified=True: bad if verified else probe)
    with pytest.raises(ControllerError) as error:
        client.get("/api/self/sites")
    assert error.value.code == "certificate_untrusted" and KEY not in str(error.value)
    assert not bad.calls and not probe.calls and bad.closed and probe.closed


@pytest.mark.parametrize(
    "mutate",
    [
        lambda row: row.update(count=2),
        lambda row: row.update(offset=True),
        lambda row: row.update(limit=25),
        lambda row: row.update(totalCount=1001),
        lambda row: row.update(totalCount=2),
        lambda row: row.update(data=[{"id": "not-a-uuid"}]),
        lambda row: row.update(data=[row["data"][0]] * 2, count=2, totalCount=2),
    ],
)
def test_incomplete_or_ambiguous_pages_fail_closed(monkeypatch, mutate):
    body = page([{"id": SITE, "name": "Site"}])
    mutate(body)
    client = IntegrationClient(RECORD["url"], KEY)
    queued_connections(monkeypatch, client, [reply(body)])
    with pytest.raises(ControllerError):
        client.get("/api/self/sites")


def test_pagination_consumes_all_pages_and_rejects_inconsistent_total(monkeypatch):
    rows = [{"id": f"00000000-0000-4000-8000-{i:012x}", "name": "Site"} for i in range(201)]
    client = IntegrationClient(RECORD["url"], KEY)
    connections = queued_connections(
        monkeypatch, client, [reply(page(rows[:200], total=201)), reply(page(rows[200:], offset=200, total=201))]
    )
    assert len(client.get("/api/self/sites")["data"]) == 201
    assert "offset=200" in connections[1].calls[0][1]
    queued_connections(
        monkeypatch, client, [reply(page(rows[:200], total=201)), reply(page([], offset=200, total=200))]
    )
    with pytest.raises(ControllerError) as error:
        client.get("/api/self/sites")
    assert error.value.code == "api_response_incomplete"


@pytest.mark.parametrize(
    "listing,details",
    [
        ([device(), {**device(), "id": SECOND}], detail()),
        ([device(), {**device(), "id": SECOND, "ipAddress": "192.0.2.2"}], detail()),
        ([device()], {**detail(), "id": SECOND}),
        ([device()], {**detail(), "macAddress": "02:00:00:00:00:02"}),
        ([device()], {**detail(), "ipAddress": "192.0.2.2"}),
        ([device()], {**detail(), "interfaces": {"ports": []}}),
    ],
)
def test_exact_ip_selection_and_detail_identity_must_be_unique_and_consistent(monkeypatch, listing, details):
    client = IntegrationClient(RECORD["url"], KEY)
    queued_connections(monkeypatch, client, [reply(page(listing)), reply(details)])
    with pytest.raises(ControllerError) as error:
        client.topology(SITE, HOST)
    assert error.value.code == "switch_not_found"


def test_hostname_switch_is_not_guessed_or_resolved(monkeypatch):
    client = IntegrationClient(RECORD["url"], KEY)
    monkeypatch.setattr(client, "_connect", lambda: pytest.fail("no request before exact switch identity"))
    with pytest.raises(ControllerError) as error:
        client.topology(SITE, "switch.invalid")
    assert error.value.code == "switch_identity_required"


def test_retry_after_and_local_request_budget_prevent_immediate_network_retries(monkeypatch):
    now = [100.0]
    client = IntegrationClient(RECORD["url"], KEY, clock=lambda: now[0])
    connections = queued_connections(
        monkeypatch, client, [reply({}, status=429, headers=[("Retry-After", "120")]), reply(page([]))]
    )
    with pytest.raises(ControllerError) as first:
        client.get("/api/self/sites")
    assert first.value.details["retry_after_seconds"] == 120
    with pytest.raises(ControllerError):
        client.get("/api/self/sites")
    assert not connections[1].calls
    now[0] += 121
    assert client.get("/api/self/sites")["data"] == []
    client._requests.extend([now[0]] * 40)
    with pytest.raises(ControllerError):
        client.get("/api/self/sites")


def test_key_record_is_encrypted_and_legacy_record_shape_is_not_repurposed(tmp_path, monkeypatch):
    captured = []

    def protect(data, *, decrypt=False):
        if decrypt:
            return data[::-1]
        captured.append(json.loads(data))
        return data[::-1]

    monkeypatch.setattr(storage, "_dpapi", protect)
    store = ControllerLoginStore(tmp_path / "login.json")
    store.save(RECORD)
    assert captured == [RECORD] and store.load() == RECORD
    assert KEY not in store.path.read_text()
    for bad in ({**RECORD, "password": "wrong"}, {**RECORD, "controller_kind": "self_hosted"}):
        with pytest.raises(CredentialStoreError):
            store.save(bad)
    assert store.load() == RECORD


@pytest.mark.anyio
@windows
async def test_remembered_key_reopens_from_protected_file_and_rejects_bad_replacement(tmp_path, monkeypatch):
    responses = connected_responses() + [reply({}, status=401)] + connected_responses()
    connections = queued_connections(monkeypatch, IntegrationClient, responses)
    sessions = SimpleNamespace(host=HOST, simulated=False, engine=SimpleNamespace(connected=True))
    integration = ControllerIntegration(sessions, data_dir=tmp_path)
    live = await integration.connect(ControllerLogin(**RECORD, remember=True))
    target = {name: RECORD[name] for name in ("url", "auth_method", "controller_kind", "console_id")}
    assert live["connected"] and live["saved_login_matches_connection"] and live["saved_target"] == target
    path = tmp_path / "controller-login.json"
    envelope = path.read_bytes()
    assert json.loads(envelope)["protection"] == "windows-dpapi" and KEY.encode() not in envelope
    with pytest.raises(ControllerError) as rejected:
        await integration.connect(ControllerLogin(**{**RECORD, "api_key": "wrong-fixture-key"}, remember=True))
    assert rejected.value.code == "authentication_failed"
    assert path.read_bytes() == envelope and integration.status()["saved_login_matches_connection"]
    await integration.close()
    restored = ControllerIntegration(sessions, data_dir=tmp_path)
    before = restored.status()
    assert before["saved_target"] == target and not before["connected"] and not before["saved_login_matches_connection"]
    await restored.tick()
    status = restored.status()
    assert status["connected"] and status["saved_login_matches_connection"] and status["site"] == SITE
    assert all(status[k] == v for k, v in target.items())
    assert all(c.calls[0][3]["X-API-Key"] == KEY for c in connections[-3:])
    assert KEY not in json.dumps(status) and path.read_bytes() == envelope
    await restored.close()


@pytest.mark.anyio
async def test_saved_key_reconnect_and_failed_replacement_preserve_active_session(tmp_path, monkeypatch):
    queued_connections(
        monkeypatch, IntegrationClient, connected_responses() + connected_responses() + [reply({}, status=401)]
    )
    sessions = SimpleNamespace(host=HOST, simulated=False, engine=SimpleNamespace(connected=True))
    store = Store()
    integration = ControllerIntegration(sessions, ControllerManager(), data_dir=tmp_path, store=store)
    await integration.connect(ControllerLogin(**RECORD))
    assert not store.saved
    await integration.preferences(True)
    assert store.saved == RECORD
    await integration.close()
    next_session = ControllerIntegration(sessions, ControllerManager(), data_dir=tmp_path, store=store)
    await next_session.tick()
    assert next_session.status()["connected"] and next_session.status()["saved_login_matches_connection"]
    original = next_session.manager._client
    with pytest.raises(ControllerError):
        await next_session.connect(ControllerLogin(**{**RECORD, "api_key": "wrong-key"}))
    assert next_session.manager._client is original and next_session.status()["connected"]
    assert store.saved == RECORD and KEY not in json.dumps(next_session.status())
    await next_session.close()


@pytest.mark.anyio
async def test_api_key_cannot_be_reused_for_another_origin_console_or_auth_method(monkeypatch):
    queued_connections(monkeypatch, IntegrationClient, connected_responses())
    manager = ControllerManager()
    await manager.connect(**RECORD, switch_host=HOST)
    old = manager._client
    with pytest.raises(ControllerError) as absent:
        await manager.connect(
            url="https://another.invalid", controller_kind="unifi_os", auth_method="api_key", switch_host=HOST
        )
    assert absent.value.code == "api_key_required" and manager._client is old
    with pytest.raises(ControllerError) as mixed:
        await manager.connect(**RECORD, username="user", switch_host=HOST)
    assert mixed.value.code == "invalid_auth_method" and manager._client is old
    await manager.disconnect()


@pytest.mark.anyio
async def test_key_multisite_selection_reuses_only_candidate_and_commits_after_detail_match(monkeypatch):
    sites = page([{"id": SITE, "name": "North"}, {"id": SECOND, "name": "South"}])
    connections = queued_connections(
        monkeypatch,
        IntegrationClient,
        connected_responses()
        + [
            reply(sites),
            reply(sites),
            reply(page([device()])),
            reply({**detail(), "id": SECOND}),
            reply(sites),
            reply(page([device()])),
            reply(detail()),
        ],
    )
    manager = ControllerManager()
    await manager.connect(**RECORD, switch_host=HOST)
    old = manager._client
    candidate_record = {**RECORD, "url": "https://candidate.invalid", "site": None, "api_key": "candidate-key"}
    pending = await manager.connect(**candidate_record, switch_host=HOST)
    candidate = manager._pending
    assert pending["connected"] and pending["url"] == RECORD["url"]
    assert pending["pending_site_choice"]["auth_method"] == "api_key"
    assert pending["pending_site_choice"]["capabilities"]["client_mapping"] is False
    assert not manager.can_save_login(HOST)
    selected = {**candidate_record, "api_key": "", "site": SECOND}
    with pytest.raises(ControllerError) as mismatch:
        await manager.connect(**selected, switch_host=HOST)
    assert mismatch.value.code == "switch_not_found"
    assert manager._client is old and manager._pending is candidate and manager.status()["connected"]
    accepted = await manager.connect(**selected, switch_host=HOST)
    assert accepted["connected"] and accepted["site"] == SECOND and not accepted["pending_site_choice"]
    assert manager._client is candidate and not old.api_key
    for connection in connections[3:]:
        assert connection.calls[0][3]["X-API-Key"] == "candidate-key"
    assert all(f"/sites/{SECOND}/" in c.calls[0][1] for c in [connections[5], connections[6], connections[8]])
    await manager.disconnect()


@pytest.mark.anyio
async def test_cloud_candidate_console_change_never_reuses_saved_key(monkeypatch):
    record = {**RECORD, "url": "https://api.ui.com", "controller_kind": "cloud_connector", "console_id": CONSOLE}
    queued_connections(monkeypatch, IntegrationClient, connected_responses())
    manager = ControllerManager()
    await manager.connect(**record, switch_host=HOST)
    original = manager._client
    with pytest.raises(ControllerError) as absent:
        await manager.connect(**{**record, "console_id": "another-console", "api_key": ""}, switch_host=HOST)
    assert absent.value.code == "api_key_required" and manager._client is original
    await manager.disconnect()


@pytest.mark.anyio
@pytest.mark.parametrize(
    "failure,keeps_active", [(reply({**detail(), "id": SECOND}), True), (reply({}, status=401), False)]
)
async def test_same_key_site_selection_failure_preserves_old_site_unless_authentication_fails(
    monkeypatch,
    failure,
    keeps_active,
):
    sites = page([{"id": SITE, "name": "North"}, {"id": SECOND, "name": "South"}])
    queued_connections(
        monkeypatch,
        IntegrationClient,
        connected_responses()
        + [
            reply(sites),
            reply(sites),
            reply(page([device()])),
            failure,
        ],
    )
    manager = ControllerManager()
    await manager.connect(**RECORD, switch_host=HOST)
    original = manager._client
    await manager.connect(**{**RECORD, "site": None, "api_key": ""}, switch_host=HOST)
    with pytest.raises(ControllerError):
        await manager.connect(**{**RECORD, "site": SECOND, "api_key": ""}, switch_host=HOST)
    result = manager.status()
    assert manager._client is original and result["site"] == SITE
    assert result["connected"] is keeps_active
    assert result["mapped_ports"] == (2 if keeps_active else 0)
    if keeps_active:
        assert manager.enrich_ports([{"port": 1}], HOST)[0]["controller"]["state"] == "UP"
        await manager.cancel()
        assert manager.status()["connected"]
    await manager.disconnect()


@pytest.mark.anyio
async def test_official_refresh_rate_limit_preserves_session_and_obeys_retry_after(tmp_path, monkeypatch):
    now = [100.0]
    connections = queued_connections(
        monkeypatch,
        IntegrationClient,
        connected_responses()
        + [
            reply({}, status=429, headers=[("Retry-After", "120")]),
            reply(page([device()])),
            reply(detail()),
        ],
    )
    sessions = SimpleNamespace(host=HOST, simulated=False, engine=SimpleNamespace(connected=True))
    manager = ControllerManager(clock=lambda: now[0])
    integration = ControllerIntegration(sessions, manager, data_dir=tmp_path, store=Store(), clock=lambda: now[0])
    await integration.connect(ControllerLogin(**RECORD))
    original = manager._client
    original._clock = lambda: now[0]
    original._requests.clear()
    await integration.preferences(True)
    now[0] += 16
    await integration.tick()
    assert manager._client is original and original.api_key == KEY
    assert integration.status()["reconnect_state"] == "retrying" and integration._next_due == 236
    assert manager.enrich_ports([{"port": 1}], HOST) == [{"port": 1}]
    now[0] = 235
    await integration.tick()
    assert not connections[4].calls
    now[0] = 237
    await integration.tick()
    assert integration.status()["connected"] and manager._client is original
    await integration.close()


def host(identifier=CONSOLE, **fields):
    return {
        "id": identifier,
        "type": "ucore",
        "isBlocked": False,
        "reportedState": {"hostname": "Fixture Console"},
        **fields,
    }


@pytest.mark.anyio
async def test_console_discovery_follows_encoded_pagination_and_preserves_active_session(monkeypatch):
    queued_connections(monkeypatch, IntegrationClient, connected_responses())
    manager = ControllerManager()
    await manager.connect(**RECORD, switch_host=HOST)
    before, active = manager.status(), manager._client
    bodies = [
        {
            "data": [host(userData={"email": "private@example.invalid"}, ipAddress="192.0.2.2")],
            "httpStatusCode": 200,
            "nextToken": "opaque/value?x=1&y=2",
        },
        {"data": [host("second-console", isBlocked=True, reportedState=None)], "nextToken": None},
    ]
    clients = []
    connections = [FakeConnection(reply(body)) for body in bodies]
    queue = iter(connections)

    def connect(client):
        clients.append(client)
        return next(queue)

    monkeypatch.setattr(ConsoleClient, "_connect", connect)
    result = await manager.discover_consoles(KEY)
    assert manager._client is active and manager.status() == before
    assert result["consoles"] == [
        {"id": CONSOLE, "name": "Fixture Console", "type": "ucore", "blocked": False},
        {"id": "second-console", "name": "Console · -console", "type": "ucore", "blocked": True},
    ]
    assert KEY not in json.dumps(result) and "private@example.invalid" not in json.dumps(result)
    assert "192.0.2.2" not in json.dumps(result)
    assert connections[0].calls[0][1] == "/v1/hosts?pageSize=100"
    assert connections[1].calls[0][1] == "/v1/hosts?pageSize=100&nextToken=opaque%2Fvalue%3Fx%3D1%26y%3D2"
    assert all(c.calls[0][0] == "GET" and c.calls[0][3]["X-API-Key"] == KEY and c.closed for c in connections)
    assert all(client.url == "https://api.ui.com" and client.pin is None and not client.api_key for client in clients)
    await manager.disconnect()


@pytest.mark.parametrize(
    "bodies,code",
    [
        ([{"data": []}], "no_consoles"),
        ([{"data": [host(), host()]}], "api_response_incomplete"),
        ([{"data": [host("../bad")]}], "invalid_response"),
        (
            [{"data": [host()], "nextToken": "again"}, {"data": [host("other")], "nextToken": "again"}],
            "api_response_incomplete",
        ),
        ([{"data": [], "nextToken": "again"}], "api_response_incomplete"),
        ([{"data": [host()], "nextToken": "line\nbreak"}], "api_response_incomplete"),
        ([{"data": [host()], "nextToken": 123}], "api_response_incomplete"),
        ([{"data": "wrong"}], "invalid_response"),
        ([{"data": [host()], "httpStatusCode": 503}], "invalid_response"),
        ([{"data": [host()], "code": "bad_key"}], "invalid_response"),
        ([{"data": [host(str(i))], "nextToken": str(i)} for i in range(10)], "api_response_incomplete"),
    ],
)
def test_console_list_never_returns_partial_or_ambiguous_choices(monkeypatch, bodies, code):
    client = ConsoleClient(KEY)
    queued_connections(monkeypatch, client, [reply(body) for body in bodies])
    with pytest.raises(ControllerError) as error:
        client.consoles()
    assert error.value.code == code


@pytest.mark.anyio
async def test_console_discovery_rate_limit_survives_new_requests_and_key_changes(monkeypatch):
    now = [100.0]
    manager = ControllerManager(clock=lambda: now[0])
    connections = queued_connections(
        monkeypatch,
        ConsoleClient,
        [
            reply({}, status=429, headers=[("Retry-After", "120")]),
            reply({"data": [host()]}),
        ],
    )
    with pytest.raises(ControllerError):
        await manager.discover_consoles(KEY)
    with pytest.raises(ControllerError) as retry:
        await manager.discover_consoles("different-fixture-key")
    assert retry.value.code == "rate_limited" and not connections[1].calls
    now[0] = 221
    result = await manager.discover_consoles(KEY)
    assert len(result["consoles"]) == 1 and not manager.status()["configured"]


@pytest.mark.parametrize("status", [301, 401, 403, 500])
def test_console_discovery_does_not_follow_redirect_or_return_raw_error(monkeypatch, status):
    client = ConsoleClient(KEY)
    connections = queued_connections(monkeypatch, client, [reply({"message": KEY}, status=status)])
    with pytest.raises(ControllerError) as error:
        client.consoles()
    assert KEY not in json.dumps(error.value.detail()) and len(connections[0].calls) == 1


@pytest.mark.anyio
async def test_reported_integration_port_name_is_available_for_automatic_naming(monkeypatch):
    reported = detail()
    reported["interfaces"]["ports"][0]["name"] = "Office socket"
    queued_connections(monkeypatch, IntegrationClient, [
        reply(page([{"id": SITE, "name": "Site"}])), reply(page([device()])), reply(reported)])
    manager = ControllerManager()
    await manager.connect(url="https://console.invalid", controller_kind="unifi_os", auth_method="api_key",
                          api_key=KEY, switch_host=HOST)
    ports = manager.enrich_ports([{"port": 1}, {"port": 2}], HOST)
    assert ports[0]["controller"]["port_name"] == "Office socket"
    assert "port_name" not in ports[1]["controller"]
    await manager.disconnect()
