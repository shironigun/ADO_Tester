from __future__ import annotations

from app.schemas.base import CamelModel


class DiagnosticsStatus(CamelModel):
    active_provider: str
    ado_pat_present: bool
    anthropic_key_present: bool
    azure_open_ai_configured: bool
    claude_cli_available: bool = False
    claude_cli_version: str | None = None


class LlmPingResponse(CamelModel):
    provider: str
    reply: str


class ProviderInfo(CamelModel):
    current: str
    available: list[str]


class SetProviderRequest(CamelModel):
    provider: str
