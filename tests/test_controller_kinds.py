import asyncio
import base64
import json
import ssl
from types import SimpleNamespace

import pytest
from test_controller import FakeConnection, FakeResponse, queued_connections, response, topology
from test_controller_credentials import RECORD
from test_controller_reconnect import Store

from etherlighting_evolved import controller_credentials as storage
from etherlighting_evolved.controller import ControllerClient, ControllerError, ControllerManager
from etherlighting_evolved.controller_api import ControllerIntegration, ControllerLogin
from etherlighting_evolved.controller_credentials import ControllerLoginStore
from etherlighting_evolved.credentials import CredentialStoreError


def os_login(*, headers=None):
    return FakeResponse(data=b'{"id":"fixture-local-account"}', headers=headers or [
        ("Set-Cookie", "TOKEN=fixture-session; Secure; Path=/"), ("X-CSRF-Token", "fixture-csrf")])


@pytest.mark.parametrize("kind,prefix,login_path,remember", [
    ("self_hosted", "", "/api/login", "remember"),
    ("unifi_os", "/proxy/network", "/api/auth/login", "rememberMe"),
])
def test_fresh_account_logs_in_and_reads_only_selected_controller_routes(monkeypatch, kind, prefix,
                                                                       login_path, remember):
    client = ControllerClient("https://new-controller.invalid:9443", "new-user", "fixture-secret", kind=kind)
    connections = queued_connections(monkeypatch, client, [os_login() if kind == "unifi_os" else FakeResponse(),
        response([{"name": "branch"}]), response(topology()), response([])])
    client.login()
    for path in ("/api/self/sites", "/api/s/branch/stat/device", "/api/s/branch/stat/sta"):
        client.get(path)
    calls = [connection.calls[0] for connection in connections]
    assert calls[0][0:2] == ("POST", login_path)
    assert json.loads(calls[0][2]) == {"username": "new-user", "password": "fixture-secret", remember: False}
    assert [call[1] for call in calls[1:]] == [prefix + path for path in (
        "/api/self/sites", "/api/s/branch/stat/device", "/api/s/branch/stat/sta")]
    assert all(call[0] == "GET" and call[2] is None for call in calls[1:])
    if kind == "unifi_os":
        assert all(call[3]["Cookie"] == "TOKEN=fixture-session" for call in calls[1:])
        assert all(call[3]["X-CSRF-Token"] == "fixture-csrf" for call in calls[1:])
    client.clear()
    assert not client.cookies and client.csrf_token is None and not client.password


@pytest.mark.parametrize("method,path", [
    ("POST", "/api/auth/login"), ("GET", "/proxy/network/api/self/sites"),
    ("POST", "/api/s/default/cmd/devmgr"), ("GET", "//other.invalid/api/self/sites"),
    ("GET", "/api/s/branch/stat/device?extra=1"), ("GET", "/api/s/../stat/device"),
])
def test_unifi_os_does_not_widen_logical_endpoint_allowlist(monkeypatch, method, path):
    client = ControllerClient("https://console.invalid", "user", "secret", kind="unifi_os")
    monkeypatch.setattr(client, "_connect", lambda: pytest.fail("unexpected HTTPS request"))
    with pytest.raises(ControllerError) as error:
        client.request(method, path)
    assert error.value.code == "invalid_request"


@pytest.mark.parametrize("kind", ["self_hosted", "unifi_os"])
def test_each_kind_requires_certificate_approval_before_credentials(monkeypatch, kind):
    client = ControllerClient("https://new-controller.invalid", "user", "secret", kind=kind)
    bad, probe = FakeConnection(error=ssl.SSLCertVerificationError("fixture")), FakeConnection()
    monkeypatch.setattr(client, "_connection", lambda verified=True: bad if verified else probe)
    with pytest.raises(ControllerError) as error:
        client.login()
    assert error.value.code == "certificate_untrusted"
    assert len(error.value.details["certificate_sha256"]) == 64
    assert not bad.calls and not probe.calls and bad.closed and probe.closed


@pytest.mark.parametrize("result,code", [
    (FakeResponse(status=404), "unsupported_controller"),
    (FakeResponse(status=401), "authentication_failed"),
    (FakeResponse(status=499), "controller_local_account_required"),
    (FakeResponse(data=b'{"id":"fixture"}'), "invalid_response"),
    (FakeResponse(data=b'{"errorCode":"private error"}'), "authentication_failed"),
    (FakeResponse(data=b'<html>private response</html>'), "invalid_response"),
    (FakeResponse(status=302), "redirect_blocked"),
])
def test_wrong_controller_kind_and_login_failures_never_probe_another_route(monkeypatch, result, code):
    client = ControllerClient("https://console.invalid", "user", "secret", kind="unifi_os")
    connections = queued_connections(monkeypatch, client, [result])
    with pytest.raises(ControllerError) as error:
        client.login()
    assert error.value.code == code and "private" not in str(error.value)
    assert connections[0].calls[0][1] == "/api/auth/login" and len(connections[0].calls) == 1
    assert not client.authenticated


@pytest.mark.parametrize("headers", [
    [("X-CSRF-Token", "bad\r\nInjected: value")],
    [("X-CSRF-Token", "a"), ("x-csrf-token", "b")],
    [("X-CSRF-Token", "a" * 4097)],
])
def test_invalid_csrf_never_reaches_request_headers(monkeypatch, headers):
    client = ControllerClient("https://console.invalid", "user", "secret", kind="unifi_os")
    queued_connections(monkeypatch, client, [os_login(headers=[
        ("Set-Cookie", "TOKEN=fixture; Secure; Path=/"), *headers])])
    with pytest.raises(ControllerError) as error:
        client.login()
    assert error.value.code == "invalid_response" and client.csrf_token is None
    assert not client.authenticated


