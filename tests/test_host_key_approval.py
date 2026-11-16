"""SSH host key approval: EE sends credentials only to a switch whose key the user accepted.

The handshake tests run an ephemeral asyncssh server on 127.0.0.1 with fixture credentials only. The API tests use a
transport that checks the saved key the way SSHTransport does and otherwise behaves as the simulator.
"""

import contextlib
import json

import asyncssh
import pytest
from fastapi.testclient import TestClient

from etherlighting_evolved.api import create_app
from etherlighting_evolved.credentials import SavedLogin
from etherlighting_evolved.device_playback import PlaybackRecord
from etherlighting_evolved.ownership import device_identity
from etherlighting_evolved.session import LoginError, SessionManager
from etherlighting_evolved.transport import (
    PRESENTED_LIMIT,
    HostKeyMismatchError,
    HostKeyStore,
    HostKeyUntrustedError,
    SimulatedTransport,
    SSHTransport,
    TransportError,
)

FP_A = "SHA256:" + "A" * 43
FP_B = "SHA256:" + "B" * 42 + "A"
FP_C = "SHA256:" + "C" * 43
HOST = "192.168.1.2"
OTHER = "switch.invalid"
LOGIN = {"host": HOST, "port": 22, "username": "fixture", "password": "fixture-only"}
BASE = "http://127.0.0.1"
LOCAL = ("127.0.0.1", 50000)
OWNER = "a" * 48
ACCEPT = "/api/session/host-key/accept"
REPLACE = "/api/session/host-key/replace"
UNTRUSTED_MESSAGE = "Check this switch's SSH key fingerprint before signing in."
CHANGED_MESSAGE = "The switch's SSH key changed. Check the new fingerprint before signing in."


# The real SSH handshake

def recording_server(events):
    class Server(asyncssh.SSHServer):
        def begin_auth(self, username):
            events.append(("begin_auth", username))
            return True

        def password_auth_supported(self):
            return True

        def validate_password(self, username, password):
            events.append(("password", username))
            return True

    return Server


@contextlib.asynccontextmanager
async def ssh_server(key, events, port=0):
    server = await asyncssh.create_server(recording_server(events), "127.0.0.1", port, server_host_keys=[key])
    try:
        yield server.get_port()
    finally:
        server.close()
        await server.wait_closed()


def ssh(port, store):
    return SSHTransport("127.0.0.1", port=port, host_keys=store, username="fixture", password="fixture")


@pytest.mark.anyio
async def test_first_connection_stops_before_the_username_is_sent(tmp_path):
    key, events = asyncssh.generate_private_key("ssh-ed25519"), []
    store = HostKeyStore(tmp_path / "known_switches.json")
    async with ssh_server(key, events) as port:
        transport = ssh(port, store)
        with pytest.raises(HostKeyUntrustedError) as error:
            await transport.connect()
    assert error.value.presented == key.get_fingerprint("sha256")
    assert error.value.presented_type == "ssh-ed25519"
    assert events == [] and not transport.connected
    assert store.presented("127.0.0.1", port) == error.value.presented
    assert not store.path.exists()


@pytest.mark.anyio
async def test_accepted_key_logs_in_without_another_prompt(tmp_path):
    key, events = asyncssh.generate_private_key("ssh-ed25519"), []
    store = HostKeyStore(tmp_path / "known_switches.json")
    async with ssh_server(key, events) as port:
        with pytest.raises(HostKeyUntrustedError) as error:
            await ssh(port, store).connect()
        store.accept("127.0.0.1", port, error.value.presented)
        saved = store.path.read_bytes()
        for _ in range(2):
            transport = ssh(port, store)
            await transport.connect()
            assert transport.host_key_fingerprint == error.value.presented
            await transport.close()
    assert [kind for kind, _ in events].count("password") == 2
    assert store.path.read_bytes() == saved and store.presented("127.0.0.1", port) is None


@pytest.mark.anyio
async def test_switch_pinned_by_an_earlier_release_connects_without_approval(tmp_path):
    key, events = asyncssh.generate_private_key("ssh-ed25519"), []
    path = tmp_path / "known_switches.json"
    async with ssh_server(key, events) as port:
        # The file as releases up to 0.5.1 wrote it: a flat object of addresses, no version.
        path.write_text(json.dumps({f"127.0.0.1:{port}": key.get_fingerprint("sha256")}), "utf-8")
        saved = path.read_bytes()
        store = HostKeyStore(path)
        transport = ssh(port, store)
        await transport.connect()
        await transport.close()
    assert ("password", "fixture") in events
    assert path.read_bytes() == saved and store.presented("127.0.0.1", port) is None


