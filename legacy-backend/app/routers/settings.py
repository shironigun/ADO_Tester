"""
Settings router.
  GET  /api/settings/provider               → { current, available }
  PUT  /api/settings/provider {provider}    → { current }
  GET  /api/settings/config                 → all UI-editable settings (secrets masked)
  PUT  /api/settings/config {values}        → upsert + reload clients
"""
from __future__ import annotations

import logging

from fastapi import APIRouter, HTTPException

from app.config import LlmProvider
from app.schemas import ProviderInfo, SetProviderRequest
from app.schemas.app_settings import UpdateAppSettingsRequest
from app.services.app_settings import get_app_settings_service
from app.services.llm import get_provider_state

router = APIRouter(prefix="/api/settings", tags=["settings"])
log = logging.getLogger("qa.settings")


@router.get("/provider", response_model=ProviderInfo)
async def get_provider() -> ProviderInfo:
    state = get_provider_state()
    current = await state.current()
    return ProviderInfo(
        current=current.value,
        available=[p.value for p in state.available()],
    )


@router.put("/provider", response_model=ProviderInfo)
async def set_provider(req: SetProviderRequest) -> ProviderInfo:
    try:
        provider = LlmProvider(req.provider)
    except ValueError:
        raise HTTPException(status_code=400, detail=f"Unknown provider '{req.provider}'.")
    state = get_provider_state()
    await state.set(provider)
    current = await state.current()
    return ProviderInfo(
        current=current.value,
        available=[p.value for p in state.available()],
    )


# ── UI-editable settings ────────────────────────────────────────────────────


@router.get("/config")
async def get_config() -> dict[str, object]:
    """All known settings keys. Secrets returned as masked '••••XXXX' with a `*.set` flag."""
    try:
        return await get_app_settings_service().get_all_for_ui()
    except Exception as e:  # noqa: BLE001
        log.exception("Get settings failed")
        raise HTTPException(status_code=500, detail=str(e)) from None


@router.put("/config")
async def put_config(req: UpdateAppSettingsRequest) -> dict[str, object]:
    """Upsert values; empty string on a secret leaves it unchanged."""
    try:
        svc = get_app_settings_service()
        await svc.update(req.values or {})
        return await svc.get_all_for_ui()
    except Exception as e:  # noqa: BLE001
        log.exception("Update settings failed")
        raise HTTPException(status_code=500, detail=str(e)) from None


# ── ADO discovery (PAT-only — no manual org/project typing) ────────────────


@router.get("/ado/orgs")
async def list_orgs() -> dict[str, object]:
    """
    Resolve organizations the configured PAT has access to. PATs use Basic auth and the
    vssps profile endpoint requires OAuth/AAD — most PATs return 401 here. Frontend treats
    `organizations: []` as "type the org manually".
    """
    from app.services.ado_client import get_ado_client
    try:
        orgs = await get_ado_client().discover_organizations()
        return {"organizations": orgs, "supported": True}
    except Exception as e:  # noqa: BLE001
        log.info("Org auto-discovery not available with this PAT: %s", e)
        return {"organizations": [], "supported": False}


@router.get("/ado/projects")
async def list_projects(organization: str | None = None) -> dict[str, object]:
    """Projects in the given (or currently-configured) organization."""
    from app.config import settings as env_settings
    from app.services.ado_client import get_ado_client
    org = organization or env_settings.ado_organization
    if not org:
        raise HTTPException(status_code=400, detail="No organization configured or supplied.")
    try:
        return {"organization": org, "projects": await get_ado_client().discover_projects(org)}
    except Exception as e:  # noqa: BLE001
        log.exception("ADO discover projects failed for %s", org)
        raise HTTPException(status_code=502, detail=str(e)) from None


@router.get("/claude-cli/models")
async def claude_cli_models() -> dict[str, object]:
    """
    Suggested model identifiers accepted by the local `claude` CLI's `--model` flag.
    These are curated suggestions — the UI also allows freetext, since model IDs change often
    and the CLI accepts aliases ("sonnet", "opus", "haiku") and full versioned IDs.
    """
    return {
        "suggestions": [
            # Aliases (CLI resolves to current latest)
            "sonnet",
            "opus",
            "haiku",
            # Versioned IDs (examples — update freely)
            "claude-sonnet-4-5",
            "claude-sonnet-4-6",
            "claude-opus-4-7",
            "claude-opus-4-8",
            "claude-haiku-4-6",
        ],
        "notes": (
            "Aliases like 'sonnet'/'opus'/'haiku' auto-resolve to the current latest. "
            "Leave blank to use whatever the CLI defaults to (matches your Claude Code config)."
        ),
    }


@router.get("/ado/area-paths")
async def list_area_paths(
    organization: str | None = None, project: str | None = None,
) -> dict[str, object]:
    """Area paths (a.k.a. 'boards') under the given/current organization + project."""
    from app.config import settings as env_settings
    from app.services.ado_client import get_ado_client
    org = organization or env_settings.ado_organization
    proj = project or env_settings.ado_project
    if not org or not proj:
        raise HTTPException(status_code=400, detail="Organization and project required.")
    try:
        return {
            "organization": org,
            "project": proj,
            "areaPaths": await get_ado_client().discover_area_paths(org, proj),
        }
    except Exception as e:  # noqa: BLE001
        log.exception("ADO discover area paths failed for %s / %s", org, proj)
        raise HTTPException(status_code=502, detail=str(e)) from None
