"""
Configuration — mirrors the .NET AzureDevOpsOptions / AnthropicOptions / etc.
Secrets are loaded from the repo-root .env via pydantic-settings.
"""
from __future__ import annotations

from enum import Enum
from pathlib import Path

from pydantic_settings import BaseSettings, SettingsConfigDict

# Resolve repo root (one level up from backend/). The .env file already exists there.
REPO_ROOT = Path(__file__).resolve().parents[2]
ENV_PATH = REPO_ROOT / ".env"


class LlmProvider(str, Enum):
    """JSON-serializes as 'Anthropic' / 'AzureOpenAI' / 'OpenAICompatible' / 'ClaudeCli'."""
    Anthropic = "Anthropic"
    AzureOpenAI = "AzureOpenAI"
    OpenAICompatible = "OpenAICompatible"
    ClaudeCli = "ClaudeCli"


class Settings(BaseSettings):
    model_config = SettingsConfigDict(
        env_file=str(ENV_PATH),
        env_file_encoding="utf-8",
        case_sensitive=False,
        extra="ignore",
    )

    # ── Server ──────────────────────────────────────────────────────────────
    backend_port: int = 5250  # .NET still on 5249 during the migration

    # ── Azure DevOps ────────────────────────────────────────────────────────
    ado_organization: str = "ConstellationDealer"
    ado_project: str = "Ideal Agile"
    ado_api_version: str = "7.1"
    adoaccess_pat: str = ""  # env: ADOACCESS_PAT

    # ── Anthropic ───────────────────────────────────────────────────────────
    anthropic_api_key: str = ""
    anthropic_model: str = "claude-sonnet-4-6"
    anthropic_max_tokens: int = 4096

    # ── Azure OpenAI ────────────────────────────────────────────────────────
    azure_openai_endpoint: str = ""
    azure_openai_api_key: str = ""
    azure_openai_deployment: str = ""
    azure_openai_api_version: str = "2024-08-01-preview"

    # ── OpenAI-compatible (GitHub Models / Groq / OpenAI / Ollama) ──────────
    openai_base_url: str = "https://models.inference.ai.azure.com"
    openai_api_key: str = ""
    openai_model: str = "gpt-4o-mini"

    # ── Claude CLI (Pro/Max subscription via local `claude` CLI) ────────────
    claude_cli_path: str = "claude"           # use PATH lookup by default
    claude_cli_model: str = ""                # blank = whatever Claude CLI defaults to
    claude_cli_timeout_seconds: int = 180

    # ── LLM defaults ────────────────────────────────────────────────────────
    llm_provider_default: LlmProvider = LlmProvider.Anthropic

    # ── Storage ─────────────────────────────────────────────────────────────
    data_directory: str = "data"
    database_file_name: str = "qa.db"
    knowledgebase_folder_name: str = "kb"
    reports_folder_name: str = "reports"

    # ── Scheduler ───────────────────────────────────────────────────────────
    scheduler_timezone: str = "Asia/Karachi"  # Mahmood's local TZ — Thursday 11:00 fires here

    @property
    def ado_base_url(self) -> str:
        from urllib.parse import quote
        return f"https://dev.azure.com/{self.ado_organization.strip('/')}/{quote(self.ado_project)}"

    @property
    def data_path(self) -> Path:
        p = Path(self.data_directory)
        if not p.is_absolute():
            p = (REPO_ROOT / "backend" / p).resolve()
        p.mkdir(parents=True, exist_ok=True)
        return p

    @property
    def database_path(self) -> Path:
        return self.data_path / self.database_file_name

    @property
    def database_url(self) -> str:
        return f"sqlite+aiosqlite:///{self.database_path.as_posix()}"


settings = Settings()