@pytest.mark.anyio
async def test_replaced_key_is_refused_before_authentication_with_both_fingerprints(tmp_path):
    original, replacement, events = (asyncssh.generate_private_key("ssh-ed25519"),
                                     asyncssh.generate_private_key("ssh-ed25519"), [])
    store = HostKeyStore(tmp_path / "known_switches.json")
    async with ssh_server(original, events) as port:
        store.accept("127.0.0.1", port, original.get_fingerprint("sha256"))
    async with ssh_server(replacement, events, port):
        with pytest.raises(HostKeyMismatchError) as error:
            await ssh(port, store).connect()
    assert error.value.presented == replacement.get_fingerprint("sha256")
    assert error.value.pinned == original.get_fingerprint("sha256")
    assert events == []
    assert store.presented("127.0.0.1", port) == replacement.get_fingerprint("sha256")


@pytest.mark.anyio
async def test_compare_and_swap_replacement_then_login(tmp_path):
    original, replacement, events = (asyncssh.generate_private_key("ssh-ed25519"),
                                     asyncssh.generate_private_key("ssh-ed25519"), [])
    store = HostKeyStore(tmp_path / "known_switches.json")
    async with ssh_server(original, events) as port:
        store.accept("127.0.0.1", port, original.get_fingerprint("sha256"))
    async with ssh_server(replacement, events, port):
        store.replace("127.0.0.1", port, original.get_fingerprint("sha256"), replacement.get_fingerprint("sha256"))
        transport = ssh(port, store)
        await transport.connect()
        await transport.close()
    assert ("password", "fixture") in events


# The saved keys and the transport around asyncssh

def test_store_replace_refuses_a_stale_saved_key(tmp_path):
    store = HostKeyStore(tmp_path / "known_switches.json")
    store.accept(HOST, 22, FP_A)
    saved = store.path.read_bytes()
    with pytest.raises(HostKeyMismatchError):
        store.replace(HOST, 22, FP_C, FP_B)
    assert store.path.read_bytes() == saved


def test_store_accept_never_overwrites_a_different_key(tmp_path):
    store = HostKeyStore(tmp_path / "known_switches.json")
    store.accept(HOST, 22, FP_A)
    saved = store.path.read_bytes()
    with pytest.raises(HostKeyMismatchError) as error:
        store.accept(HOST, 22, FP_B)
    assert (error.value.presented, error.value.pinned) == (FP_B, FP_A)
    store.accept(HOST, 22, FP_A)
    assert store.path.read_bytes() == saved


@pytest.mark.anyio
async def test_unreadable_store_fails_before_any_network_io(tmp_path, monkeypatch):
    path = tmp_path / "known_switches.json"
    path.write_text("not-json", "utf-8")
    monkeypatch.setattr(asyncssh, "connect", lambda **kwargs: pytest.fail("the switch must not be contacted"))
    with pytest.raises(TransportError) as error:
        await SSHTransport(HOST, host_keys=HostKeyStore(path), username="fixture", password="fixture").connect()
    assert not isinstance(error.value, HostKeyUntrustedError | HostKeyMismatchError)


class FakeKey:
    def __init__(self, fingerprint, algorithm="ssh-ed25519"):
        self.fingerprint, self.algorithm = fingerprint, algorithm

    def get_fingerprint(self, hash_name):
        return self.fingerprint

    def get_algorithm(self):
        return self.algorithm


class FakeConnection:
    def __init__(self, key):
        self.key, self.closed = key, False

    def get_server_host_key(self):
        return self.key

    def close(self):
        self.closed = True

    async def wait_closed(self):
        pass


def handshake(key, *, after=None, connection=None):
    """A stand-in for asyncssh.connect that runs EE's key check; credentials pass only if the check accepts."""
    async def connect(**kwargs):
        client = kwargs["client_factory"]()
        if not client.validate_host_public_key(HOST, HOST, 22, key):
            raise asyncssh.HostKeyNotVerifiable("fixture rejection")
        if after:
            after()
        if connection is None:
            pytest.fail("credentials must not be sent")
        return connection
    return connect


