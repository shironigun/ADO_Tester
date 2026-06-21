from __future__ import annotations

from pydantic import BaseModel, ConfigDict
from pydantic.alias_generators import to_camel


class CamelModel(BaseModel):
    """
    Base schema. JSON keys are camelCase (matches the .NET API + React client expectations).
    Inputs accept either alias or field name so PATCH bodies are forgiving.
    """
    model_config = ConfigDict(
        alias_generator=to_camel,
        populate_by_name=True,
        from_attributes=True,
    )
