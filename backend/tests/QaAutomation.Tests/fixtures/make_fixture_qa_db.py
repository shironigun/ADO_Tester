#!/usr/bin/env python3
"""Build a faithful fixture ``backend/data/qa.db`` for the .NET D6 adoption test.

The real migration shares the Python app's Alembic-built ``qa.db``; that file is gitignored
(``*.db``) so it is absent on a clean checkout. ``DatabaseInitializerTests.Adopts_existing_alembic_db_
without_data_loss`` then has nothing to adopt. This script reproduces — with the **stdlib only** —
exactly what Alembic + SQLAlchemy would have written:

* the cumulative schema (all 5 migrations) with the frozen PascalCase table/column names,
* ``alembic_version`` stamped at the head revision ``b1c2d3e4f5a6``,
* SQLAlchemy-style datetime text (``YYYY-MM-DD HH:MM:SS.ffffff``) so we prove EF can read it,
* enum columns holding their exact stored string values (``Status``, ``Theme``, ``Module='author-tests'``),
* at least one row in every table (``TestSessions`` > 0, per the test's meaningfulness guard).

Run from anywhere:  ``python make_fixture_qa_db.py``  (writes <repo>/backend/data/qa.db)
"""
from __future__ import annotations

import sqlite3
from pathlib import Path

# <repo>/dotnet/tests/QaAutomation.Tests/fixtures/this.py -> parents[4] == <repo>
REPO_ROOT = Path(__file__).resolve().parents[4]
DB_PATH = REPO_ROOT / "backend" / "data" / "qa.db"

ALEMBIC_HEAD = "b1c2d3e4f5a6"

