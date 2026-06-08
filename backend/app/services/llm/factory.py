"""Resolves the active LLM provider into a concrete client per call."""
from __future__ import annotations

from app.config import LlmProvider
from app.services.llm.anthropic_client import AnthropicLlmClient
from app.services.llm.azure_openai_client import AzureOpenAiLlmClient
from app.services.llm.base import LlmClient
from app.services.llm.claude_cli_client import ClaudeCliLlmClient
from app.services.llm.openai_compatible_client import OpenAiCompatibleLlmClient
from app.services.llm.provider_state import get_provider_state


async def get_llm_client() -> LlmClient:
    provider = await get_provider_state().current()
    if provider is LlmProvider.AzureOpenAI:
        return AzureOpenAiLlmClient()
    if provider is LlmProvider.OpenAICompatible:
        return OpenAiCompatibleLlmClient()
    if provider is LlmProvider.ClaudeCli:
        return ClaudeCliLlmClient()
    return AnthropicLlmClient()
