"""The WebSocket at /api/ws: session, state and frame messages, plus the quick lighting controls."""

from __future__ import annotations

import asyncio
import contextlib
import logging
from weakref import ref

from fastapi import FastAPI, WebSocket, WebSocketDisconnect

from ..engine import Engine, NotFound
from ..transport import TransportError
from .context import ApiContext, token_matches

log = logging.getLogger(__name__)


def handle_ws_command(engine: Engine, msg: dict) -> dict | None:
    """The quick, high-frequency controls. Everything else goes through REST."""
    kind = msg.get("type")
    if kind == "ping":
        return {"type": "pong", "id": msg.get("id")}
    if kind == "set_port":
        engine.set_override(msg["port"], msg["color"], msg.get("brightness"))
    elif kind == "set_ports":
        engine.set_overrides({int(k): v for k, v in msg["ports"].items()})
    elif kind == "clear_port":
        engine.clear_override(msg.get("port"))
    elif kind == "set_effect":
        clear = msg.get("clear_overrides", False)
        if not isinstance(clear, bool):
            raise ValueError("clear_overrides must be true or false")
        engine.set_effect(msg["id"], msg.get("params"), zone=msg.get("zone"), clear_overrides=clear)
    elif kind == "set_params":
        engine.update_params(msg["params"], zone=msg.get("zone"))
    elif kind == "update_rule":
        engine.update_rule(msg["id"], msg["changes"])
    elif kind == "set_brightness":
        engine.set_brightness(msg["value"])
    elif kind == "release":
        engine.release()
    elif kind == "resume":
        engine.resume()
    else:
        raise ValueError(f"unknown message type {kind!r}")
    return {"type": "ack", "id": msg["id"]} if msg.get("id") is not None else None


def attach(app: FastAPI, ctx: ApiContext) -> None:
    sessions, controller = ctx.sessions, ctx.controller

    @app.websocket("/api/ws")
    async def websocket(ws: WebSocket):
        token = ctx.config.api.token
        if token and not token_matches(ws.headers.get("authorization"), token):
            await ws.close(code=4401)
            return
        bound_engine = (ref(sessions.engine) if sessions.engine is not None and sessions.engine.connected
                        and not sessions.handoff_pending else None)
        bound_workspace = sessions.workspace_id()
        handshake_workspace = ws.headers.get("X-EE-Workspace")
        await ws.accept()
        mailbox = sessions.listen()
        mailbox.put({"type": "session", "session": sessions.info()})
        if sessions.engine is not None:
            mailbox.put(sessions.engine.state_message())
            mailbox.put(sessions.engine.frame_message())

        async def sender() -> None:
            while True:
                await ws.send_json(controller.message(await mailbox.get()))

        send_task = asyncio.create_task(sender())
        try:
            while True:
                msg = await ws.receive_json()
                if not isinstance(msg, dict):
                    mailbox.put({"type": "error", "code": "invalid", "message": "messages must be objects"})
                    continue
                current = None if sessions.handoff_pending else sessions.engine
                if current is None and msg.get("type") != "ping":
                    mailbox.put({"type": "error", "id": msg.get("id"), "code": "not_logged_in",
                                         "message": "log in to a switch first"})
                    continue
                if msg.get("type") != "ping" and (
                    bound_engine is None or current is not bound_engine()
                    or sessions.workspace_id() != bound_workspace
                    or handshake_workspace is not None and handshake_workspace != bound_workspace
                    or "workspace_id" in msg and msg["workspace_id"] != bound_workspace
                ):
                    mailbox.put({"type": "error", "id": msg.get("id"), "code": "workspace_changed",
                                 "message": "The switch session changed. Reopen this connection before "
                                            "changing lighting."})
                    continue
                try:
                    reply = handle_ws_command(current, msg) if current else {"type": "pong", "id": msg.get("id")}
                    if current and msg.get("type") == "release":
                        await current.wait_for_output_idle()
                except NotFound as exc:
                    reply = {"type": "error", "id": msg.get("id"), "code": "not_found", "message": str(exc)}
                except TransportError as exc:
                    reply = {"type": "error", "id": msg.get("id"), "code": "output_unconfirmed", "message": str(exc)}
                except (ValueError, KeyError, TypeError) as exc:
                    reply = {"type": "error", "id": msg.get("id"), "code": "invalid",
                             "message": str(exc).strip("'\"")}
                if reply:
                    mailbox.put(reply)
        except WebSocketDisconnect:
            pass
        except Exception:
            log.debug("websocket closed", exc_info=True)
        finally:
            send_task.cancel()
            with contextlib.suppress(asyncio.CancelledError, Exception):
                await send_task
            sessions.unlisten(mailbox)
