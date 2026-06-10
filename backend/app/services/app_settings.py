"""
AppSettingsService — UI-editable settings persisted in the AppSetting key/value table.

Layering:
  table value (if set)  → falls back to env-loaded `settings.*`

Reads cache in-memory after first load; `apply()` re-syncs the cache and patches `settings.*`
attributes so all the existing clients (ADO + LLM) pick up the new values on next use.
PUT handler calls `apply()` + resets the ADO + LLM HttpClient/SDK singletons.
"""
from __future__ import annotations

import asyncio
import logging
from typing import Any

from sqlalchemy import select

from app.config import settings as env_settings
from app.db import SessionLocal
from app.models import AppSetting

log = logging.getLogger("qa.app-settings")


# ── canonical setting keys ─────────────────────────────────────────────────

# Secrets / connectivity
K_ADO_ORG          = "ado.organization"
K_ADO_PROJECT      = "ado.project"
K_ADO_PAT          = "ado.pat"
K_ADO_REPO         = "ado.repository"
K_ADO_BRANCH       = "ado.default_branch"
K_ANTHROPIC_KEY    = "anthropic.api_key"
K_ANTHROPIC_MODEL  = "anthropic.model"
K_AZURE_ENDPOINT   = "azure_openai.endpoint"
K_AZURE_KEY        = "azure_openai.api_key"
K_AZURE_DEPLOY     = "azure_openai.deployment"
K_OPENAI_BASE_URL  = "openai.base_url"
K_OPENAI_KEY       = "openai.api_key"
K_OPENAI_MODEL     = "openai.model"
K_CLAUDE_CLI_PATH  = "claude_cli.path"
K_CLAUDE_CLI_MODEL = "claude_cli.model"
K_CLAUDE_CLI_TIMEOUT = "claude_cli.timeout_seconds"

# Author Tests defaults
K_DEFAULT_PLAN_ID  = "defaults.test_plan_id"
K_DEFAULT_SUITE_ID = "defaults.test_suite_id"

# Reports config
K_DEFAULT_BOARDS   = "reports.default_boards"     # semicolon-separated area paths
K_SCHED_ENABLED    = "reports.schedule_enabled"   # "true" | "false"
K_DEFAULT_RANGE    = "reports.default_range_days" # int as string
K_REPORT_TEAM_NAME = "reports.team_name"          # shown on the report cover eyebrow
K_REPORT_TYPES     = "reports.ticket_types"       # semicolon-separated work-item types

ALL_KEYS = [
    K_ADO_ORG, K_ADO_PROJECT, K_ADO_PAT, K_ADO_REPO, K_ADO_BRANCH,
    K_ANTHROPIC_KEY, K_ANTHROPIC_MODEL,
    K_AZURE_ENDPOINT, K_AZURE_KEY, K_AZURE_DEPLOY,
    K_OPENAI_BASE_URL, K_OPENAI_KEY, K_OPENAI_MODEL,
    K_CLAUDE_CLI_PATH, K_CLAUDE_CLI_MODEL, K_CLAUDE_CLI_TIMEOUT,
    K_DEFAULT_PLAN_ID, K_DEFAULT_SUITE_ID,
    K_DEFAULT_BOARDS, K_SCHED_ENABLED, K_DEFAULT_RANGE, K_REPORT_TEAM_NAME,
    K_REPORT_TYPES,
]

SECRET_KEYS = {K_ADO_PAT, K_ANTHROPIC_KEY, K_AZURE_KEY, K_OPENAI_KEY}


def _env_default(key: str) -> str:
    """Initial value when nothing is persisted yet — pulled from the .env-backed Settings."""
    return {
        K_ADO_ORG:         env_settings.ado_organization,
        K_ADO_PROJECT:     env_settings.ado_project,
        K_ADO_PAT:         env_settings.adoaccess_pat,
        K_ADO_REPO:        env_settings.ado_repository,
        K_ADO_BRANCH:      env_settings.ado_default_branch,
        K_ANTHROPIC_KEY:   env_settings.anthropic_api_key,
        K_ANTHROPIC_MODEL: env_settings.anthropic_model,
        K_AZURE_ENDPOINT:  env_settings.azure_openai_endpoint,
        K_AZURE_KEY:       env_settings.azure_openai_api_key,
        K_AZURE_DEPLOY:    env_settings.azure_openai_deployment,
        K_OPENAI_BASE_URL: env_settings.openai_base_url,
        K_OPENAI_KEY:      env_settings.openai_api_key,
        K_OPENAI_MODEL:    env_settings.openai_model,
        K_CLAUDE_CLI_PATH: env_settings.claude_cli_path,
        K_CLAUDE_CLI_MODEL: env_settings.claude_cli_model,
        K_CLAUDE_CLI_TIMEOUT: str(env_settings.claude_cli_timeout_seconds),
        K_DEFAULT_PLAN_ID:  "",
        K_DEFAULT_SUITE_ID: "",
        K_DEFAULT_BOARDS:   "",
        K_SCHED_ENABLED:    "true",
        K_DEFAULT_RANGE:    "7",
        K_REPORT_TEAM_NAME: "",
        K_REPORT_TYPES:     "User Story;Bug;Defect;Task",
    }.get(key, "")


