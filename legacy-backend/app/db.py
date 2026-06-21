"""SQLAlchemy async engine + session factory + FTS5 bootstrap."""
from __future__ import annotations

from collections.abc import AsyncIterator
from contextlib import asynccontextmanager

from sqlalchemy import text
from sqlalchemy.ext.asyncio import AsyncSession, async_sessionmaker, create_async_engine
from sqlalchemy.orm import DeclarativeBase

from app.config import settings


class Base(DeclarativeBase):
    """Single declarative base for every ORM model."""


engine = create_async_engine(settings.database_url, echo=False, future=True)

SessionLocal = async_sessionmaker(
    engine,
    class_=AsyncSession,
    expire_on_commit=False,
)


async def get_session() -> AsyncIterator[AsyncSession]:
    """FastAPI dependency — yields a request-scoped DB session."""
    async with SessionLocal() as session:
        yield session


@asynccontextmanager
async def session_scope() -> AsyncIterator[AsyncSession]:
    """For background tasks — opens a fresh session, commits on exit, never leaks across the request boundary."""
    async with SessionLocal() as session:
        yield session


async def ensure_fts_table() -> None:
    """
    Creates the FTS5 virtual table if missing — SQLAlchemy doesn't manage virtual tables,
    so this runs at startup alongside (after) Alembic migrations.
    """
    async with engine.begin() as conn:
        await conn.execute(text(
            "CREATE VIRTUAL TABLE IF NOT EXISTS doc_chunks "
            "USING fts5(content, project_id UNINDEXED, doc_id UNINDEXED)"
        ))
