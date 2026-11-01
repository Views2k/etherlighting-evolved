import asyncio
import gc
from contextlib import asynccontextmanager
from weakref import ref

import pytest
from starlette.datastructures import Headers
from starlette.websockets import WebSocketDisconnect

from etherlighting_evolved import api
from etherlighting_evolved.mailbox import Mailbox

TOKEN = "websocket-target-fixture-token"
WRITES = [
    {"type": "set_port", "port": 1, "color": "#123456"},
    {"type": "set_ports", "ports": {"1": {"color": "#123456"}}},
    {"type": "clear_port", "port": 1},
    {"type": "set_effect", "id": "breathing", "params": {}},
    {"type": "set_params", "params": {"speed": 2}},
    {"type": "update_rule", "id": "fixture-rule", "changes": {"enabled": False}},
    {"type": "set_brightness", "value": 25},
    {"type": "release"},
    {"type": "resume"},
]


class RecordingEngine:
    def __init__(self, *, connected=True):
        self.connected = connected
        self.calls = []

    def state_message(self):
        return {"type": "state", "state": {"sfp": [], "connected": self.connected}}

    def frame_message(self):
        return {"type": "frame", "ports": [], "sfp": [], "connected": self.connected}

    def __getattr__(self, name):
        if name not in {"set_override", "set_overrides", "clear_override", "set_effect", "update_params",
                        "update_rule", "set_brightness", "release", "resume"}:
            raise AttributeError(name)
        return lambda *args, **kwargs: self.calls.append((name, args, kwargs))

    async def wait_for_output_idle(self):
        pass


class Sessions:
    simulated = True
    host = "demo"
    handoff_pending = False

    def __init__(self):
        self.engine = RecordingEngine()
        self.workspace = "fixture-workspace"
        self.mailboxes = set()

    def workspace_id(self):
        return self.workspace if self.engine is not None else None

    def info(self):
        return {"logged_in": self.engine is not None, "workspace_id": self.workspace_id()}

    def listen(self):
        mailbox = Mailbox()
        self.mailboxes.add(mailbox)
        return mailbox

    def unlisten(self, mailbox):
        self.mailboxes.discard(mailbox)

    def notify(self):
        for mailbox in self.mailboxes:
            mailbox.put({"type": "session", "session": self.info()})
            if self.engine is not None:
                mailbox.put(self.engine.state_message())
                mailbox.put(self.engine.frame_message())


class Socket:
    def __init__(self, *, headers=None, query=None, on_accept=None):
        self.headers = Headers(headers if headers is not None else {"Authorization": f"Bearer {TOKEN}"})
        self.query_params = query or {}
        self.incoming = asyncio.Queue()
        self.outgoing = asyncio.Queue()
        self.on_accept = on_accept
        self.closed = None

    async def accept(self):
        if self.on_accept:
            self.on_accept()

    async def close(self, code):
        self.closed = code

    async def send_json(self, message):
        self.outgoing.put_nowait(message)

    async def receive_json(self):
        message = await self.incoming.get()
        if message is None:
            raise WebSocketDisconnect()
        return message

    async def read(self):
        return await asyncio.wait_for(self.outgoing.get(), 2)

    async def command(self, value):
        self.incoming.put_nowait({"id": "fixture-command", **value})
        return await self.read()


@pytest.fixture
def wired(config, monkeypatch):
    sessions = Sessions()
    config.api.token = TOKEN
    monkeypatch.setattr(api, "SessionManager", lambda *args, **kwargs: sessions)
    app = api.create_app(config, autoconnect=False)
    endpoint = next(route.endpoint for route in app.routes if getattr(route, "path", None) == "/api/ws")
    return sessions, endpoint


@asynccontextmanager
async def connected(wired, **kwargs):
    sessions, endpoint = wired
    socket = Socket(**kwargs)
    task = asyncio.create_task(endpoint(socket))
    try:
        assert (await socket.read())["type"] == "session"
        if sessions.engine is not None:
            assert (await socket.read())["type"] == "state"
            assert (await socket.read())["type"] == "frame"
        yield socket
    finally:
        socket.incoming.put_nowait(None)
        await asyncio.wait_for(task, 2)


@pytest.mark.anyio
@pytest.mark.parametrize("command", WRITES, ids=lambda command: command["type"])
async def test_same_address_replacement_rejects_every_writable_command(wired, command):
    sessions, _ = wired
    previous = sessions.engine
    async with connected(wired) as socket:
        sessions.engine = RecordingEngine()
        # A new login to the same device retains its browser-storage workspace ID.
        result = await socket.command({**command, "workspace_id": sessions.workspace})
        assert result["type"] == "error" and result["code"] == "workspace_changed"
        assert previous.calls == sessions.engine.calls == []
        assert (await socket.command({"type": "ping"}))["type"] == "pong"


