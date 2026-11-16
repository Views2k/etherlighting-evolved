"""SSH host keys: the first key of an address is saved at the first login, and a changed key is refused before any
credentials are sent until the user accepts it on the computer running EE.

The handshake tests run an ephemeral asyncssh server on 127.0.0.1 with fixture credentials only. The API tests use a
transport that checks and saves the key the way SSHTransport does and otherwise behaves as the simulator.
"""

import contextlib
import json
import logging

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
REPLACE = "/api/session/host-key/replace"
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
async def test_first_connection_sends_the_password_and_saves_the_key(tmp_path, caplog):
    key, events = asyncssh.generate_private_key("ssh-ed25519"), []
    fingerprint = key.get_fingerprint("sha256")
    store = HostKeyStore(tmp_path / "known_switches.json")
    caplog.set_level(logging.INFO, "etherlighting_evolved.transport")
    async with ssh_server(key, events) as port:
        transport = ssh(port, store)
        await transport.connect()
        assert (transport.host_key_fingerprint, transport.host_key_type) == (fingerprint, "ssh-ed25519")
        await transport.close()
        assert events == [("begin_auth", "fixture"), ("password", "fixture")]
        assert store.get("127.0.0.1", port) == fingerprint and store.presented("127.0.0.1", port) is None
        assert f"saved the ssh-ed25519 SSH key {fingerprint}" in caplog.text
        saved = store.path.read_bytes()
        transport = ssh(port, store)
        await transport.connect()
        await transport.close()
    assert [kind for kind, _ in events].count("password") == 2
    assert store.path.read_bytes() == saved


@pytest.mark.anyio
async def test_switch_pinned_by_an_earlier_release_connects_without_a_write(tmp_path):
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
async def test_changed_key_is_refused_before_credentials_with_both_fingerprints(tmp_path):
    original, replacement, events = (asyncssh.generate_private_key("ssh-ed25519"),
                                     asyncssh.generate_private_key("ssh-ed25519"), [])
    store = HostKeyStore(tmp_path / "known_switches.json")
    async with ssh_server(original, events) as port:
        transport = ssh(port, store)
        await transport.connect()
        await transport.close()
    first_login = list(events)
    async with ssh_server(replacement, events, port):
        transport = ssh(port, store)
        with pytest.raises(HostKeyMismatchError) as error:
            await transport.connect()
    assert error.value.presented == replacement.get_fingerprint("sha256")
    assert error.value.pinned == original.get_fingerprint("sha256")
    # Neither the username nor the password reached the device with the changed key.
    assert events == first_login and not transport.connected
    assert store.get("127.0.0.1", port) == original.get_fingerprint("sha256")
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
    assert store.accept(HOST, 22, FP_A) is True
    saved = store.path.read_bytes()
    with pytest.raises(HostKeyMismatchError) as error:
        store.accept(HOST, 22, FP_B)
    assert (error.value.presented, error.value.pinned) == (FP_B, FP_A)
    assert store.accept(HOST, 22, FP_A) is False
    assert store.path.read_bytes() == saved


@pytest.mark.anyio
async def test_unreadable_store_fails_before_any_network_io(tmp_path, monkeypatch):
    path = tmp_path / "known_switches.json"
    path.write_text("not-json", "utf-8")
    monkeypatch.setattr(asyncssh, "connect", lambda **kwargs: pytest.fail("the switch must not be contacted"))
    with pytest.raises(TransportError) as error:
        await SSHTransport(HOST, host_keys=HostKeyStore(path), username="fixture", password="fixture").connect()
    assert not isinstance(error.value, HostKeyMismatchError)


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
async def test_transport_without_a_store_sends_no_credentials(monkeypatch):
    monkeypatch.setattr(asyncssh, "connect", handshake(FakeKey(FP_A)))
    with pytest.raises(TransportError) as error:
        await SSHTransport(HOST, username="fixture", password="fixture").connect()
    assert not isinstance(error.value, HostKeyMismatchError)


