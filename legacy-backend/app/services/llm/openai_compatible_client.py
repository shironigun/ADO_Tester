"""
OpenAI-compatible provider. Works with:
  - GitHub Models (free) — base_url=https://models.inference.ai.azure.com, key=GitHub PAT
  - OpenAI direct      — base_url=https://api.openai.com/v1
  - Groq               — base_url=https://api.groq.com/openai/v1
  - OpenRouter         — base_url=https://openrouter.ai/api/v1
  - Local Ollama       — base_url=http://localhost:11434/v1
"""
from __future__ import annotations

from openai import APIStatusError, AsyncOpenAI

from app.config import settings
from app.services.sanitizer import sanitize


class OpenAiCompatibleLlmClient:
    provider_name = "OpenAICompatible"

    def __init__(self) -> None:
        self._client = AsyncOpenAI(
            base_url=settings.openai_base_url,
            api_key=settings.openai_api_key or "missing-key",
        )

    async def complete(
        self,
        system_prompt: str,
        user_prompt: str,
        model: str | None = None,
        progress: object | None = None,  # streaming progress — ignored (non-streaming provider)
        emit: object | None = None,       # streaming emit callback — ignored (non-streaming provider)
    ) -> str:
        try:
            resp = await self._client.chat.completions.create(
                model=model or settings.openai_model,
                max_tokens=settings.anthropic_max_tokens,  # shared default; per-provider override later
                messages=[
                    {"role": "system", "content": system_prompt},
                    {"role": "user", "content": user_prompt},
                ],
            )
        except APIStatusError as e:
            body = sanitize(str(e), settings.openai_api_key)
            raise RuntimeError(f"OpenAI-compatible endpoint returned {e.status_code}: {body}") from None

        choice = resp.choices[0] if resp.choices else None
        return choice.message.content if (choice and choice.message and choice.message.content) else ""