@pytest.mark.anyio
async def test_transport_without_a_store_trusts_no_key(monkeypatch):
    monkeypatch.setattr(asyncssh, "connect", handshake(FakeKey(FP_A)))
    with pytest.raises(HostKeyUntrustedError) as error:
        await SSHTransport(HOST, username="fixture", password="fixture").connect()
    assert error.value.presented == FP_A and error.value.presented_type == "ssh-ed25519"


@pytest.mark.anyio
@pytest.mark.parametrize("change,expected", [("replace", HostKeyMismatchError), ("forget", HostKeyUntrustedError)])
async def test_key_changed_during_authentication_is_not_adopted(tmp_path, monkeypatch, change, expected):
    store = HostKeyStore(tmp_path / "known_switches.json")
    store.accept(HOST, 22, FP_A)
    connection = FakeConnection(FakeKey(FP_A))

    def change_the_pin():
        if change == "replace":
            store.replace(HOST, 22, FP_A, FP_B)
        else:
            store.forget(HOST, 22)

    monkeypatch.setattr(asyncssh, "connect", handshake(FakeKey(FP_A), after=change_the_pin, connection=connection))
    transport = SSHTransport(HOST, host_keys=store, username="fixture", password="fixture")
    with pytest.raises(expected) as error:
        await transport.connect()
    assert error.value.presented == FP_A
    if expected is HostKeyMismatchError:
        assert error.value.pinned == FP_B
    assert connection.closed and not transport.connected and transport.host_key_fingerprint is None
    assert store.presented(HOST, 22) == FP_A


@pytest.mark.anyio
async def test_rejection_without_the_callback_matches_the_store(tmp_path, monkeypatch):
    async def refuse(**kwargs):
        raise asyncssh.HostKeyNotVerifiable("host certificate")

    monkeypatch.setattr(asyncssh, "connect", refuse)
    store = HostKeyStore(tmp_path / "known_switches.json")
    with pytest.raises(HostKeyUntrustedError) as untrusted:
        await SSHTransport(HOST, host_keys=store, username="fixture", password="fixture").connect()
    assert untrusted.value.presented is None
    store.accept(HOST, 22, FP_A)
    with pytest.raises(HostKeyMismatchError) as changed:
        await SSHTransport(HOST, host_keys=store, username="fixture", password="fixture").connect()
    assert changed.value.presented is None and changed.value.pinned == FP_A


@pytest.mark.anyio
async def test_connect_keeps_validation_on_and_disables_gss(tmp_path, monkeypatch):
    seen = {}

    async def connect(**kwargs):
        seen.update(kwargs)
        raise asyncssh.HostKeyNotVerifiable("fixture rejection")

    monkeypatch.setattr(asyncssh, "connect", connect)
    with pytest.raises(HostKeyUntrustedError):
        await SSHTransport(HOST, host_keys=HostKeyStore(tmp_path / "keys.json"), username="fixture",
                           password="fixture").connect()
    assert seen["known_hosts"] == ([], [], []) and seen["config"] is None
    assert seen["gss_kex"] is False and seen["gss_auth"] is False
    assert seen["client_keys"] is None and seen["agent_path"] is None


def test_presented_keys_are_bounded(tmp_path):
    store = HostKeyStore(tmp_path / "known_switches.json")
    for number in range(40):
        store.note_presented(f"switch-{number}.invalid", 22, FP_A)
    kept = [number for number in range(40) if store.presented(f"switch-{number}.invalid", 22)]
    assert kept == list(range(40 - PRESENTED_LIMIT, 40))
    store.note_presented("switch-8.invalid", 22, FP_B)
    store.note_presented("new.invalid", 22, FP_A)
    assert store.presented("switch-8.invalid", 22) == FP_B and store.presented("switch-9.invalid", 22) is None


# The session and its routes