@pytest.mark.anyio
async def test_key_replaced_during_authentication_is_not_adopted(tmp_path, monkeypatch):
    store = HostKeyStore(tmp_path / "known_switches.json")
    store.accept(HOST, 22, FP_A)
    connection = FakeConnection(FakeKey(FP_A))
    monkeypatch.setattr(asyncssh, "connect", handshake(FakeKey(FP_A), after=lambda: store.replace(HOST, 22, FP_A, FP_B),
                                                       connection=connection))
    transport = SSHTransport(HOST, host_keys=store, username="fixture", password="fixture")
    with pytest.raises(HostKeyMismatchError) as error:
        await transport.connect()
    assert (error.value.presented, error.value.pinned) == (FP_A, FP_B)
    assert connection.closed and not transport.connected and transport.host_key_fingerprint is None
    assert store.get(HOST, 22) == FP_B and store.presented(HOST, 22) == FP_A


@pytest.mark.anyio
async def test_first_key_never_overwrites_a_key_saved_during_authentication(tmp_path, monkeypatch):
    store = HostKeyStore(tmp_path / "known_switches.json")
    connection = FakeConnection(FakeKey(FP_A))
    monkeypatch.setattr(asyncssh, "connect", handshake(FakeKey(FP_A), after=lambda: store.accept(HOST, 22, FP_B),
                                                       connection=connection))
    transport = SSHTransport(HOST, host_keys=store, username="fixture", password="fixture")
    with pytest.raises(HostKeyMismatchError) as error:
        await transport.connect()
    assert (error.value.presented, error.value.pinned) == (FP_A, FP_B)
    assert connection.closed and not transport.connected and store.get(HOST, 22) == FP_B


@pytest.mark.anyio
async def test_first_key_that_cannot_be_saved_ends_the_connection(tmp_path, monkeypatch):
    store = HostKeyStore(tmp_path / "known_switches.json")
    connection = FakeConnection(FakeKey(FP_A))

    def fail(self, data):
        raise OSError("fixture storage failure")

    monkeypatch.setattr(HostKeyStore, "_save", fail)
    monkeypatch.setattr(asyncssh, "connect", handshake(FakeKey(FP_A), connection=connection))
    transport = SSHTransport(HOST, host_keys=store, username="fixture", password="fixture")
    with pytest.raises(TransportError, match="could not be saved") as error:
        await transport.connect()
    assert not isinstance(error.value, HostKeyMismatchError)
    assert connection.closed and not transport.connected and transport.host_key_fingerprint is None


@pytest.mark.anyio
async def test_rejection_without_the_callback_matches_the_store(tmp_path, monkeypatch):
    async def refuse(**kwargs):
        raise asyncssh.HostKeyNotVerifiable("host certificate")

    monkeypatch.setattr(asyncssh, "connect", refuse)
    store = HostKeyStore(tmp_path / "known_switches.json")
    with pytest.raises(TransportError) as unknown:
        await SSHTransport(HOST, host_keys=store, username="fixture", password="fixture").connect()
    assert not isinstance(unknown.value, HostKeyMismatchError) and not store.path.exists()
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
    with pytest.raises(TransportError):
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
    """The simulator behind a key check that works as SSHTransport's: an address without a saved key saves the
    presented one after the login, and a different saved key stops connect before it."""

    def __init__(self, store, host, port, presented):
        self.store, self.host, self.port, self.presented = store, host.strip(), port, presented
        self.simulator = SimulatedTransport(latency=0)
        self.host_key_fingerprint = self.host_key_type = None

    def __getattr__(self, name):
        return getattr(self.simulator, name)

    async def connect(self):
        pinned = self.store.get(self.host, self.port)
        if pinned is not None and pinned != self.presented:
            self.store.note_presented(self.host, self.port, self.presented)
            raise HostKeyMismatchError("fixture", presented=self.presented, presented_type="ssh-rsa", pinned=pinned)
        self.store.clear_presented(self.host, self.port)
        await self.simulator.connect()
        self.store.accept(self.host, self.port, self.presented)
        self.host_key_fingerprint, self.host_key_type = self.presented, "ssh-ed25519"

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


