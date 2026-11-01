import asyncio
from copy import deepcopy

import pytest
from fastapi.testclient import TestClient
from starlette.websockets import WebSocketDisconnect

from etherlighting_evolved import api, controller_api
from etherlighting_evolved.controller import ControllerError
from etherlighting_evolved.mailbox import Mailbox
from etherlighting_evolved.session import LoginError

BASE = "http://127.0.0.1:8787"
TOKEN = "fixture-controller-token"
HEADERS = {"Authorization": f"Bearer {TOKEN}"}
LOGIN = {"url": "https://controller.invalid:8443", "username": "fixture", "password": "fixture-password"}
PORT = {"port": 1, "rgb": "#123456", "display_rgb": [0.1, 0.2, 0.3], "brightness": 45,
        "link": True, "speed_mbps": 2500, "poe_watts": 12.5, "rx_mbps": 25.5,
        "source": {"kind": "effect", "id": "fixture-zone"}}
SFP = {"port": 9, "mode": "pulse", "rgb": "#654321", "display_rgb": [0.3, 0.2, 0.1],
       "brightness": 68, "control": {"state": "active"}, "rule": None}


class FakeEngine:
    connected = True

    def ports(self):
        return [deepcopy(PORT)]

    def state(self):
        return {"mode": "running", "connected": True, "brightness": 51, "sfp": [deepcopy(SFP)],
                "zones": [{"id": "fixture-zone", "effect": {"id": "marquee"}}]}

    def state_message(self):
        return {"type": "state", "state": self.state()}

    def frame_message(self):
        return {"type": "frame", "connected": True, "mode": "running", "ports": self.ports(),
                "sfp": [deepcopy(SFP)], "time": 123.5}


class FakeSessions:
    handoff_pending = False

    def __init__(self):
        self.host = "switch.invalid"
        self.engine = None
        self.simulated = False
        self.mailboxes = set()
        self.closed = False
        self.restores = 0

    def info(self):
        return {"logged_in": self.engine is not None, "simulated": self.simulated}

    def workspace_id(self):
        return "controller-test-workspace" if self.engine is not None else None

    def listen(self):
        mailbox = Mailbox()
        self.mailboxes.add(mailbox)
        return mailbox

    def unlisten(self, mailbox):
        self.mailboxes.discard(mailbox)

    async def restore_saved(self, *, resume=False):
        self.restores += 1
        raise LoginError("no_saved_login", "Enter your SSH credentials to connect to the switch.", 409)

    async def close(self):
        self.closed = True


class FakeController:
    def __init__(self):
        self.connects = []
        self.disconnects = 0
        self.cancels = 0
        self.discoveries = []
        self.status_reads = 0
        self.error = None
        self.connected = True

    def status(self):
        self.status_reads += 1
        return {"connected": self.connected, "mapped_ports": 2}

    async def connect(self, **kwargs):
        self.connects.append(kwargs)
        if self.error:
            raise self.error
        self.connected = True
        return self.status()

    def can_save_login(self, switch_host):
        return bool(switch_host and self.connects and not self.error)

    def enrich_ports(self, ports, switch_host):
        if switch_host is None:
            return ports
        return [{**port, "name": f"Device {port['port']}", "controller": {"source": "unifi"}} for port in ports]

    async def refresh(self, switch_host):
        return self.status()

    async def disconnect(self):
        self.disconnects += 1
        self.connected = False
        return {"connected": False}

    async def cancel(self):
        self.cancels += 1
        return self.status()

    async def discover_consoles(self, key):
        self.discoveries.append(key)
        return {"consoles": [{"id": "fixture-console", "name": "Fixture console", "type": None, "blocked": False}]}


@pytest.fixture
def wired(config, monkeypatch):
    sessions, controller = FakeSessions(), FakeController()
    config.api.token = TOKEN
    monkeypatch.setattr(api, "SessionManager", lambda *args, **kwargs: sessions)
    monkeypatch.setattr(controller_api, "ControllerManager", lambda: controller)
    app = api.create_app(config, autoconnect=False)
    return app, sessions, controller


