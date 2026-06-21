"""
Diagnostics endpoints — mirror the .NET DiagnosticsController exactly.
Routes:
  GET  /api/diagnostics              → env-var + active-provider status
  GET  /api/diagnostics/workitem/{id} → reads a work item via PAT
  GET  /api/diagnostics/llm          → single-turn ping to the active LLM provider
"""
from __future__ import annotations

import logging

from fastapi import APIRouter, HTTPException

from app.config import settings
from app.schemas import AdoWorkItemSchema, DiagnosticsStatus, LlmPingResponse
from app.services.ado_client import AdoApiError, get_ado_client
from app.services.llm import get_llm_client, get_provider_state

router = APIRouter(prefix="/api/diagnostics", tags=["diagnostics"])
log = logging.getLogger("qa.diagnostics")


@router.get("", response_model=DiagnosticsStatus)
async def status() -> DiagnosticsStatus:
    import asyncio

    from app.services.llm.claude_cli_client import probe_claude_cli

    provider = await get_provider_state().current()

    # Probe `claude` CLI via the SAME resolver the provider uses, run on a worker
    # thread (synchronous subprocess). asyncio's subprocess API is unsupported under
    # the Selector event loop uvicorn installs on Windows and raises a bare
    # NotImplementedError — which is why the CLI showed "missing" despite being installed.
    cli_available, cli_version = await asyncio.to_thread(probe_claude_cli)

    return DiagnosticsStatus(
        active_provider=provider.value,
        ado_pat_present=bool(settings.adoaccess_pat),
        anthropic_key_present=bool(settings.anthropic_api_key),
        azure_open_ai_configured=bool(
            settings.azure_openai_endpoint
            and settings.azure_openai_api_key
            and settings.azure_openai_deployment
        ),
        claude_cli_available=cli_available,
        claude_cli_version=cli_version,
    )


@router.get("/workitem/{work_item_id}", response_model=AdoWorkItemSchema)
async def get_work_item(work_item_id: int) -> AdoWorkItemSchema:
    try:
        wi = await get_ado_client().get_work_item(work_item_id)
    except AdoApiError as e:
        log.warning("ADO read failed: %s", e)
        raise HTTPException(status_code=404 if e.status == 404 else 502, detail=str(e)) from None
    except Exception as e:  # noqa: BLE001
        log.exception("ADO read failed unexpectedly")
        raise HTTPException(status_code=500, detail=str(e)) from None
    return AdoWorkItemSchema.model_validate(wi)


@router.get("/llm", response_model=LlmPingResponse)
async def ping_llm() -> LlmPingResponse:
    client = await get_llm_client()
    try:
        reply = await client.complete(
            system_prompt="Reply with exactly: OK",
            user_prompt="Say OK.",
        )
    except Exception as e:  # noqa: BLE001 — sanitized message comes from the client
        raise HTTPException(status_code=502, detail=str(e)) from None
    return LlmPingResponse(provider=client.provider_name, reply=reply)
