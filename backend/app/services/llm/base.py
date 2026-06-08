"""LLM provider interface — same shape as the .NET ILlmClient."""
from __future__ import annotations

from typing import Protocol


class LlmClient(Protocol):
    """Provider-agnostic chat completion. Implementations: Anthropic, AzureOpenAI, OpenAICompatible."""

    @property
    def provider_name(self) -> str: ...

    async def complete(
        self,
        system_prompt: str,
        user_prompt: str,
        model: str | None = None,
    ) -> str:
        """Single-turn completion. Returns the concatenated text content of the reply."""
        ...