@pytest.mark.parametrize("method,path", [
    ("get", "/api/controller"), ("post", "/api/controller/connect"),
    ("post", "/api/controller/disconnect"), ("post", "/api/session/restore"),
    ("put", "/api/controller/preferences"),
    ("post", "/api/controller/cancel"),
    ("post", "/api/controller/consoles"),
])
def test_controller_and_restore_routes_require_token(wired, method, path):
    app, sessions, controller = wired
    with TestClient(app, base_url=BASE) as client:
        kwargs = {"json": LOGIN} if path.endswith("/connect") else {}
        response = getattr(client, method)(path, **kwargs)
        assert response.status_code == 401
        assert response.json()["detail"]["code"] == "bad_token"
        assert not controller.connects and not controller.disconnects and not sessions.restores
    assert sessions.closed and controller.disconnects == 1


def test_controller_cancel_is_authenticated_and_does_not_disconnect_active_controller(wired):
    app, _, controller = wired
    with TestClient(app, base_url=BASE, headers=HEADERS) as client:
        response = client.post("/api/controller/cancel")
        assert response.status_code == 200 and response.json()["connected"]
        assert controller.cancels == 1 and controller.disconnects == 0


def test_console_discovery_is_authenticated_read_only_and_does_not_echo_key(wired):
    app, _, controller = wired
    with TestClient(app, base_url=BASE, headers=HEADERS) as client:
        response = client.post("/api/controller/consoles", json={"api_key": "fixture-secret-key"})
        assert response.status_code == 200 and len(response.json()["consoles"]) == 1
        assert "fixture-secret-key" not in response.text
        assert not controller.connects and not controller.disconnects
        rejected = client.post("/api/controller/consoles", json={"api_key": "fixture-secret-key", "url": "https://other.invalid"})
        assert rejected.status_code == 422 and "fixture-secret-key" not in rejected.text
        assert controller.discoveries == ["fixture-secret-key"]


@pytest.mark.parametrize("body", [
    {"api_key": "discovery-secret-marker" * 200},
    {"api_key": {"nested": "discovery-secret-marker"}},
    {"api_key": "valid-fixture-key", "extra_secret": "discovery-secret-marker"},
])
def test_console_discovery_validation_redacts_every_submitted_secret(wired, body):
    app, _, controller = wired
    with TestClient(app, base_url=BASE, headers=HEADERS) as client:
        response = client.post("/api/controller/consoles", json=body)
        assert response.status_code == 422
        assert response.json() == {"detail": {
            "code": "invalid_credentials", "message": "Check the connection fields and try again."
        }}
        assert "discovery-secret-marker" not in response.text and "valid-fixture-key" not in response.text
        assert not controller.discoveries


@pytest.mark.parametrize("demo,engine", [(False, None), (True, FakeEngine())])
def test_controller_connect_requires_a_real_switch_before_controller_call(wired, demo, engine):
    app, sessions, controller = wired
    sessions.simulated, sessions.engine = demo, engine
    with TestClient(app, base_url=BASE, headers=HEADERS) as client:
        response = client.post("/api/controller/connect", json=LOGIN)
        assert response.status_code == 409
        assert response.json()["detail"]["code"] == "switch_required"
        assert not controller.connects


def test_controller_connect_passes_server_side_switch_and_cert_error_is_sanitized(wired):
    app, sessions, controller = wired
    sessions.engine = FakeEngine()
    fingerprint = "ab" * 32
    controller.error = ControllerError("certificate_untrusted", "Verify this controller certificate before signing in.",
                                       certificate_sha256=fingerprint)
    with TestClient(app, base_url=BASE, headers=HEADERS) as client:
        response = client.post("/api/controller/connect", json=LOGIN)
        assert response.status_code == 400
        detail = response.json()["detail"]
        assert set(detail) == {"code", "message", "certificate_sha256"}
        assert detail["certificate_sha256"] == fingerprint
        assert LOGIN["password"] not in response.text and LOGIN["username"] not in response.text
        assert sessions.host not in response.text
        assert controller.connects[0]["switch_host"] == sessions.host
        assert controller.connects[0]["password"] == LOGIN["password"]
        controller.error = None
        accepted = client.post("/api/controller/connect", json={**LOGIN, "certificate_sha256": fingerprint})
        assert accepted.status_code == 200
        assert controller.connects[-1]["certificate_sha256"] == fingerprint
        assert client.get("/api/controller").json()["connected"]