class KeyedSwitch:
    """The simulator behind a key check that works as SSHTransport's: only the saved key gets past connect."""

    def __init__(self, store, host, port, presented):
        self.store, self.host, self.port, self.presented = store, host.strip(), port, presented
        self.simulator = SimulatedTransport(latency=0)
        self.host_key_fingerprint = None

    def __getattr__(self, name):
        return getattr(self.simulator, name)

    async def connect(self):
        pinned = self.store.get(self.host, self.port)
        if pinned != self.presented:
            self.store.note_presented(self.host, self.port, self.presented)
            if pinned is None:
                raise HostKeyUntrustedError("fixture", presented=self.presented, presented_type="ssh-ed25519")
            raise HostKeyMismatchError("fixture", presented=self.presented, presented_type="ssh-rsa", pinned=pinned)
        self.store.clear_presented(self.host, self.port)
        self.host_key_fingerprint = self.presented
        await self.simulator.connect()

    async def run_isolated(self, command, *, channel, timeout=10.0):
        return await self.simulator.run(command, channel=channel, timeout=timeout)


class Keys(dict):
    """The key each host presents; FP_A unless set."""

    def __missing__(self, host):
        return FP_A


def keyed_factory(holder, keys):
    def factory(**kwargs):
        return KeyedSwitch(holder["sessions"].host_keys, kwargs["host"], kwargs["port"], keys[kwargs["host"].strip()])
    return factory


@pytest.fixture
def keys():
    return Keys()


@pytest.fixture
def saved_login(monkeypatch):
    monkeypatch.setattr(SavedLogin, "load", lambda self: dict(LOGIN))


def make_app(config, keys):
    """The API with a service that reads the saved files in the data folder as they are now."""
    holder = {}
    application = create_app(config, autoconnect=False, transport_factory=keyed_factory(holder, keys))
    holder["sessions"] = application.state.sessions
    return application


@pytest.fixture
def app(config, keys):
    return make_app(config, keys)


@pytest.fixture
def client(app):
    with TestClient(app, base_url=BASE, client=LOCAL) as test_client:
        yield test_client


def untrusted(fingerprint=FP_A):
    return {"code": "host_key_untrusted", "message": UNTRUSTED_MESSAGE, "host": HOST, "port": 22,
            "ssh_host_key": fingerprint, "ssh_host_key_type": "ssh-ed25519"}


def changed(saved=FP_A, new=FP_B):
    return {"code": "host_key_changed", "message": CHANGED_MESSAGE, "host": HOST, "port": 22, "ssh_host_key": new,
            "ssh_host_key_type": "ssh-rsa", "saved_ssh_host_key": saved}


def save_playback(config, record):
    PlaybackRecord(config.data_dir / "switch-playback.json").save(record)
    return (config.data_dir / "switch-playback.json").read_bytes()


def log_in_elsewhere(client, app):
    """Connect to OTHER, a switch that is not the simulator and whose key is saved, up to its lighting choice."""
    app.state.sessions.host_keys.accept(OTHER, 22, FP_A)
    return client.post("/api/session/login", json={**LOGIN, "host": OTHER}).json()["pending_scene_choice"]


def choose_new_lighting(client, pending):
    return client.post("/api/session/scene-choice", json={"connection_id": pending["connection_id"],
                       "choice": "start_new", "legacy_revision": pending["legacy_revision"]}).json()


def test_login_reports_an_untrusted_key_with_its_fingerprint(client):
    response = client.post("/api/session/login", json=LOGIN)
    assert response.status_code == 409
    assert response.json()["detail"] == untrusted()
    assert client.get("/api/session").json()["error"] == untrusted()


def test_login_reports_a_changed_key_with_saved_and_new_fingerprints(client, app, keys):
    app.state.sessions.host_keys.accept(HOST, 22, FP_A)
    keys[HOST] = FP_B
    response = client.post("/api/session/login", json=LOGIN)
    assert response.status_code == 409 and response.json()["detail"] == changed()
    assert client.get("/api/session").json()["error"] == changed()
    assert choose_new_lighting(client, log_in_elsewhere(client, app))["logged_in"]
    assert client.post("/api/session/login", json=LOGIN).status_code == 409
    session = client.get("/api/session").json()
    assert session["logged_in"] and session["host"] == OTHER and session["error"] == changed()