@pytest.mark.anyio
async def test_current_session_keeps_legacy_commands_and_explicit_matching_marker(wired):
    sessions, _ = wired
    async with connected(wired) as socket:
        for command in WRITES:
            assert (await socket.command(command))["type"] == "ack"
        result = await socket.command({"type": "set_brightness", "value": 40,
                                       "workspace_id": sessions.workspace})
        assert result["type"] == "ack" and len(sessions.engine.calls) == len(WRITES) + 1


@pytest.mark.anyio
@pytest.mark.parametrize("initial", ["logged_out", "disconnected", "handoff"])
async def test_no_writable_target_at_open_never_acquires_one_implicitly(wired, initial):
    sessions, _ = wired
    if initial == "logged_out":
        sessions.engine = None
    elif initial == "disconnected":
        sessions.engine.connected = False
    else:
        sessions.handoff_pending = True
    async with connected(wired) as socket:
        assert (await socket.command({"type": "ping"}))["type"] == "pong"
        if sessions.engine is None:
            sessions.engine = RecordingEngine()
        sessions.engine.connected = True
        sessions.handoff_pending = False
        sessions.notify()
        assert {(await socket.read())["type"] for _ in range(3)} == {"session", "state", "frame"}
        result = await socket.command({"type": "set_brightness", "value": 25})
        assert result["code"] == "workspace_changed" and sessions.engine.calls == []


@pytest.mark.anyio
async def test_logout_and_new_socket_require_explicit_write_target_acquisition(wired):
    sessions, _ = wired
    async with connected(wired) as old_socket:
        sessions.engine = None
        assert (await old_socket.command({"type": "resume"}))["code"] == "not_logged_in"
        assert (await old_socket.command({"type": "ping"}))["type"] == "pong"
        sessions.engine = RecordingEngine()
        async with connected(wired) as new_socket:
            assert (await old_socket.command({"type": "resume"}))["code"] == "workspace_changed"
            assert (await new_socket.command({"type": "resume"}))["type"] == "ack"
        assert [name for name, _, _ in sessions.engine.calls] == ["resume"]
    assert not sessions.mailboxes


@pytest.mark.anyio
async def test_workspace_marker_change_rejects_even_if_engine_object_is_unchanged(wired):
    sessions, _ = wired
    async with connected(wired) as socket:
        sessions.workspace = "different-workspace"
        assert (await socket.command({"type": "resume"}))["code"] == "workspace_changed"
        assert sessions.engine.calls == []


@pytest.mark.anyio
async def test_stale_handshake_marker_does_not_acquire_current_target(wired):
    sessions, _ = wired
    headers = {"Authorization": f"Bearer {TOKEN}", "X-EE-Workspace": "previous-workspace"}
    async with connected(wired, headers=headers) as socket:
        assert (await socket.command({"type": "ping"}))["type"] == "pong"
        assert (await socket.command({"type": "resume"}))["code"] == "workspace_changed"
        assert sessions.engine.calls == []


@pytest.mark.anyio
@pytest.mark.parametrize("marker", [None, "other-workspace", {"unexpected": "object"}])
async def test_supplied_command_marker_must_match_and_cannot_retarget(wired, marker):
    sessions, _ = wired
    async with connected(wired) as socket:
        result = await socket.command({"type": "resume", "workspace_id": marker})
        assert result["code"] == "workspace_changed" and sessions.engine.calls == []
        assert (await socket.command({"type": "resume"}))["type"] == "ack"


@pytest.mark.anyio
async def test_session_replaced_while_accept_is_awaited_cannot_become_write_target(wired):
    sessions, _ = wired
    previous = sessions.engine
    async with connected(wired, on_accept=lambda: setattr(sessions, "engine", RecordingEngine())) as socket:
        assert (await socket.command({"type": "resume"}))["code"] == "workspace_changed"
        assert previous.calls == sessions.engine.calls == []


@pytest.mark.anyio
async def test_telemetry_only_socket_does_not_retain_a_replaced_engine(wired):
    sessions, _ = wired
    async with connected(wired) as socket:
        previous = ref(sessions.engine)
        sessions.engine = RecordingEngine()
        gc.collect()
        assert previous() is None
        assert (await socket.command({"type": "ping"}))["type"] == "pong"
        assert (await socket.command({"type": "resume"}))["code"] == "workspace_changed"


@pytest.mark.anyio
@pytest.mark.parametrize("headers,query", [({}, {}), ({"Authorization": "Bearer wrong"}, {"token": TOKEN})])
async def test_binding_does_not_weaken_authentication(wired, headers, query):
    sessions, endpoint = wired
    socket = Socket(headers=headers, query=query)
    await asyncio.wait_for(endpoint(socket), 2)
    assert socket.closed == 4401 and not sessions.mailboxes and sessions.engine.calls == []