def _mask(value: str) -> str:
    """Never leak any character of a secret. The `.set` boolean tells the UI it's configured."""
    if not value:
        return ""
    return "••••••••"


class AppSettingsService:
    def __init__(self) -> None:
        self._lock = asyncio.Lock()
        self._cache: dict[str, str] = {}
        self._loaded = False

    async def get(self, key: str) -> str:
        await self._ensure_loaded()
        return self._cache.get(key, _env_default(key))

    async def get_all_for_ui(self) -> dict[str, Any]:
        """Return every known key. Secrets are masked + `*_set` boolean flags accompany them."""
        await self._ensure_loaded()
        out: dict[str, Any] = {}
        for k in ALL_KEYS:
            v = self._cache.get(k, _env_default(k))
            if k in SECRET_KEYS:
                out[k] = _mask(v)
                out[f"{k}.set"] = bool(v)
            else:
                out[k] = v
        return out

    async def update(self, values: dict[str, str]) -> None:
        async with self._lock:
            async with SessionLocal() as s:
                for raw_key, raw_val in values.items():
                    if raw_key not in ALL_KEYS:
                        continue
                    val = "" if raw_val is None else str(raw_val)
                    # Empty string for a secret = leave as-is (UI sends "" by default for unchanged secrets).
                    if raw_key in SECRET_KEYS and val == "":
                        continue
                    existing = (await s.execute(
                        select(AppSetting).where(AppSetting.key == raw_key)
                    )).scalar_one_or_none()
                    if existing is None:
                        s.add(AppSetting(key=raw_key, value=val))
                    else:
                        existing.value = val
                await s.commit()

        await self.apply()

    async def apply(self) -> None:
        """Re-sync cache from DB and patch env_settings.* so existing clients pick it up."""
        async with self._lock:
            async with SessionLocal() as s:
                rows = (await s.execute(select(AppSetting))).scalars().all()
                self._cache = {r.key: r.value for r in rows}
                self._loaded = True

        # Mirror into env_settings (Pydantic v2 BaseSettings is a regular model — fields are settable).
        env_settings.ado_organization      = self._cache.get(K_ADO_ORG,         env_settings.ado_organization)
        env_settings.ado_project           = self._cache.get(K_ADO_PROJECT,     env_settings.ado_project)
        env_settings.adoaccess_pat         = self._cache.get(K_ADO_PAT,         env_settings.adoaccess_pat)
        env_settings.ado_repository        = self._cache.get(K_ADO_REPO,        env_settings.ado_repository)
        env_settings.ado_default_branch    = self._cache.get(K_ADO_BRANCH,      env_settings.ado_default_branch)
        env_settings.anthropic_api_key     = self._cache.get(K_ANTHROPIC_KEY,   env_settings.anthropic_api_key)
        env_settings.anthropic_model       = self._cache.get(K_ANTHROPIC_MODEL, env_settings.anthropic_model)
        env_settings.azure_openai_endpoint = self._cache.get(K_AZURE_ENDPOINT,  env_settings.azure_openai_endpoint)
        env_settings.azure_openai_api_key  = self._cache.get(K_AZURE_KEY,       env_settings.azure_openai_api_key)
        env_settings.azure_openai_deployment = self._cache.get(K_AZURE_DEPLOY,  env_settings.azure_openai_deployment)
        env_settings.openai_base_url       = self._cache.get(K_OPENAI_BASE_URL, env_settings.openai_base_url)
        env_settings.openai_api_key        = self._cache.get(K_OPENAI_KEY,      env_settings.openai_api_key)
        env_settings.openai_model          = self._cache.get(K_OPENAI_MODEL,    env_settings.openai_model)
        env_settings.claude_cli_path       = self._cache.get(K_CLAUDE_CLI_PATH, env_settings.claude_cli_path)
        env_settings.claude_cli_model      = self._cache.get(K_CLAUDE_CLI_MODEL, env_settings.claude_cli_model)
        try:
            timeout_raw = self._cache.get(K_CLAUDE_CLI_TIMEOUT, str(env_settings.claude_cli_timeout_seconds))
            env_settings.claude_cli_timeout_seconds = max(10, int(timeout_raw))
        except (TypeError, ValueError):
            pass

        # Reset clients so the patched values get picked up on the next request.
        from app.services import ado_client as ado_mod
        ado_mod._client = None  # type: ignore[attr-defined]

        log.info("App settings applied — ADO + LLM clients will rebuild on next use")

    async def _ensure_loaded(self) -> None:
        if self._loaded:
            return
        await self.apply()


_singleton: AppSettingsService | None = None


def get_app_settings_service() -> AppSettingsService:
    global _singleton
    if _singleton is None:
        _singleton = AppSettingsService()
    return _singleton