def test_accept_saves_only_the_key_ee_last_saw(client, app, monkeypatch):
    sessions = app.state.sessions
    assert client.post("/api/session/login", json=LOGIN).status_code == 409
    stale = client.post(ACCEPT, json={"host": HOST, "port": 22, "ssh_host_key": FP_B})
    assert stale.status_code == 409 and stale.json()["detail"]["code"] == "host_key_stale"
    assert not sessions.host_keys.path.exists()
    assert client.get("/api/session").json()["error"] == untrusted()
    accepted = client.post(ACCEPT, json={"host": HOST, "port": 22, "ssh_host_key": FP_A})
    assert accepted.status_code == 200 and accepted.json()["error"] is None
    assert sessions.host_keys.get(HOST, 22) == FP_A and sessions.host_keys.presented(HOST, 22) is None
    assert client.get("/api/session").json()["error"] is None
    writes = []
    monkeypatch.setattr(HostKeyStore, "_save", lambda self, data: writes.append(data))
    assert client.post(ACCEPT, json={"host": HOST, "port": 22, "ssh_host_key": FP_A}).status_code == 200
    assert writes == []
    assert client.post("/api/session/login", json=LOGIN).json()["pending_scene_choice"]


def test_accept_never_replaces_a_saved_key(client, app, keys):
    app.state.sessions.host_keys.accept(HOST, 22, FP_A)
    keys[HOST] = FP_B
    assert client.post("/api/session/login", json=LOGIN).status_code == 409
    response = client.post(ACCEPT, json={"host": HOST, "port": 22, "ssh_host_key": FP_B})
    assert response.status_code == 409 and response.json()["detail"]["code"] == "host_key_stale"
    assert app.state.sessions.host_keys.get(HOST, 22) == FP_A


def test_replace_requires_the_saved_and_the_presented_key(client, app, keys, monkeypatch):
    store = app.state.sessions.host_keys
    store.accept(HOST, 22, FP_A)
    keys[HOST] = FP_B
    assert client.post("/api/session/login", json=LOGIN).status_code == 409
    for saved, new in [(FP_C, FP_B), (FP_A, FP_C), (FP_B, FP_B)]:
        response = client.post(REPLACE, json={"host": HOST, "port": 22, "saved_ssh_host_key": saved,
                                              "ssh_host_key": new})
        assert response.status_code == 409 and response.json()["detail"]["code"] == "host_key_stale"
        assert store.get(HOST, 22) == FP_A
    body = {"host": HOST, "port": 22, "saved_ssh_host_key": FP_A, "ssh_host_key": FP_B}
    response = client.post(REPLACE, json=body)
    assert response.status_code == 200 and response.json()["error"] is None
    assert store.get(HOST, 22) == FP_B
    writes = []
    monkeypatch.setattr(HostKeyStore, "_save", lambda self, data: writes.append(data))
    assert client.post(REPLACE, json=body).status_code == 200 and writes == []
    assert client.post("/api/session/login", json=LOGIN).json()["pending_scene_choice"]


@pytest.fixture
def replaced_switch(config, keys, saved_login):
    """A saved login for HOST, its saved key FP_A with switch playback, and a switch that now presents FP_B."""
    HostKeyStore(config.data_dir / "known_switches.json").accept(HOST, 22, FP_A)
    keys[HOST] = FP_B
    return save_playback(config, {"state": "last_playing", "device": device_identity(FP_A), "owner": OWNER})


REPLACE_BODY = {"host": HOST, "port": 22, "saved_ssh_host_key": FP_A, "ssh_host_key": FP_B}


def test_replace_clears_the_playback_record_of_the_replaced_key(config, keys, replaced_switch):
    app = make_app(config, keys)
    with TestClient(app, base_url=BASE, client=LOCAL) as client:
        restore = client.post("/api/session/restore?resume=true")
        assert restore.status_code == 409 and restore.json()["detail"] == changed()
        response = client.post(REPLACE, json=REPLACE_BODY)
        assert response.status_code == 200 and response.json()["switch_playback"] is None
        assert not (config.data_dir / "switch-playback.json").exists()
        assert app.state.sessions.info()["switch_playback"] is None
        session = client.post("/api/session/restore?resume=true").json()
        assert session["pending_scene_choice"] and session["error"] is None