# Cumulative schema = initial (1e39a44b971c) + Theme/SummaryJson (3c0a91f4d2e7)
# + EventLogJson (7b2e5c9a1f04) + CodeVerificationJson (9d4f1a6c2b88) + Batches/BatchId (b1c2d3e4f5a6).
SCHEMA = """
CREATE TABLE alembic_version (version_num VARCHAR(32) NOT NULL, CONSTRAINT alembic_version_pkc PRIMARY KEY (version_num));

CREATE TABLE "Projects" (
    "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    "Name" VARCHAR NOT NULL, "AdoOrganization" VARCHAR NOT NULL, "AdoProject" VARCHAR NOT NULL);

CREATE TABLE "Settings" ("Key" VARCHAR NOT NULL PRIMARY KEY, "Value" VARCHAR NOT NULL);

CREATE TABLE "TestSessions" (
    "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    "ProjectId" INTEGER NOT NULL, "WorkItemId" INTEGER NOT NULL, "WorkItemTitle" VARCHAR NOT NULL,
    "Status" VARCHAR NOT NULL, "CurrentActivity" VARCHAR, "AnalysisJson" VARCHAR, "ErrorMessage" VARCHAR,
    "EventLogJson" VARCHAR, "BatchId" INTEGER, "CreatedAt" DATETIME NOT NULL, "UpdatedAt" DATETIME NOT NULL);

CREATE TABLE "TestCaseDrafts" (
    "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    "SessionId" INTEGER NOT NULL, "Title" VARCHAR NOT NULL, "Preconditions" VARCHAR NOT NULL,
    "StepsJson" VARCHAR NOT NULL, "Status" VARCHAR NOT NULL, "AdoTestCaseId" INTEGER, "SortOrder" INTEGER NOT NULL,
    FOREIGN KEY("SessionId") REFERENCES "TestSessions" ("Id") ON DELETE CASCADE);

CREATE TABLE "BugSessions" (
    "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    "ProjectId" INTEGER NOT NULL, "WorkItemId" INTEGER NOT NULL, "WorkItemTitle" VARCHAR NOT NULL,
    "Status" VARCHAR NOT NULL, "CurrentActivity" VARCHAR, "AnalysisJson" VARCHAR, "ActualBehavior" VARCHAR,
    "CodeContext" VARCHAR, "ErrorMessage" VARCHAR, "EventLogJson" VARCHAR, "CodeVerificationJson" VARCHAR,
    "BatchId" INTEGER, "CreatedAt" DATETIME NOT NULL, "UpdatedAt" DATETIME NOT NULL);

CREATE TABLE "BugDrafts" (
    "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    "SessionId" INTEGER NOT NULL, "Title" VARCHAR NOT NULL, "Severity" VARCHAR NOT NULL,
    "Preconditions" VARCHAR NOT NULL, "ReproStepsJson" VARCHAR NOT NULL, "ExpectedBehavior" VARCHAR NOT NULL,
    "ActualBehavior" VARCHAR NOT NULL, "SuggestedFix" VARCHAR NOT NULL, "Status" VARCHAR NOT NULL,
    "AdoBugId" INTEGER, "SortOrder" INTEGER NOT NULL,
    FOREIGN KEY("SessionId") REFERENCES "BugSessions" ("Id") ON DELETE CASCADE);

CREATE TABLE "Reports" (
    "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    "ProjectId" INTEGER NOT NULL, "FromUtc" DATETIME NOT NULL, "ToUtc" DATETIME NOT NULL,
    "Boards" VARCHAR NOT NULL, "TicketCount" INTEGER NOT NULL, "Status" VARCHAR NOT NULL,
    "HtmlPath" VARCHAR, "ErrorMessage" VARCHAR, "CreatedAt" DATETIME NOT NULL, "Title" VARCHAR NOT NULL,
    "Trigger" VARCHAR NOT NULL, "Theme" VARCHAR DEFAULT 'Executive' NOT NULL,
    "SummaryJson" VARCHAR, "EventLogJson" VARCHAR);

CREATE TABLE "Documents" (
    "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    "ProjectId" INTEGER NOT NULL, "FileName" VARCHAR NOT NULL, "StoredPath" VARCHAR NOT NULL,
    "ChunkCount" INTEGER NOT NULL, "SizeBytes" INTEGER NOT NULL, "UploadedAt" DATETIME NOT NULL,
    FOREIGN KEY("ProjectId") REFERENCES "Projects" ("Id") ON DELETE CASCADE);

CREATE TABLE "Batches" (
    "Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    "ProjectId" INTEGER NOT NULL, "Module" VARCHAR NOT NULL, "ParentWorkItemId" INTEGER,
    "Title" VARCHAR DEFAULT '' NOT NULL, "Mode" VARCHAR,
    "CreatedAt" DATETIME NOT NULL, "UpdatedAt" DATETIME NOT NULL);

CREATE INDEX "ix_Documents_ProjectId" ON "Documents" ("ProjectId");
CREATE INDEX "ix_TestCaseDrafts_SessionId" ON "TestCaseDrafts" ("SessionId");
CREATE INDEX "ix_BugDrafts_SessionId" ON "BugDrafts" ("SessionId");
CREATE INDEX "ix_TestSessions_BatchId" ON "TestSessions" ("BatchId");
CREATE INDEX "ix_BugSessions_BatchId" ON "BugSessions" ("BatchId");
"""

# SQLAlchemy persists Python datetimes to SQLite as this exact text shape (6-digit microseconds).
DT = "2026-06-12 09:15:42.123456"
DT2 = "2026-06-13 14:02:05.987000"


