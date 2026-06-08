"""Azure OpenAI provider — uses the `openai` SDK's AsyncAzureOpenAI wrapper."""
from __future__ import annotations

from openai import APIStatusError, AsyncAzureOpenAI

from app.config import settings
from app.services.sanitizer import sanitize


class AzureOpenAiLlmClient:
    provider_name = "AzureOpenAI"

    def __init__(self) -> None:
        self._client = AsyncAzureOpenAI(
            azure_endpoint=settings.azure_openai_endpoint,
            api_key=settings.azure_openai_api_key,
            api_version=settings.azure_openai_api_version,
        )

    async def complete(
        self,
        system_prompt: str,
        user_prompt: str,
        model: str | None = None,
        progress: object | None = None,  # streaming progress — ignored (non-streaming provider)
        emit: object | None = None,       # streaming emit callback — ignored (non-streaming provider)
    ) -> str:
        deployment = model or settings.azure_openai_deployment
        try:
            resp = await self._client.chat.completions.create(
                model=deployment,
                max_tokens=settings.anthropic_max_tokens,
                messages=[
                    {"role": "system", "content": system_prompt},
                    {"role": "user", "content": user_prompt},
                ],
            )
        except APIStatusError as e:
            body = sanitize(str(e), settings.azure_openai_api_key)
            raise RuntimeError(f"Azure OpenAI returned {e.status_code}: {body}") from None

        choice = resp.choices[0] if resp.choices else None
        return choice.message.content if (choice and choice.message and choice.message.content) else ""
