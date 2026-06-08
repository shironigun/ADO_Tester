from __future__ import annotations

from sqlalchemy import String
from sqlalchemy.orm import Mapped, mapped_column

from app.db import Base


class Project(Base):
    __tablename__ = "Projects"

    id: Mapped[int] = mapped_column("Id", primary_key=True, autoincrement=True)
    name: Mapped[str] = mapped_column("Name", String, nullable=False)
    ado_organization: Mapped[str] = mapped_column("AdoOrganization", String, nullable=False, default="")
    ado_project: Mapped[str] = mapped_column("AdoProject", String, nullable=False, default="")
