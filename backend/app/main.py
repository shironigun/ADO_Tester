"""FastAPI entry point. Wires lifespan (DB + FTS + seed), CORS for the Vite dev server, routers."""
from __future__ import annotations

import logging
from contextlib import asynccontextmanager

from fastapi import FastAPI
from fastapi.middleware.cors import CORSMiddleware
from sqlalchemy import select

from app.config import settings
from app.db import SessionLocal, ensure_fts_table
from app.models import Project
from app.routers import author_tests as author_tests_router
from app.routers import diagnostics as diagnostics_router
from app.routers import find_bugs as find_bugs_router
from app.routers import knowledgebase as knowledgebase_router
from app.routers import reports as reports_router
from app.routers import settings as settings_router
from app.services.reports import get_scheduler

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s — %(message)s")
log = logging.getLogger("qa.api")


async def seed_default_project() -> None:
    """Make sure Project id=1 ("Ideal Agile") exists — same default the .NET seed did."""
    async with SessionLocal() as s:
        existing = (await s.execute(select(Project).where(Project.id == 1))).scalar_one_or_none()
        if existing is None:
            s.add(Project(
                id=1,
                name=settings.ado_project,
                ado_organization=settings.ado_organization,
                ado_project=settings.ado_project,
            ))
            await s.commit()
            log.info("Seeded Project id=1 '%s'", settings.ado_project)


@asynccontextmanager
async def lifespan(app: FastAPI):
    log.info("Starting QA Automation backend (Python)")
    log.info("DB path: %s", settings.database_path)
    await ensure_fts_table()
    await seed_default_project()

    # Pull UI-edited overrides (Settings table) into env_settings so all clients see them.
    from app.services.app_settings import get_app_settings_service
    await get_app_settings_service().apply()

    scheduler = get_scheduler()
    scheduler.start()

    yield

    scheduler.shutdown()
    log.info("Shutting down")


app = FastAPI(
    title="QA Automation API (Python)",
    version="0.1.0",
    lifespan=lifespan,
)

# Vite dev server proxies /api here; allow it for direct browser hits during development.
app.add_middleware(
    CORSMiddleware,
    allow_origins=["http://localhost:5173", "http://localhost:5174"],
    allow_methods=["*"],
    allow_headers=["*"],
    allow_credentials=False,
)


app.include_router(diagnostics_router.router)
app.include_router(settings_router.router)
app.include_router(knowledgebase_router.router)
app.include_router(author_tests_router.router)
app.include_router(find_bugs_router.router)
app.include_router(reports_router.router)


@app.get("/api/health")
async def health() -> dict[str, str]:
    return {"status": "ok"}
