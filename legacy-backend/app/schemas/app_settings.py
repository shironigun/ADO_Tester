from __future__ import annotations

from typing import Any

from pydantic import BaseModel, ConfigDict


class AppSettingsConfig(BaseModel):
    """Plain dict — keys are dotted setting names. Secrets are masked on GET."""
    model_config = ConfigDict(extra="allow")

    values: dict[str, Any]


class UpdateAppSettingsRequest(BaseModel):
    """Any subset of canonical keys. Empty string on a secret = leave unchanged."""
    model_config = ConfigDict(extra="allow")

    values: dict[str, str]
