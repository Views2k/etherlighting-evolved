import asyncio

import pytest
from fastapi.testclient import TestClient

from etherlighting_evolved.api import create_app
from etherlighting_evolved.session import LoginError, SessionManager
from etherlighting_evolved.transport import SimulatedTransport, TransportError


class RecordingSimulator(SimulatedTransport):
    def __init__(self):
        super().__init__(latency=0)
        self.operations = []
        self.fail_initialize = False

    async def write_behavior(self, behavior):
        self.operations.append(("behavior", behavior))
        if self.fail_initialize:
            raise TransportError("baseline was not acknowledged")
        await super().write_behavior(behavior)

    async def run(self, command, **kwargs):
        if '"port_rgb":' in command:
            self.operations.append(("rgb", "port"))
        return await super().run(command, **kwargs)


@pytest.mark.anyio
async def test_production_login_initializes_before_any_color_and_sfp_acknowledgment(config):
    transport = RecordingSimulator()
    sessions = SessionManager(config, transport_factory=lambda **kwargs: transport)
    try:
        result = await sessions.connect("demo")
        assert result["connected"] is True
        assert transport.operations[0] == ("behavior", "steady")
        sessions.engine.set_sfp(9, {"mode": "color", "color": "blue"})
        await asyncio.wait_for(sessions.engine.wait_for_sfp(9), 2)
        assert sessions.engine.sfp_state()[0]["control"]["state"] == "active"
        assert transport.operations.count(("behavior", "steady")) == 1
        assert transport.sfp_modes[9] is True
    finally:
        await sessions.close()


@pytest.mark.anyio
async def test_failed_initialization_returns_login_error_and_closes_unused_connection(config):
    transport = RecordingSimulator()
    transport.fail_initialize = True
    sessions = SessionManager(config, transport_factory=lambda **kwargs: transport)
    with pytest.raises(LoginError) as error:
        await sessions.connect("demo")
    assert error.value.status == 409 and error.value.code == "output_unconfirmed"
    assert sessions.engine is None and not transport.connected
    assert not any(operation[0] == "rgb" for operation in transport.operations)
    assert sessions.info()["logged_in"] is False


@pytest.mark.anyio
async def test_failed_old_session_cleanup_also_closes_the_new_probed_session(config):
    transports = [RecordingSimulator(), RecordingSimulator()]
    factory = iter(transports)
    sessions = SessionManager(config, transport_factory=lambda **kwargs: next(factory))
    await sessions.connect("demo")
    old = sessions.engine
    stop = old.stop

    async def unconfirmed_stop():
        await stop()
        raise TransportError("old output cleanup was not acknowledged")

    old.stop = unconfirmed_stop
    with pytest.raises(LoginError) as error:
        await sessions.connect("demo")
    assert error.value.status == 409
    assert sessions.engine is None and sessions.host is None and sessions.username is None
    assert all(not transport.connected for transport in transports)
    assert not transports[1].operations  # probing a replacement never started output


@pytest.mark.anyio
async def test_disconnect_reports_failed_cleanup_and_clears_session_identity(config):
    sessions = SessionManager(config)
    await sessions.connect("demo")
    engine = sessions.engine
    stop = engine.stop

    async def unconfirmed_stop():
        await stop()
        raise TransportError("output cleanup was not acknowledged")

    engine.stop = unconfirmed_stop
    with pytest.raises(LoginError) as error:
        await sessions.disconnect()
    assert error.value.status == 409
    state = sessions.info()
    assert not state["logged_in"] and not state["connected"]
    assert state["host"] is None and state["username"] is None and state["simulated"] is False
    assert state["error"]["code"] == "output_unconfirmed"


@pytest.mark.anyio
async def test_canceling_disconnect_still_waits_for_owned_output_cleanup(config):
    sessions = SessionManager(config)
    await sessions.connect("demo")
    engine = sessions.engine
    stop = engine.stop
    entered, complete = asyncio.Event(), asyncio.Event()

    async def delayed_stop():
        entered.set()
        await complete.wait()
        await stop()

    engine.stop = delayed_stop
    disconnect = asyncio.create_task(sessions.disconnect())
    await asyncio.wait_for(entered.wait(), 1)
    disconnect.cancel()
    await asyncio.sleep(0)
    assert not disconnect.done()
    complete.set()
    with pytest.raises(asyncio.CancelledError):
        await disconnect
    assert not engine.switch.transport.connected and not engine._tasks
    assert sessions.engine is None and sessions.host is None


def test_api_reports_pending_intent_while_released_then_acknowledges_after_resume(config):
    app = create_app(config, autoconnect=False)
    with TestClient(app, base_url="http://127.0.0.1:8787") as client:
        assert client.post("/api/session/login", json={"host": "demo"}).status_code == 200
        assert client.post("/api/release").status_code == 200
        response = client.put("/api/sfp/9", json={"mode": "color", "color": "blue"})
        assert response.status_code == 200
        assert response.json()["sfp"][0]["control"]["state"] == "pending"
        assert client.post("/api/resume").status_code == 200
        response = client.put("/api/sfp/9", json={"mode": "color", "color": "blue"})
        assert response.status_code == 200, response.text
        state = response.json()["sfp"][0]
        assert state["control"]["state"] == "active" and state["control"]["owned"]
        assert state["revision"] == state["acknowledged_revision"]


def test_http_and_websocket_output_failures_are_errors_instead_of_acknowledgments(config, monkeypatch):
    app = create_app(config, autoconnect=False)
    with TestClient(app, base_url="http://127.0.0.1:8787") as client:
        assert client.post("/api/session/login", json={"host": "demo"}).status_code == 200
        engine = app.state.sessions.engine

        async def unconfirmed(*args):
            raise TransportError("output was not acknowledged")

        with monkeypatch.context() as patch:
            patch.setattr(engine, "wait_for_sfp", unconfirmed)
            patch.setattr(engine, "wait_for_output_idle", unconfirmed)
            for route, method, data in [("/api/sfp/9", "put", {"mode": "color"}),
                                        ("/api/release", "post", None)]:
                response = getattr(client, method)(route, json=data)
                assert response.status_code == 409
                assert response.json()["detail"]["code"] == "output_unconfirmed"
            with client.websocket_connect("/api/ws", headers={"Host": "127.0.0.1:8787"}) as websocket:
                websocket.send_json({"type": "release", "id": "release-check"})
                for _ in range(20):
                    response = websocket.receive_json()
                    if response.get("id") == "release-check":
                        break
                assert response["type"] == "error" and response["code"] == "output_unconfirmed"
        with monkeypatch.context() as patch:
            patch.setattr(engine, "_native_blocked", True)
            response = client.post("/api/resume")
            assert response.status_code == 409
            assert response.json()["detail"]["code"] == "output_unconfirmed"
