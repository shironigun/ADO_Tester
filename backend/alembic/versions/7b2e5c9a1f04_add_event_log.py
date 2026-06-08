"""Add EventLogJson column to TestSessions, BugSessions, Reports.

Stores the persisted narrative of the live agent console (non-token events) so a finished
run's activity feed can be replayed. Nullable.

Revision ID: 7b2e5c9a1f04
Revises: 3c0a91f4d2e7
Create Date: 2026-06-08
"""
from __future__ import annotations

from typing import Sequence, Union

import sqlalchemy as sa
from alembic import op


revision: str = "7b2e5c9a1f04"
down_revision: Union[str, Sequence[str], None] = "3c0a91f4d2e7"
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None

_TABLES = ("TestSessions", "BugSessions", "Reports")


def upgrade() -> None:
    for tbl in _TABLES:
        with op.batch_alter_table(tbl) as batch:
            batch.add_column(sa.Column("EventLogJson", sa.String(), nullable=True))


def downgrade() -> None:
    for tbl in _TABLES:
        with op.batch_alter_table(tbl) as batch:
            batch.drop_column("EventLogJson")
