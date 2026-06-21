from __future__ import annotations

from app.schemas.base import CamelModel


class AdoWorkItemSchema(CamelModel):
    id: int
    work_item_type: str = ""
    title: str = ""
    description: str | None = None
    acceptance_criteria: str | None = None
    assigned_to: str | None = None
    state: str | None = None
    url: str = ""


class WorkItemOption(CamelModel):
    """Lighter shape for the search autocomplete — matches the .NET search endpoint."""
    id: int
    title: str
    work_item_type: str