def changed(saved=FP_A, new=FP_B):
    return {"code": "host_key_changed", "message": CHANGED_MESSAGE, "host": HOST, "port": 22, "ssh_host_key": new,
            "ssh_host_key_type": "ssh-rsa", "saved_ssh_host_key": saved}


def save_playback(config, record):
    PlaybackRecord(config.data_dir / "switch-playback.json").save(record)
    return (config.data_dir / "switch-playback.json").read_bytes()


def log_in_elsewhere(client, app):
    """Connect to OTHER, a switch that is not the simulator, up to its lighting choice."""
    return client.post("/api/session/login", json={**LOGIN, "host": OTHER}).json()["pending_scene_choice"]


def choose_new_lighting(client, pending):
    return client.post("/api/session/scene-choice", json={"connection_id": pending["connection_id"],
                       "choice": "start_new", "legacy_revision": pending["legacy_revision"]}).json()


def test_login_saves_the_key_of_a_new_switch_and_shows_it(client, app):
    session = client.get("/api/session").json()
    assert (session["ssh_host_key"], session["ssh_host_key_type"]) == (None, None)
    response = client.post("/api/session/login", json=LOGIN)
    assert response.status_code == 200 and response.json()["pending_scene_choice"]
    assert app.state.sessions.host_keys.get(HOST, 22) == FP_A
    connected = choose_new_lighting(client, response.json()["pending_scene_choice"])
    assert connected["logged_in"] and connected["error"] is None
    session = client.get("/api/session").json()
    assert (session["ssh_host_key"], session["ssh_host_key_type"]) == (FP_A, "ssh-ed25519")


def test_the_demo_shows_no_ssh_key(config):
    with TestClient(create_app(config, autoconnect=False), base_url=BASE, client=LOCAL) as client:
        session = client.post("/api/session/login", json={"host": "demo"}).json()
    assert session["logged_in"] and session["simulated"]
    assert session["ssh_host_key"] is None and session["ssh_host_key_type"] is None


def test_login_reports_a_changed_key_with_saved_and_new_fingerprints(client, app, keys):
    app.state.sessions.host_keys.accept(HOST, 22, FP_A)
    keys[HOST] = FP_B
    response = client.post("/api/session/login", json=LOGIN)
    assert response.status_code == 409 and response.json()["detail"] == changed()
    assert client.get("/api/session").json()["error"] == changed()
    assert app.state.sessions.host_keys.get(HOST, 22) == FP_A
    assert choose_new_lighting(client, log_in_elsewhere(client, app))["logged_in"]
    assert client.post("/api/session/login", json=LOGIN).status_code == 409
    session = client.get("/api/session").json()
    assert session["logged_in"] and session["host"] == OTHER and session["error"] == changed()


def test_first_use_approval_routes_are_gone(client, app):
    app.state.sessions.host_keys.accept(HOST, 22, FP_A)
    for path in ["/api/session/host-key/accept", "/api/session/forget-host-key"]:
        response = client.post(path, json={"host": HOST, "port": 22, "ssh_host_key": FP_B})
        assert response.status_code == 404, path
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
    assert store.get(HOST, 22) == FP_B and store.presented(HOST, 22) is None
    writes = []
    monkeypatch.setattr(HostKeyStore, "_save", lambda self, data: writes.append(data))
    assert client.post(REPLACE, json=body).status_code == 200 and writes == []
    assert client.post("/api/session/login", json=LOGIN).json()["pending_scene_choice"]


def test_replace_needs_a_saved_key(client, app):
    store = app.state.sessions.host_keys
    store.note_presented(HOST, 22, FP_B)
    response = client.post(REPLACE, json=REPLACE_BODY)
    assert response.status_code == 409 and response.json()["detail"]["code"] == "host_key_stale"
    assert not store.path.exists()


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


