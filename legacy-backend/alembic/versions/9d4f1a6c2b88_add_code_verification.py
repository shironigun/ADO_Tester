"""Add CodeVerificationJson column to BugSessions.

Stores per-test-case static code-verification verdicts for the code-analysis phase. Nullable.

Revision ID: 9d4f1a6c2b88
Revises: 7b2e5c9a1f04
Create Date: 2026-06-08
"""
from __future__ import annotations

from typing import Sequence, Union

import sqlalchemy as sa
from alembic import op


revision: str = "9d4f1a6c2b88"
down_revision: Union[str, Sequence[str], None] = "7b2e5c9a1f04"
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    with op.batch_alter_table("BugSessions") as batch:
        batch.add_column(sa.Column("CodeVerificationJson", sa.String(), nullable=True))


def downgrade() -> None:
    with op.batch_alter_table("BugSessions") as batch:
        batch.drop_column("CodeVerificationJson")