def test_api_kind_is_explicit_and_invalid_kind_never_reaches_controller(wired):
    app, sessions, manager = wired
    sessions.engine = FakeEngine()
    with TestClient(app, base_url=BASE, headers=HEADERS) as client:
        assert client.post("/api/controller/connect", json={**LOGIN, "controller_kind": "unifi_os"}).status_code == 200
        assert manager.connects[-1]["controller_kind"] == "unifi_os"
        count = len(manager.connects)
        assert client.post("/api/controller/connect", json={**LOGIN, "controller_kind": "other"}).status_code == 422
        assert len(manager.connects) == count
        assert client.post("/api/controller/connect", json=LOGIN).status_code == 200
        assert manager.connects[-1]["controller_kind"] == "self_hosted"


def test_api_key_fields_are_server_side_and_validation_does_not_echo_the_key(wired):
    app, sessions, manager = wired
    sessions.engine = FakeEngine()
    key = "fixture-private-api-key"
    body = {"url": "https://console.invalid", "controller_kind": "unifi_os", "auth_method": "api_key",
            "api_key": key}
    with TestClient(app, base_url=BASE, headers=HEADERS) as client:
        result = client.post("/api/controller/connect", json=body)
        assert result.status_code == 200 and key not in result.text
        assert manager.connects[-1]["api_key"] == key and manager.connects[-1]["password"] == ""
        count = len(manager.connects)
        rejected = client.post("/api/controller/connect", json={**body, "api_key": key * 400})
        assert rejected.status_code == 422 and key not in rejected.text
        assert len(manager.connects) == count
    assert key not in repr(controller_api.ControllerLogin(**body))


def test_rest_and_websocket_enrichment_preserves_lighting_and_telemetry(wired):
    app, sessions, _ = wired
    sessions.engine = FakeEngine()
    with TestClient(app, base_url=BASE, headers=HEADERS) as client:
        ports = client.get("/api/ports").json()
        state = client.get("/api/state").json()
        assert ports[0] == {**PORT, "name": "Device 1", "controller": {"source": "unifi"}}
        assert state["sfp"][0] == {**SFP, "name": "Device 9", "controller": {"source": "unifi"}}
        assert state["mode"] == "running" and state["brightness"] == 51
        with client.websocket_connect("/api/ws", headers={"Host": "127.0.0.1:8787", **HEADERS}) as socket:
            assert socket.receive_json()["type"] == "session"
            websocket_state = socket.receive_json()
            frame = socket.receive_json()
            assert websocket_state["state"] == state
            assert frame["ports"] == ports and frame["sfp"] == state["sfp"]
            assert frame["time"] == 123.5 and frame["connected"] and frame["mode"] == "running"
            socket.send_json({"type": "ping", "id": "fixture"})
            assert socket.receive_json() == {"type": "pong", "id": "fixture"}
        assert sessions.engine.ports() == [PORT]
        assert sessions.engine.state()["sfp"] == [SFP]
    assert not sessions.mailboxes


def test_websocket_rejects_invalid_token(wired):
    app, _, _ = wired
    with TestClient(app, base_url=BASE) as client:
        with pytest.raises(WebSocketDisconnect) as error, client.websocket_connect(
                "/api/ws", headers={"Host": "127.0.0.1:8787", "Authorization": "Bearer invalid"}):
            pytest.fail("unauthorized socket opened")
        assert error.value.code == 4401


def test_restore_saved_error_is_structured(wired):
    app, sessions, _ = wired
    with TestClient(app, base_url=BASE, headers=HEADERS) as client:
        response = client.post("/api/session/restore")
        assert response.status_code == 409
        assert response.json()["detail"]["code"] == "no_saved_login"
        assert sessions.restores == 1


