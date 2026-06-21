"""
Singleton tracking the active LLM provider, persisted in the AppSetting table
(key=`llm.provider`) so the choice survives restarts. Mirrors the .NET LlmProviderState.
"""
from __future__ import annotations

import asyncio

from sqlalchemy import select

from app.config import LlmProvider, settings
from app.db import SessionLocal
from app.models import AppSetting

SETTING_KEY = "llm.provider"


class ProviderState:
    def __init__(self) -> None:
        self._lock = asyncio.Lock()
        self._loaded = False
        self._current: LlmProvider = settings.llm_provider_default

    async def current(self) -> LlmProvider:
        await self._ensure_loaded()
        return self._current

    async def set(self, provider: LlmProvider) -> None:
        async with self._lock:
            self._current = provider
            self._loaded = True
            async with SessionLocal() as s:
                existing = (await s.execute(
                    select(AppSetting).where(AppSetting.key == SETTING_KEY)
                )).scalar_one_or_none()
                if existing is None:
                    s.add(AppSetting(key=SETTING_KEY, value=provider.value))
                else:
                    existing.value = provider.value
                await s.commit()

    @staticmethod
    def available() -> list[LlmProvider]:
        return list(LlmProvider)

    async def _ensure_loaded(self) -> None:
        if self._loaded:
            return
        async with self._lock:
            if self._loaded:
                return
            try:
                async with SessionLocal() as s:
                    setting = (await s.execute(
                        select(AppSetting).where(AppSetting.key == SETTING_KEY)
                    )).scalar_one_or_none()
                    if setting is not None:
                        try:
                            self._current = LlmProvider(setting.value)
                        except ValueError:
                            pass  # stick with default if stored value is malformed
            except Exception:  # noqa: BLE001 — DB may not be ready during very early startup
                pass
            self._loaded = True


_state: ProviderState | None = None


def get_provider_state() -> ProviderState:
    global _state
    if _state is None:
        _state = ProviderState()
    return _state
