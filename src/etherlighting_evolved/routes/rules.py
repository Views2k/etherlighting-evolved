"""Automation rules and the rule tester."""

from __future__ import annotations

from datetime import datetime
from typing import Any

from fastapi import APIRouter, Body, HTTPException
from pydantic import BaseModel, Field

from .context import MAX_RULES, ApiContext, apply, limit_rules, scene_envelope


class RulesRequest(BaseModel):
    rules: list[dict[str, Any]] = Field(max_length=MAX_RULES)


class OrderRequest(BaseModel):
    ids: list[str] = Field(max_length=MAX_RULES)


class RuleTest(BaseModel):
    port: int = Field(ge=1, le=256)
    rules: list[dict[str, Any]] = Field(max_length=MAX_RULES)
    at: str | None = None


def attach(api: APIRouter, ctx: ApiContext) -> None:
    engine = ctx.engine

    @api.post("/rules/test")
    async def test_rules(body: RuleTest):
        e = engine()
        if body.port not in [*range(1, e.num_ports + 1), *e.switch.sfp_ports]:
            raise HTTPException(400, detail={"code": "invalid", "message": "Choose a port on this switch."})
        now = apply(datetime.fromisoformat, body.at) if body.at else datetime.now()
        if now.tzinfo is not None:
            raise HTTPException(400, detail={"code": "invalid", "message":
                "Use the EE computer's local date and time."})
        return {**apply(e.explain_rules, body.port, body.rules, now),
                "scene_revision": scene_envelope(e.snapshot())["revision"]}

    @api.get("/rules")
    async def rules():
        return engine().state()["rules"]

    @api.put("/rules")
    async def replace_rules(body: RulesRequest):
        e = engine()
        apply(e.set_rules, body.rules)
        return e.state()["rules"]

    @api.post("/rules", status_code=201)
    async def add_rule(rule: dict[str, Any] = Body(...)):
        e = engine()
        limit_rules([*e.rules, rule])
        position = rule.pop("position", None)
        return apply(e.add_rule, rule, position=None if position is None else apply(int, position)).to_dict()

    @api.put("/rules/order")
    async def order_rules(body: OrderRequest):
        e = engine()
        apply(e.reorder_rules, body.ids)
        return e.state()["rules"]

    @api.patch("/rules/{rule_id}")
    async def update_rule(rule_id: str, changes: dict[str, Any] = Body(...)):
        return apply(engine().update_rule, rule_id, changes).to_dict()

    @api.delete("/rules/{rule_id}")
    async def delete_rule(rule_id: str):
        e = engine()
        apply(e.delete_rule, rule_id)
        return e.state()["rules"]