def test_replace_is_refused_for_callers_off_this_computer(app):
    store = app.state.sessions.host_keys
    store.accept(HOST, 22, FP_A)
    store.note_presented(HOST, 22, FP_B)
    for caller in [("100.64.0.5", 50000), ("testclient", 50000), ("::ffff:100.64.0.5", 50000)]:
        with TestClient(app, base_url=BASE, client=caller) as remote:
            for request in (REPLACE_BODY, {**REPLACE_BODY, "port": "22"}):
                response = remote.post(REPLACE, json=request)
                assert response.status_code == 403
                assert response.json()["detail"] == {"code": "local_access_required",
                                                     "message": "Manage this setting on the computer running EE."}
    assert store.get(HOST, 22) == FP_A
    for caller in [("::1", 50000), ("::ffff:127.0.0.1", 50000)]:
        with TestClient(app, base_url=BASE, client=caller) as local:
            response = local.post(REPLACE, json={**REPLACE_BODY, "saved_ssh_host_key": FP_C})
            assert response.status_code == 409 and response.json()["detail"]["code"] == "host_key_stale"
    with TestClient(app, base_url=BASE, client=LOCAL) as local:
        assert local.post(REPLACE, json=REPLACE_BODY).status_code == 200
    assert store.get(HOST, 22) == FP_B


def test_replace_rejects_malformed_input(client):
    for change in [{"ssh_host_key": FP_A[:-1]}, {"saved_ssh_host_key": "MD5:" + "A" * 43}, {"port": 0},
                   {"port": "22"}, {"port": True}, {"port": 22.5}, {"force": True}, {"host": ""}]:
        response = client.post(REPLACE, json={**REPLACE_BODY, **change})
        assert response.status_code == 422, change
        assert response.json()["detail"] == {"code": "invalid_credentials",
                                             "message": "Check the connection fields and try again."}
    for field in ["ssh_host_key", "saved_ssh_host_key"]:
        missing = {key: value for key, value in REPLACE_BODY.items() if key != field}
        assert client.post(REPLACE, json=missing).status_code == 422
    blank = client.post(REPLACE, json={**REPLACE_BODY, "host": "   "})
    assert blank.status_code == 400 and blank.json()["detail"]["code"] == "missing_host"


def test_unreadable_saved_keys_are_reported_without_a_write(client, app):
    store = app.state.sessions.host_keys
    store.path.parent.mkdir(parents=True, exist_ok=True)
    store.path.write_text("not-json", "utf-8")
    store.note_presented(HOST, 22, FP_B)
    response = client.post(REPLACE, json=REPLACE_BODY)
    assert response.status_code == 500 and response.json()["detail"]["code"] == "host_key_save_failed"
    assert store.path.read_text("utf-8") == "not-json"


def test_saved_login_restore_saves_the_key_of_a_new_switch(config, keys, saved_login):
    app = make_app(config, keys)
    with TestClient(app, base_url=BASE, client=LOCAL) as client:
        session = client.post("/api/session/restore?resume=true").json()
    assert session["pending_scene_choice"] and session["error"] is None
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
async def test_automatic_login_saves_the_key_of_a_new_switch(config, keys, saved_login):
    sessions = manager(config, keys)
    try:
        await sessions.auto_connect()
        assert sessions.info()["error"] is None and not sessions.info()["connecting"]
        assert sessions.host_keys.get(HOST, 22) == FP_A
    finally:
        await sessions.close()


@pytest.mark.anyio
async def test_automatic_login_leaves_a_changed_key_for_the_connect_page(config, keys, saved_login, caplog):
    HostKeyStore(config.data_dir / "known_switches.json").accept(HOST, 22, FP_A)
    keys[HOST] = FP_B
    sessions = manager(config, keys)
    try:
        await sessions.auto_connect()
        assert sessions.info()["error"] == changed()
        assert not sessions.info()["logged_in"] and not sessions.info()["connecting"]
        assert f"Presented key: {FP_B}." in caplog.text
    finally:
        await sessions.close()


@pytest.mark.anyio
async def test_replace_publishes_a_session_message(config, keys):
    HostKeyStore(config.data_dir / "known_switches.json").accept(HOST, 22, FP_A)
    keys[HOST] = FP_B
    sessions = manager(config, keys)
    try:
        with pytest.raises(LoginError) as error:
            await sessions.connect(HOST, "fixture", "fixture-only")
        assert error.value.detail() == changed()
        mailbox = sessions.listen()
        await sessions.replace_host_key(HOST, 22, FP_A, FP_B)
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