def seed(cx: sqlite3.Connection) -> None:
    cx.execute("INSERT INTO alembic_version (version_num) VALUES (?)", (ALEMBIC_HEAD,))

    cx.execute('INSERT INTO "Projects" ("Id","Name","AdoOrganization","AdoProject") VALUES (1,?,?,?)',
               ("Ideal Agile", "ConstellationDealer", "Ideal Agile"))

    cx.executemany('INSERT INTO "Settings" ("Key","Value") VALUES (?,?)',
                   [("llm.provider", "ClaudeCli"), ("schedule.weekly_report", "true")])

    cx.executemany(
        'INSERT INTO "TestSessions" '
        '("ProjectId","WorkItemId","WorkItemTitle","Status","CurrentActivity","AnalysisJson",'
        '"ErrorMessage","EventLogJson","BatchId","CreatedAt","UpdatedAt") VALUES (?,?,?,?,?,?,?,?,?,?,?)',
        [
            (1, 160728, "Customer search returns stale results", "Done", None, '{"summary":"ok"}',
             None, "[]", None, DT, DT2),
            (1, 160990, "Lead dedupe edge case", "Drafted", "Awaiting review", None,
             None, None, None, DT2, DT2),
        ])

    cx.executemany(
        'INSERT INTO "TestCaseDrafts" '
        '("SessionId","Title","Preconditions","StepsJson","Status","AdoTestCaseId","SortOrder") VALUES (?,?,?,?,?,?,?)',
        [
            (1, "Search by VIN returns fresh row", "Logged in as agent",
             '[{"action":"Search VIN","expected":"Fresh result shown"}]', "Pushed", 988001, 0),
            (1, "Search cache invalidates on edit", "", "[]", "Draft", None, 1),
        ])

    cx.execute(
        'INSERT INTO "BugSessions" '
        '("ProjectId","WorkItemId","WorkItemTitle","Status","CurrentActivity","AnalysisJson",'
        '"ActualBehavior","CodeContext","ErrorMessage","EventLogJson","CodeVerificationJson",'
        '"BatchId","CreatedAt","UpdatedAt") VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?)',
        (1, 160728, "Customer search returns stale results", "Done", None, '{"summary":"ok"}',
         "Old rows linger for ~30s", None, None, "[]", "[]", None, DT, DT2))

    cx.execute(
        'INSERT INTO "BugDrafts" '
        '("SessionId","Title","Severity","Preconditions","ReproStepsJson","ExpectedBehavior",'
        '"ActualBehavior","SuggestedFix","Status","AdoBugId","SortOrder") VALUES (?,?,?,?,?,?,?,?,?,?,?)',
        (1, "Stale customer rows after edit", "High", "Agent logged in",
         '["Edit a customer","Search again within 30s"]', "Updated row appears immediately",
         "Old row shown for ~30s", "Invalidate the search cache on write", "Pushed", 990123, 0))

    cx.execute(
        'INSERT INTO "Reports" '
        '("ProjectId","FromUtc","ToUtc","Boards","TicketCount","Status","HtmlPath","ErrorMessage",'
        '"CreatedAt","Title","Trigger","Theme","SummaryJson","EventLogJson") VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?)',
        (1, DT, DT2, "CRM Team", 12, "Done", "data/reports/r1.html", None,
         DT2, "Weekly QA — Jun 12", "Manual", "Executive", '{"tldr":["12 tickets"]}', "[]"))

    cx.execute(
        'INSERT INTO "Batches" '
        '("ProjectId","Module","ParentWorkItemId","Title","Mode","CreatedAt","UpdatedAt") VALUES (?,?,?,?,?,?,?)',
        (1, "author-tests", 160000, "Epic 160000 — Search revamp", None, DT, DT2))

    cx.execute(
        'INSERT INTO "Documents" '
        '("ProjectId","FileName","StoredPath","ChunkCount","SizeBytes","UploadedAt") VALUES (?,?,?,?,?,?)',
        (1, "domain-base.md", "data/kb/domain-base.md", 3, 2048, DT))


def main() -> None:
    DB_PATH.parent.mkdir(parents=True, exist_ok=True)
    if DB_PATH.exists():
        DB_PATH.unlink()
    cx = sqlite3.connect(str(DB_PATH))
    try:
        cx.executescript(SCHEMA)
        seed(cx)
        cx.commit()
    finally:
        cx.close()
    print(f"Wrote fixture DB -> {DB_PATH}")


if __name__ == "__main__":
    main()
