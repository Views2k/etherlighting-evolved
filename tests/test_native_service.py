"""Native host authentication and isolated child-process lifecycle."""

import json
import os
import queue
import subprocess
import sys
import threading
import urllib.error
import urllib.request
from pathlib import Path

import pytest
from fastapi.testclient import TestClient
from starlette.websockets import WebSocketDisconnect

from etherlighting_evolved.api import create_app


@pytest.mark.parametrize("headers,query,allowed", [
    ({"Authorization": "Bearer native-test-token"}, "", True),
    ({}, "?token=native-test-token", False),
    ({}, "", False),
    ({"Authorization": "Bearer wrong"}, "?token=native-test-token", False),
])
def test_native_websocket_header(config, headers, query, allowed):
    config.api.token = "native-test-token"
    with TestClient(create_app(config, autoconnect=False), base_url="http://127.0.0.1:8787") as client:
        if allowed:
            with client.websocket_connect("/api/ws" + query, headers={"Host": "127.0.0.1:8787", **headers}) as ws:
                assert ws.receive_json()["type"] == "session"
        else:
            with (
                pytest.raises(WebSocketDisconnect) as error,
                client.websocket_connect("/api/ws" + query, headers={"Host": "127.0.0.1:8787", **headers}),
            ):
                pass
            assert error.value.code == 4401


def test_native_service_demo_and_parent_eof(tmp_path):
    root = Path(__file__).resolve().parents[1]
    env = {**os.environ, "PYTHONPATH": str(root / "src"), "PYTHONUNBUFFERED": "1",
           "EE_HOST": "must-not-connect.invalid", "EE_DATA_DIR": str(tmp_path / "must-not-use")}
    process = subprocess.Popen(
        [sys.executable, str(root / "packaging" / "native_service.py")],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        text=True, env=env, creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
    )
    token = "native-lifecycle-test-token-not-a-secret"
    try:
        process.stdin.write(json.dumps({"token": token, "demo": True, "data_dir": str(tmp_path / "demo")}) + "\n")
        process.stdin.flush()
        replies = queue.Queue()
        threading.Thread(target=lambda: replies.put(process.stdout.readline()), daemon=True).start()
        ready = json.loads(replies.get(timeout=15))
        assert ready["ready"] is True
        base = f"http://127.0.0.1:{ready['port']}/api/"
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        with pytest.raises(urllib.error.HTTPError) as error:
            opener.open(base + "session", timeout=5)
        assert error.value.code == 401
        request = urllib.request.Request(base + "session", headers={"Authorization": f"Bearer {token}"})
        with opener.open(request, timeout=5) as reply:
            session = json.load(reply)
        assert session["simulated"] is True
        assert session["logged_in"] is True
        assert session["has_saved_login"] is False
        assert not (tmp_path / "must-not-use").exists()
        request = urllib.request.Request(
            base + "session/login",
            data=json.dumps({"host": "must-not-connect.invalid", "username": "fixture"}).encode(),
            headers={"Authorization": f"Bearer {token}", "Content-Type": "application/json"}, method="POST",
        )
        with pytest.raises(urllib.error.HTTPError) as error:
            opener.open(request, timeout=5)
        assert error.value.code == 409
        assert json.load(error.value)["detail"]["code"] == "preview_only"
        process.stdin.close()
        assert process.wait(timeout=20) == 0
        assert token not in process.stdout.read()
        assert process.stderr.read() == ""
    finally:
        if process.poll() is None:
            process.kill()  # Only this test's isolated simulator subprocess.
            process.wait(timeout=5)
