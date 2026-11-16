"""Login, logout, saved logins, handoff and switch recovery."""

from __future__ import annotations

from fastapi import APIRouter
from pydantic import BaseModel, Field

from .context import ApiContext, await_output


class LoginRequest(BaseModel):
    host: str = Field(max_length=253, description='Switch IP or hostname. "demo" logs in to the built-in simulator.')
    username: str = Field(default="", max_length=256)
    password: str = Field(default="", max_length=4096, repr=False)
    # SessionManager.connect checks the range, so a bad port gets its own error code.
    port: int = 22
    remember: bool = False


class SceneChoiceRequest(BaseModel):
    connection_id: str = Field(min_length=1, max_length=128)
    choice: str
    legacy_revision: str = Field(default="", max_length=128)


class HostKeyForget(BaseModel):
    host: str = Field(max_length=253)
    port: int = 22


def attach(api: APIRouter, ctx: ApiContext) -> None:
    sessions = ctx.sessions

    @api.get("/session")
    async def session_info():
        return sessions.info()

    @api.post("/session/login")
    async def login(body: LoginRequest):
        return await sessions.connect(body.host, body.username, body.password, port=body.port,
                                      remember=body.remember)

    @api.post("/session/scene-choice")
    async def scene_choice(body: SceneChoiceRequest):
        return await sessions.choose_scene(body.connection_id, body.choice, body.legacy_revision)

    @api.post("/session/logout")
    async def logout(forget: bool = False):
        ctx.require_workspace()
        return await sessions.disconnect(forget=forget)

    @api.post("/session/restore")
    async def restore_saved_login(resume: bool = False):
        return await sessions.restore_saved(resume=resume)

    @api.post("/session/handoff")
    async def handoff_to_unifi():
        ctx.require_workspace()
        return await sessions.handoff()

    @api.post("/session/forget")
    async def forget_saved_login():
        return await sessions.forget_saved_login()

    @api.post("/session/forget-host-key")
    async def forget_host_key(body: HostKeyForget):
        return {"forgotten": sessions.forget_host_key(body.host, body.port)}

    @api.post("/session/recover")
    async def recover_connection():
        async with sessions.exclusive():
            e = ctx.engine()
            await await_output(e.recover_connection)
            return e.state()