def test_controller_disconnect_is_explicit_and_cleanup_closes_sessions(wired):
    app, sessions, controller = wired
    with TestClient(app, base_url=BASE, headers=HEADERS) as client:
        result = client.post("/api/controller/disconnect").json()
        assert result["reconnect_state"] == "off" and result["preferences_supported"]
        assert controller.disconnects == 1 and not sessions.closed
    assert controller.disconnects == 2 and sessions.closed


def test_failed_controller_poll_does_not_skip_switch_cleanup(wired, monkeypatch):
    app, sessions, controller = wired

    async def failed_poll():
        raise RuntimeError("fixture polling failure")

    monkeypatch.setattr(app.state.controller, "run", failed_poll)
    try:
        with TestClient(app, base_url=BASE, headers=HEADERS) as client:
            client.portal.call(asyncio.sleep, 0)
    except RuntimeError:
        pass
    assert controller.disconnects == 1 and sessions.closed


def test_failed_controller_disconnect_still_closes_switch_session(wired, monkeypatch):
    app, sessions, controller = wired

    async def failed_disconnect():
        raise RuntimeError("fixture disconnect failure")

    monkeypatch.setattr(controller, "disconnect", failed_disconnect)
    try:
        with TestClient(app, base_url=BASE, headers=HEADERS):
            pass
    except RuntimeError:
        pass
    assert sessions.closed


@pytest.mark.parametrize("path,body", [
    ("/api/controller/connect", {**LOGIN, "password": LOGIN["password"] * 400}),
    ("/api/session/login", {"host": "switch.invalid", "username": "fixture",
                            "password": {"secret": LOGIN["password"]}}),
])
def test_login_validation_never_echoes_credentials(wired, path, body):
    app, sessions, _ = wired
    sessions.engine = FakeEngine()
    with TestClient(app, base_url=BASE, headers=HEADERS) as client:
        response = client.post(path, json=body)
        assert response.status_code == 422
        assert LOGIN["password"] not in response.text


def test_verification_code_is_forwarded_only_to_controller_login(wired):
    app, sessions, controller = wired
    sessions.engine = FakeEngine()
    with TestClient(app, base_url=BASE, headers=HEADERS) as client:
        initial = client.post("/api/controller/connect", json=LOGIN)
        assert initial.status_code == 200
        assert not controller.connects[-1].get("verification_code")
        verified = client.post("/api/controller/connect", json={**LOGIN, "verification_code": "123456"})
        assert verified.status_code == 200
        assert controller.connects[-1]["verification_code"] == "123456"
        assert "123456" not in verified.text
        assert "123456" not in client.get("/api/controller").text
        assert sessions.engine.ports() == [PORT]


def test_verification_errors_do_not_echo_the_submitted_code(wired):
    app, sessions, controller = wired
    sessions.engine = FakeEngine()
    controller.error = ControllerError(
        "two_factor_required", "Enter the verification code from your authenticator.", 401)
    with TestClient(app, base_url=BASE, headers=HEADERS) as client:
        response = client.post("/api/controller/connect", json={**LOGIN, "verification_code": "123456"})
        assert response.status_code == 401
        assert response.json()["detail"]["code"] == "two_factor_required"
        assert "123456" not in response.text and LOGIN["password"] not in response.text


@pytest.mark.parametrize("code,private_value", [
    ({"secret": "fixture-verification-code"}, "fixture-verification-code"), ("123456789", "123456789"),
])
def test_invalid_verification_code_is_redacted_before_response(wired, code, private_value):
    app, sessions, controller = wired
    sessions.engine = FakeEngine()
    with TestClient(app, base_url=BASE, headers=HEADERS) as client:
        response = client.post("/api/controller/connect", json={
            **LOGIN, "verification_code": code,
        })
        assert response.status_code == 422
        assert private_value not in response.text
        assert LOGIN["password"] not in response.text
        assert not controller.connects
