"""Anthropic provider via the official `anthropic` async SDK."""
from __future__ import annotations

from anthropic import AsyncAnthropic, APIStatusError

from app.config import settings
from app.services.sanitizer import sanitize


class AnthropicLlmClient:
    provider_name = "Anthropic"

    def __init__(self) -> None:
        self._client = AsyncAnthropic(api_key=settings.anthropic_api_key)

    async def complete(
        self,
        system_prompt: str,
        user_prompt: str,
        model: str | None = None,
        progress: object | None = None,  # streaming progress — ignored (non-streaming provider)
        emit: object | None = None,       # streaming emit callback — ignored (non-streaming provider)
    ) -> str:
        try:
            resp = await self._client.messages.create(
                model=model or settings.anthropic_model,
                max_tokens=settings.anthropic_max_tokens,
                system=system_prompt,
                messages=[{"role": "user", "content": user_prompt}],
            )
        except APIStatusError as e:  # echoes body — sanitize any leaked key
            body = sanitize(str(e), settings.anthropic_api_key)
            raise RuntimeError(f"Anthropic API returned {e.status_code}: {body}") from None

        return "".join(
            block.text for block in resp.content if getattr(block, "type", "") == "text"
        )
