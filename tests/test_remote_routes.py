"""preview/remote-routes.mjs lists what devices on Tailscale may reach through the private gateway.

The gateway refuses every route without a remote entry, so a route missing from the list fails here instead of
silently becoming unavailable on those devices.
"""

import json
import re
from pathlib import Path

import pytest
from fastapi.routing import APIRoute, APIWebSocketRoute, iter_route_contexts
from fastapi.testclient import TestClient

from etherlighting_evolved.api import create_app
from etherlighting_evolved.routes.ws import WS_COMMANDS

ROOT = Path(__file__).resolve().parents[1]
ROUTES_FILE = ROOT / "preview" / "remote-routes.mjs"
API_REFERENCE = ROOT / "docs" / "API.md"
PREFIX = "export const ROUTE_ACCESS = "
# Local-only host-key routes that are listed before the engine has them.
PENDING_ENGINE_ROUTES = {("POST", "/api/session/host-key/accept"), ("POST", "/api/session/host-key/replace")}
METHOD_ORDER = ["GET", "POST", "PUT", "PATCH", "DELETE", "WS"]
OWNER_LOCAL = {
    ("POST", "/api/session/login"), ("POST", "/api/session/forget-host-key"),
    ("POST", "/api/session/host-key/accept"), ("POST", "/api/session/host-key/replace"),
    ("POST", "/api/session/forget"), ("POST", "/api/session/logout"),
    ("POST", "/api/controller/connect"), ("POST", "/api/controller/consoles"), ("POST", "/api/controller/disconnect"),
    ("POST", "/api/controller/cancel"), ("PUT", "/api/controller/preferences"),
    ("PUT", "/api/private-access"), ("POST", "/api/private-access/refresh"), ("POST", "/api/private-access/login"),
    ("POST", "/api/workspace/close"),
}
CLIENT_ROUTES = {
    ("GET", "/api/effects"), ("GET", "/api/session"), ("GET", "/api/state"), ("GET", "/api/ports"),
    ("GET", "/api/scene"), ("PUT", "/api/scene"), ("POST", "/api/session/restore"),
    ("POST", "/api/session/scene-choice"), ("POST", "/api/session/recover"), ("POST", "/api/session/handoff"),
    ("GET", "/api/profiles"), ("PATCH", "/api/profiles/{profile_id}"), ("GET", "/api/presets"),
    ("POST", "/api/presets"), ("GET", "/api/schedules"), ("POST", "/api/schedules"),
    ("PATCH", "/api/schedules/{schedule_id}"), ("DELETE", "/api/schedules/{schedule_id}"),
    ("POST", "/api/schedules/{schedule_id}/run"), ("GET", "/api/rules/fields"), ("GET", "/api/rules/templates"),
    ("POST", "/api/rules/test"), ("PUT", "/api/brightness"), ("PUT", "/api/zones"),
    ("PUT", "/api/zones/{zone_id}/effect"), ("PUT", "/api/ports"), ("DELETE", "/api/ports/{port}"),
    ("PUT", "/api/sfp/{port}"), ("PUT", "/api/sfp/sync"), ("POST", "/api/release"), ("POST", "/api/resume"),
    ("POST", "/api/identify"), ("DELETE", "/api/identify"), ("GET", "/api/history"),
    ("POST", "/api/history/{entry_id}/restore"), ("GET", "/api/library/{kind}"), ("POST", "/api/library/{kind}"),
    ("PUT", "/api/library/{kind}/{item_id}"), ("DELETE", "/api/library/{kind}/{item_id}"),
    ("GET", "/api/port-names"), ("PUT", "/api/port-names"), ("GET", "/api/fans"), ("PUT", "/api/fans"),
    ("GET", "/api/last-session"), ("GET", "/api/private-access"), ("GET", "/api/workspace"), ("WS", "/api/ws"),
}


def load_access():
    text = ROUTES_FILE.read_text("utf-8")
    assert text.startswith(PREFIX) and text.endswith(";\n"), \
        "preview/remote-routes.mjs must be `export const ROUTE_ACCESS = <JSON>;`"
    return json.loads(text[len(PREFIX):-2])