def test_unifi_os_multisite_selection_keeps_kind_and_credentials_server_side(monkeypatch):
    sites = [{"name": "a", "desc": "First site"}, {"name": "b", "desc": "Second site"}]
    connections = queued_connections(monkeypatch, ControllerClient, [
        os_login(), response(sites), response(sites), response(topology()), response([])])
    async def case():
        manager = ControllerManager()
        assert manager.status()["controller_kind"] is None
        pending = await manager.connect(url="https://console.invalid", username="other-user", password="secret",
                                        switch_host="192.0.2.1", controller_kind="unifi_os")
        assert pending["error"]["code"] == "site_required" and pending["controller_kind"] == "unifi_os"
        candidate = manager._pending
        connected = await manager.connect(url="https://console.invalid", site="b", switch_host="192.0.2.1",
                                          controller_kind="unifi_os")
        assert connected["connected"] and manager._client is candidate
        saved = await manager.login_for_saving("192.0.2.1")
        assert saved["controller_kind"] == "unifi_os" and saved["site"] == "b"
        assert saved["username"] not in json.dumps(connected) and "secret" not in json.dumps(connected)
        await manager.disconnect()
        assert manager.status()["controller_kind"] is None and not candidate.cookies
    asyncio.run(case())
    assert [c.calls[0][1] for c in connections] == [
        "/api/auth/login", "/proxy/network/api/self/sites", "/proxy/network/api/self/sites",
        "/proxy/network/api/s/b/stat/device", "/proxy/network/api/s/b/stat/sta"]


def test_kind_change_cannot_reuse_previous_cookies_or_credentials(monkeypatch):
    queued_connections(monkeypatch, ControllerClient, [FakeResponse(), response([{"name": "default"}]),
                                                       response(topology()), response([])])
    async def case():
        manager = ControllerManager()
        await manager.connect(url="https://controller.invalid", username="user", password="secret",
                              switch_host="192.0.2.1")
        old = manager._client
        with pytest.raises(ControllerError) as error:
            await manager.connect(url="https://controller.invalid", controller_kind="unifi_os",
                                  switch_host="192.0.2.1")
        assert error.value.code == "credentials_required"
        assert manager._client is old and manager.status()["connected"]
        assert manager._pending.kind == "unifi_os" and not manager._pending.password
        await manager.disconnect()
    asyncio.run(case())


def test_legacy_encrypted_login_defaults_to_self_hosted_without_rewriting(tmp_path, monkeypatch):
    legacy = {key: value for key, value in RECORD.items() if key != "controller_kind"}
    monkeypatch.setattr(storage, "_dpapi", lambda data, **kwargs: data)
    store = ControllerLoginStore(tmp_path / "controller-login.json")
    envelope = {"version": 1, "protection": "windows-dpapi", "purpose": "unifi-controller-login",
                "data": base64.b64encode(json.dumps(legacy).encode()).decode()}
    store.path.write_text(json.dumps(envelope))
    original = store.path.read_bytes()
    assert store.load() == RECORD and store.path.read_bytes() == original
    os_record = {**RECORD, "controller_kind": "unifi_os", "url": "https://console.invalid"}
    store.save(os_record)
    assert store.load() == os_record


@pytest.mark.parametrize("kind", ["unknown", None, True, [], "self_hosted/../unifi_os"])
def test_invalid_saved_kind_is_rejected_before_protection(tmp_path, monkeypatch, kind):
    monkeypatch.setattr(storage, "_dpapi", lambda *a, **kw: pytest.fail("must reject before protection"))
    with pytest.raises(CredentialStoreError):
        ControllerLoginStore(tmp_path / "login.json").save({**RECORD, "controller_kind": kind})


@pytest.mark.anyio
async def test_console_saved_login_survives_new_install_session_and_disconnect_pauses_it(tmp_path, monkeypatch):
    connections = queued_connections(monkeypatch, ControllerClient, [
        os_login(), response([{"name": "branch"}]), response(topology()), response([]),
        os_login(), response([{"name": "branch"}]), response(topology()), response([]),
    ])
    sessions = SimpleNamespace(host="192.0.2.1", simulated=False, engine=SimpleNamespace(connected=True))
    store = Store()
    first = ControllerIntegration(sessions, ControllerManager(), data_dir=tmp_path, store=store)
    assert not first.status()["auto_reconnect"] and not first.status()["configured"]
    await first.connect(ControllerLogin(url="https://console.invalid", username="new-user", password="secret",
                                        controller_kind="unifi_os"))
    assert not store.saved
    await first.preferences(True)
    assert store.saved["controller_kind"] == "unifi_os" and store.saved["site"] == "branch"
    await first.close()
    second = ControllerIntegration(sessions, ControllerManager(), data_dir=tmp_path, store=store)
    await second.tick()
    assert second.status()["connected"] and second.status()["controller_kind"] == "unifi_os"
    assert second.status()["saved_login_matches_connection"]
    assert len([c for c in connections if c.calls]) == 8
    await second.disconnect()
    await second.tick()
    assert second.status()["reconnect_state"] == "paused" and store.saved
    assert len([c for c in connections if c.calls]) == 8
    await second.preferences(False)
    assert not store.saved and not second.status()["auto_reconnect"]
    await second.close()