@pytest.mark.parametrize("failure", ["write", "stale"])
def test_failed_key_replacement_restores_the_playback_record(config, keys, replaced_switch, monkeypatch, failure):
    app = make_app(config, keys)
    store = app.state.sessions.host_keys
    with TestClient(app, base_url=BASE, client=LOCAL) as client:
        assert client.post("/api/session/restore?resume=true").status_code == 409
        if failure == "write":
            def fail(self, data):
                raise OSError("fixture storage failure")
            monkeypatch.setattr(HostKeyStore, "_save", fail)
        else:
            def fail(self, host, port, previous, fingerprint):
                raise HostKeyMismatchError("fixture", presented=fingerprint, pinned=FP_C)
            monkeypatch.setattr(HostKeyStore, "replace", fail)
        response = client.post(REPLACE, json=REPLACE_BODY)
    expected = ("host_key_save_failed", 500) if failure == "write" else ("host_key_stale", 409)
    assert (response.json()["detail"]["code"], response.status_code) == expected
    assert (config.data_dir / "switch-playback.json").read_bytes() == replaced_switch
    assert app.state.sessions.info()["switch_playback"] == "last_confirmed"
    assert store.get(HOST, 22) == FP_A and store.presented(HOST, 22) == FP_B


@pytest.mark.parametrize("record", [
    {"state": "handoff", "device": device_identity(FP_A), "owner": OWNER},
    {"state": "last_playing", "device": device_identity(FP_C), "owner": OWNER},
    None,
])
def test_replace_keeps_playback_it_may_not_clear(config, keys, record):
    path = config.data_dir / "switch-playback.json"
    if record is None:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("{unreadable", "utf-8")
        content = path.read_bytes()
    else:
        content = save_playback(config, record)
    HostKeyStore(config.data_dir / "known_switches.json").accept(HOST, 22, FP_A)
    app = make_app(config, keys)
    store = app.state.sessions.host_keys
    store.note_presented(HOST, 22, FP_B)
    with TestClient(app, base_url=BASE, client=LOCAL) as client:
        response = client.post(REPLACE, json=REPLACE_BODY)
    assert response.status_code == 409 and response.json()["detail"]["code"] == "switch_playback_reserved"
    assert path.read_bytes() == content and store.get(HOST, 22) == FP_A


@pytest.mark.parametrize("logged_in", [True, False])
def test_replace_is_refused_while_a_switch_connection_is_open(client, app, logged_in):
    store = app.state.sessions.host_keys
    store.accept(HOST, 22, FP_A)
    store.note_presented(HOST, 22, FP_B)
    pending = log_in_elsewhere(client, app)
    if logged_in:
        assert choose_new_lighting(client, pending)["logged_in"]
    response = client.post(REPLACE, json=REPLACE_BODY)
    assert response.status_code == 409 and response.json()["detail"]["code"] == "host_key_in_use"
    assert store.get(HOST, 22) == FP_A


@pytest.mark.parametrize("route,body", [
    (ACCEPT, {"host": HOST, "port": 22, "ssh_host_key": FP_B}),
    (REPLACE, REPLACE_BODY),
])
def test_host_key_routes_are_refused_for_callers_off_this_computer(app, route, body):
    store = app.state.sessions.host_keys
    for caller in [("100.64.0.5", 50000), ("testclient", 50000), ("::ffff:100.64.0.5", 50000)]:
        with TestClient(app, base_url=BASE, client=caller) as remote:
            for request in (body, {**body, "port": "22"}):
                response = remote.post(route, json=request)
                assert response.status_code == 403
                assert response.json()["detail"] == {"code": "local_access_required",
                                                     "message": "Manage this setting on the computer running EE."}
    assert not store.path.exists()
    for caller in [("::1", 50000), ("127.0.0.1", 50000), ("::ffff:127.0.0.1", 50000)]:
        with TestClient(app, base_url=BASE, client=caller) as local:
            response = local.post(route, json=body)
            assert response.status_code == 409 and response.json()["detail"]["code"] == "host_key_stale"


@pytest.mark.parametrize("route,body", [
    (ACCEPT, {"host": HOST, "port": 22, "ssh_host_key": FP_A}),
    (REPLACE, REPLACE_BODY),
])
def test_host_key_requests_reject_malformed_input(client, route, body):
    for change in [{"ssh_host_key": FP_A[:-1]}, {"ssh_host_key": "MD5:" + "A" * 43}, {"port": 0}, {"port": "22"},
                   {"port": True}, {"port": 22.5}, {"force": True}, {"host": ""}]:
        response = client.post(route, json={**body, **change})
        assert response.status_code == 422, change
        assert response.json()["detail"] == {"code": "invalid_credentials",
                                             "message": "Check the connection fields and try again."}
    missing = {key: value for key, value in body.items() if key != "ssh_host_key"}
    assert client.post(route, json=missing).status_code == 422
    blank = client.post(route, json={**body, "host": "   "})
    assert blank.status_code == 400 and blank.json()["detail"]["code"] == "missing_host"


