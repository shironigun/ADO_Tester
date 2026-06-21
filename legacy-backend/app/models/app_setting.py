from __future__ import annotations

from sqlalchemy import String
from sqlalchemy.orm import Mapped, mapped_column

from app.db import Base


class AppSetting(Base):
    """Tiny key/value store. Used to persist the active LLM provider across restarts."""
    __tablename__ = "Settings"

    key: Mapped[str] = mapped_column("Key", String, primary_key=True)
    value: Mapped[str] = mapped_column("Value", String, nullable=False, default="")
