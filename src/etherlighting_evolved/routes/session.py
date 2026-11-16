"""Login, logout, saved logins, handoff and switch recovery."""

from __future__ import annotations

from fastapi import APIRouter, Depends
from pydantic import BaseModel, ConfigDict, Field

from .context import ApiContext, await_output, require_local_caller

SSH_FINGERPRINT = r"^SHA256:[A-Za-z0-9+/]{43}$"


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


class HostKeyAccept(BaseModel):
    """The key a client approves, echoed back exactly as the host_key_untrusted error reported it."""

    model_config = ConfigDict(extra="forbid", strict=True)
    host: str = Field(min_length=1, max_length=253)
    port: int = Field(default=22, ge=1, le=65535)
    ssh_host_key: str = Field(pattern=SSH_FINGERPRINT)


class HostKeyReplace(HostKeyAccept):
    saved_ssh_host_key: str = Field(pattern=SSH_FINGERPRINT)


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

    # Accepting a key decides where EE sends the switch password, so it is done only on the computer running EE.
    @api.post("/session/host-key/accept", dependencies=[Depends(require_local_caller)])
    async def accept_host_key(body: HostKeyAccept):
        return await sessions.accept_host_key(body.host, body.port, body.ssh_host_key)

    @api.post("/session/host-key/replace", dependencies=[Depends(require_local_caller)])
    async def replace_host_key(body: HostKeyReplace):
        return await sessions.replace_host_key(body.host, body.port, body.saved_ssh_host_key, body.ssh_host_key)

    @api.post("/session/recover")
    async def recover_connection():
        async with sessions.exclusive():
            e = ctx.engine()
            await await_output(e.recover_connection)
            return e.state()
