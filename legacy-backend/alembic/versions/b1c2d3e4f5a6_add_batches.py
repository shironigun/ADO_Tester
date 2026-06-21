"""Add Batches table + BatchId column on TestSessions/BugSessions.

Epic/Feature runs group N child sessions (one per story) under a Batch. BatchId is nullable
(NULL for single runs). Batch status is derived from children at read time, not stored.

Revision ID: b1c2d3e4f5a6
Revises: 9d4f1a6c2b88
Create Date: 2026-06-16
"""
from __future__ import annotations

from typing import Sequence, Union

import sqlalchemy as sa
from alembic import op


revision: str = "b1c2d3e4f5a6"
down_revision: Union[str, Sequence[str], None] = "9d4f1a6c2b88"
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None

_SESSION_TABLES = ("TestSessions", "BugSessions")


def upgrade() -> None:
    op.create_table(
        "Batches",
        sa.Column("Id", sa.Integer(), autoincrement=True, nullable=False),
        sa.Column("ProjectId", sa.Integer(), nullable=False),
        sa.Column("Module", sa.String(), nullable=False),
        sa.Column("ParentWorkItemId", sa.Integer(), nullable=True),
        sa.Column("Title", sa.String(), nullable=False, server_default=""),
        sa.Column("Mode", sa.String(), nullable=True),
        sa.Column("CreatedAt", sa.DateTime(), nullable=False),
        sa.Column("UpdatedAt", sa.DateTime(), nullable=False),
        sa.PrimaryKeyConstraint("Id"),
    )
    for tbl in _SESSION_TABLES:
        with op.batch_alter_table(tbl) as batch:
            batch.add_column(sa.Column("BatchId", sa.Integer(), nullable=True))
            batch.create_index(op.f(f"ix_{tbl}_BatchId"), ["BatchId"], unique=False)


def downgrade() -> None:
    for tbl in _SESSION_TABLES:
        with op.batch_alter_table(tbl) as batch:
            batch.drop_index(op.f(f"ix_{tbl}_BatchId"))
            batch.drop_column("BatchId")
    op.drop_table("Batches")