def engine_routes(config):
    # The router is read without running the app's lifespan, so nothing connects.
    app = create_app(config, autoconnect=False)
    pairs = set()
    for context in iter_route_contexts(app.routes):
        path = context.path or ""
        if path != "/api" and not path.startswith("/api/"):
            continue
        route = context.original_route
        if isinstance(route, APIRoute):
            pairs |= {(method, path) for method in context.methods}
        elif isinstance(route, APIWebSocketRoute):
            pairs.add(("WS", path))
        else:
            pytest.fail("Routes under /api must be FastAPI routes so remote access can list them.")
    return pairs


def pairs(routes, **match):
    return {(route["method"], route["path"]) for route in routes
            if all(route[key] == value for key, value in match.items())}


def test_every_engine_route_is_classified(config):
    missing = engine_routes(config) - pairs(load_access()["routes"], source="engine")
    assert not missing, ("Add these routes to preview/remote-routes.mjs with remote true or false and a reason: "
                         + ", ".join(f"{method} {path}" for method, path in sorted(missing)))


def test_no_stale_engine_entries(config):
    # A remote entry for a route the engine no longer has would open a later route with the same path. A local entry
    # under a remote placeholder refuses requests that the placeholder would forward.
    stale = pairs(load_access()["routes"], source="engine") - engine_routes(config) - PENDING_ENGINE_ROUTES
    assert not stale, "Delete these rows from preview/remote-routes.mjs: " + ", ".join(
        f"{method} {path}" for method, path in sorted(stale))


def test_api_reference_lists_the_local_routes():
    section = API_REFERENCE.read_text("utf-8").split("These requests are local-only:\n\n", 1)[1].split("\n\n", 1)[0]
    listed = set(re.findall(r"^\| `([A-Z]+) (/api/[^`]+)` \|", section, re.MULTILINE))
    assert listed == pairs(load_access()["routes"], remote=False)


def test_entries_are_well_formed():
    access = load_access()
    assert set(access) == {"static", "routes"}
    assert set(access["static"]) == {"methods", "reason"} and access["static"]["methods"] == ["GET", "HEAD"]
    assert isinstance(access["static"]["reason"], str) and access["static"]["reason"]
    routes = access["routes"]
    for route in routes:
        assert set(route) == {"method", "path", "source", "remote", "reason"}, route
        assert route["method"] in METHOD_ORDER, route
        assert route["source"] in {"engine", "gateway"}, route
        assert route["method"] != "WS" or route["source"] == "engine", route
        assert isinstance(route["remote"], bool), route
        assert isinstance(route["reason"], str) and 0 < len(route["reason"]) <= 160, route
        assert re.fullmatch(r"/api(/([A-Za-z0-9._-]+|\{[a-z_]+\}))+", route["path"]), route
    keys = [(route["method"], route["path"]) for route in routes]
    assert len(keys) == len(set(keys)), "A method and path appear more than once"
    assert not pairs(routes, source="gateway") & pairs(routes, source="engine")
    assert routes == sorted(routes, key=lambda route: (route["path"], METHOD_ORDER.index(route["method"])))


def test_owner_local_routes_stay_local():
    assert not OWNER_LOCAL & pairs(load_access()["routes"], remote=True)


def test_client_routes_stay_remote():
    missing = CLIENT_ROUTES - pairs(load_access()["routes"], remote=True)
    assert not missing, sorted(missing)


def test_websocket_commands_are_pinned():
    assert {"ping", "set_port", "set_ports", "clear_port", "set_effect", "set_params", "update_rule",
            "set_brightness", "release", "resume"} == WS_COMMANDS, (
        "WebSocket commands reach devices on Tailscale. A command that must stay on the host computer needs a REST "
        "route.")


def test_unlisted_websocket_command_is_rejected(config):
    with TestClient(create_app(config, autoconnect=False), base_url="http://127.0.0.1:8787") as client:
        assert client.post("/api/session/login", json={"host": "demo"}).status_code == 200
        with client.websocket_connect("/api/ws", headers={"Host": "127.0.0.1:8787"}) as ws:
            assert {ws.receive_json()["type"] for _ in range(3)} == {"session", "state", "frame"}

            def reply(kind, number):
                for _ in range(50):
                    message = ws.receive_json()
                    if message["type"] == kind and message.get("id") == number:
                        return message
                pytest.fail(f"no {kind} for message {number}")

            for number, kind in ((1, "unlisted"), (2, ["x"])):
                ws.send_json({"type": kind, "id": number})
                error = reply("error", number)
                assert error["code"] == "invalid" and "unknown message type" in error["message"]
            ws.send_json({"type": "ping", "id": 3})
            reply("pong", 3)
