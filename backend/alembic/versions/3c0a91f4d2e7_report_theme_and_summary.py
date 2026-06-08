"""Add Theme + SummaryJson columns to Reports.

Revision ID: 3c0a91f4d2e7
Revises: 1e39a44b971c
Create Date: 2026-06-06
"""
from __future__ import annotations

from typing import Sequence, Union

import sqlalchemy as sa
from alembic import op


revision: str = "3c0a91f4d2e7"
down_revision: Union[str, Sequence[str], None] = "1e39a44b971c"
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    with op.batch_alter_table("Reports") as batch:
        batch.add_column(
            sa.Column("Theme", sa.String(), nullable=False, server_default="Executive")
        )
        batch.add_column(sa.Column("SummaryJson", sa.String(), nullable=True))


def downgrade() -> None:
    with op.batch_alter_table("Reports") as batch:
        batch.drop_column("SummaryJson")
        batch.drop_column("Theme")