def test_failed_key_write_keeps_the_previous_pin(client, app, monkeypatch):
    store = app.state.sessions.host_keys
    store.accept(OTHER, 22, FP_C)
    saved = store.path.read_bytes()
    assert client.post("/api/session/login", json=LOGIN).status_code == 409

    def fail(self, data):
        raise OSError("fixture storage failure")

    monkeypatch.setattr(HostKeyStore, "_save", fail)
    response = client.post(ACCEPT, json={"host": HOST, "port": 22, "ssh_host_key": FP_A})
    assert response.status_code == 500 and response.json()["detail"]["code"] == "host_key_save_failed"
    assert store.path.read_bytes() == saved and store.presented(HOST, 22) == FP_A
    assert client.get("/api/session").json()["error"] == untrusted()


def test_unreadable_saved_keys_are_reported_without_a_write(client, app):
    store = app.state.sessions.host_keys
    store.path.parent.mkdir(parents=True, exist_ok=True)
    store.path.write_text("not-json", "utf-8")
    store.note_presented(HOST, 22, FP_A)
    for route, body in [(ACCEPT, {"host": HOST, "port": 22, "ssh_host_key": FP_A}), (REPLACE, REPLACE_BODY)]:
        response = client.post(route, json=body)
        assert response.status_code == 500 and response.json()["detail"]["code"] == "host_key_save_failed"
    assert store.path.read_text("utf-8") == "not-json"


def test_forget_host_key_route_is_gone(client, app):
    app.state.sessions.host_keys.accept(HOST, 22, FP_A)
    response = client.post("/api/session/forget-host-key", json={"host": HOST, "port": 22})
    assert response.status_code == 404
    assert app.state.sessions.host_keys.get(HOST, 22) == FP_A


def test_saved_login_restore_reports_a_changed_key(config, keys, saved_login):
    app = make_app(config, keys)
    app.state.sessions.host_keys.accept(HOST, 22, FP_A)
    keys[HOST] = FP_B
    with TestClient(app, base_url=BASE, client=LOCAL) as client:
        response = client.post("/api/session/restore?resume=true")
        assert response.status_code == 409 and response.json()["detail"] == changed()
        session = client.get("/api/session").json()
    assert session["has_saved_login"] and session["remembered"]["host"] == HOST


def manager(config, keys):
    holder = {}
    sessions = SessionManager(config, transport_factory=keyed_factory(holder, keys))
    holder["sessions"] = sessions
    return sessions


@pytest.mark.anyio
async def test_automatic_login_leaves_the_key_problem_for_the_connect_page(config, keys, saved_login, caplog):
    sessions = manager(config, keys)
    try:
        await sessions.auto_connect()
        assert sessions.info()["error"] == untrusted()
        assert not sessions.info()["logged_in"] and not sessions.info()["connecting"]
        assert f"Presented key: {FP_A}." in caplog.text
    finally:
        await sessions.close()


@pytest.mark.anyio
async def test_accept_publishes_a_session_message(config, keys):
    sessions = manager(config, keys)
    try:
        with pytest.raises(LoginError) as error:
            await sessions.connect(HOST, "fixture", "fixture-only")
        assert error.value.detail() == untrusted()
        mailbox = sessions.listen()
        await sessions.accept_host_key(HOST, 22, FP_A)
        message = await mailbox.get()
        assert message["type"] == "session" and message["session"]["error"] is None
    finally:
        await sessions.close()


@pytest.mark.anyio
async def test_reopening_ee_after_a_key_change_leads_to_review(config, keys, saved_login):
    HostKeyStore(config.data_dir / "known_switches.json").accept(HOST, 22, FP_A)
    record = save_playback(config, {"state": "last_playing", "device": device_identity(FP_A), "owner": OWNER})
    first = manager(config, keys)
    await first.close()
    assert (config.data_dir / "switch-playback.json").read_bytes() == record
    assert first.info()["handoff_state"] != "unconfirmed"
    keys[HOST] = FP_B
    sessions = manager(config, keys)
    try:
        await sessions.auto_connect()
        assert sessions.info()["error"] == changed()
        await sessions.replace_host_key(HOST, 22, FP_A, FP_B)
        assert (await sessions.restore_saved(resume=True))["pending_scene_choice"]
    finally:
        await sessions.close()
