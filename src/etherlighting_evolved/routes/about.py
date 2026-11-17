"""Health, product information and the catalogs the UI offers before login."""

from __future__ import annotations

from fastapi import APIRouter

from .. import DISCLAIMER, PROJECT_NAME, SHORT_NAME, TAGLINE, __version__
from ..color import NAMED_COLORS, Color
from ..effects import list_effects
from ..rules import FIELDS, STYLES, TEMPLATES
from .context import ApiContext


def attach(api: APIRouter, ctx: ApiContext) -> None:
    @api.get("/health")
    async def health():
        return {"ok": True, "name": PROJECT_NAME, "version": __version__, "logged_in": ctx.sessions.engine is not None}

    @api.get("/about")
    async def about():
        return {"name": PROJECT_NAME, "short_name": SHORT_NAME, "version": __version__, "tagline": TAGLINE,
                "disclaimer": DISCLAIMER}

    @api.get("/effects")
    async def effects():
        return list_effects()

    @api.get("/rules/fields")
    async def rule_fields():
        return {"fields": [f.schema() for f in FIELDS.values()], "styles": list(STYLES)}

    @api.get("/rules/templates")
    async def rule_templates():
        return TEMPLATES

    @api.get("/colors")
    async def colors():
        return {name: Color(*rgb).to_hex() for name, rgb in NAMED_COLORS.items()}
