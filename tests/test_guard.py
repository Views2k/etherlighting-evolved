import pytest
from fastapi.testclient import TestClient
from starlette.websockets import WebSocketDisconnect

from etherlighting_evolved.api import create_app
from etherlighting_evolved.guard import MAX_BODY, hostname

LOCAL = "http://127.0.0.1:8787"


@pytest.fixture
def client(config):
    with TestClient(create_app(config, autoconnect=False), base_url=LOCAL) as c:
        c.post("/api/session/login", json={"host": "demo"})
        yield c


@pytest.mark.parametrize("header, name", [
    ("127.0.0.1:8787", "127.0.0.1"),
    ("localhost", "localhost"),
    ("[::1]:8787", "::1"),
    ("Evil.Example:80", "evil.example"),
])
def test_hostname(header, name):
    assert hostname(header) == name


def test_other_websites_cannot_change_anything(client):
    r = client.put("/api/brightness", json={"value": 5}, headers={"Origin": "https://evil.example"})
    assert r.status_code == 403
    assert client.get("/api/state").json()["brightness"] == 100


def test_the_ui_itself_and_scripts_can(client):
    assert client.put("/api/brightness", json={"value": 40}, headers={"Origin": LOCAL}).status_code == 200
    assert client.put("/api/brightness", json={"value": 50}).status_code == 200  # no Origin: curl, eectl


def test_reading_is_not_blocked_by_origin(client):
    assert client.get("/api/effects", headers={"Origin": "https://evil.example"}).status_code == 200


def test_dns_rebinding_is_refused(client):
    assert client.get("/api/state", headers={"Host": "evil.example:8787"}).status_code == 403


def test_websocket_from_another_site_is_refused(client):
    # The test client always sends Host: testserver for WebSockets, so set it explicitly.
    headers = {"Host": "127.0.0.1:8787", "Origin": "https://evil.example"}
    with pytest.raises(WebSocketDisconnect) as closed, client.websocket_connect("/api/ws", headers=headers) as ws:
        ws.receive_json()
    assert closed.value.code == 4403


def test_websocket_from_the_ui_is_accepted(client):
    headers = {"Host": "127.0.0.1:8787", "Origin": LOCAL}
    with client.websocket_connect("/api/ws", headers=headers) as ws:
        assert ws.receive_json()["type"] == "session"


def test_configured_origins_are_allowed(config):
    config.api.cors_origins = ["http://localhost:5173"]
    with TestClient(create_app(config, autoconnect=False), base_url=LOCAL) as c:
        c.post("/api/session/login", json={"host": "demo"}, headers={"Origin": "http://localhost:5173"})
        assert c.get("/api/session").json()["logged_in"]


def test_api_requests_a_browser_marks_as_cross_site_are_refused(client):
    for site in ("cross-site", "same-site"):
        refused = client.get("/api/profiles", headers={"Sec-Fetch-Site": site})
        assert refused.status_code == 403 and refused.json()["detail"]["code"] == "forbidden"
    for site in ("same-origin", "none"):
        assert client.get("/api/profiles", headers={"Sec-Fetch-Site": site}).status_code == 200
    assert client.get("/", headers={"Sec-Fetch-Site": "cross-site"}).status_code == 200


def test_cross_site_requests_from_configured_origins_are_allowed(config):
    config.api.cors_origins = ["http://localhost:5173"]
    with TestClient(create_app(config, autoconnect=False), base_url=LOCAL) as c:
        headers = {"Origin": "http://localhost:5173", "Sec-Fetch-Site": "same-site"}
        assert c.get("/api/session", headers=headers).status_code == 200
        assert c.get("/api/session", headers={"Sec-Fetch-Site": "same-site"}).status_code == 403


def test_request_bodies_over_the_limit_are_refused(client):
    large = b'{"value": 5, "padding": "' + b"x" * MAX_BODY + b'"}'
    refused = client.put("/api/brightness", content=large, headers={"Content-Type": "application/json"})
    assert refused.status_code == 413 and refused.json()["detail"]["code"] == "too_large"
    chunked = client.put("/api/brightness", content=iter([large[:1000], large[1000:]]),
                         headers={"Content-Type": "application/json"})
    assert chunked.status_code == 413
    accepted = client.put("/api/brightness", content=iter([b'{"value": ', b"30}"]),
                          headers={"Content-Type": "application/json"})
    assert accepted.status_code == 200 and accepted.json()["brightness"] == 30


def test_wildcard_origins_require_a_token(config):
    config.api.cors_origins = ["*"]
    with pytest.raises(ValueError, match="cors_origins"):
        create_app(config, autoconnect=False)
    config.api.token = "wildcard-origin-test-token-32-characters"
    with TestClient(create_app(config, autoconnect=False), base_url=LOCAL) as c:
        headers = {"Origin": "https://evil.example"}
        assert c.put("/api/brightness", json={"value": 5}, headers=headers).status_code == 401


def test_a_non_ascii_token_header_is_refused_without_a_server_error(config):
    config.api.token = "non-ascii-header-test-token-32-characters"
    with TestClient(create_app(config, autoconnect=False), base_url=LOCAL) as c:
        assert c.get("/api/health", headers={"Authorization": b"Bearer caf\xe9"}).status_code == 401


def test_listening_on_the_network_skips_the_host_check(config):
    config.api.host = "0.0.0.0"
    config.api.token = "network-test-token-32-characters-long"
    with TestClient(create_app(config, autoconnect=False), base_url="http://ee.lan:8787") as c:
        assert c.get("/api/health", headers={"Authorization": f"Bearer {config.api.token}"}).status_code == 200
