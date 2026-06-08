"""
Tolerant JSON parsing helpers — matches the .NET FlexibleStringListConverter + lenient JSON.

LLMs sometimes return:
  - markdown-fenced JSON ( ```json ... ``` )
  - list items as objects/numbers/bools where strings are expected
  - keys that drift in case
This module normalizes all of that before validation.
"""
from __future__ import annotations

import json
from typing import Any


def strip_fences(raw: str, opener: str = "{") -> str:
    """Strip leading ``` fences and trailing closers — opener is `{` for objects, `[` for arrays."""
    closer = "}" if opener == "{" else "]"
    s = raw.strip()
    if s.startswith("```"):
        i = s.find(opener)
        j = s.rfind(closer)
        if i >= 0 and j > i:
            return s[i:j + 1]
    return s


def coerce_string(value: Any) -> str:
    """Flatten any JSON value into a single readable string."""
    if value is None:
        return ""
    if isinstance(value, str):
        return value
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, (int, float)):
        return str(value)
    if isinstance(value, dict):
        # "key: value; key: value" — same shape as the .NET FlexibleStringListConverter
        return "; ".join(f"{k}: {coerce_string(v)}" for k, v in value.items())
    if isinstance(value, list):
        return json.dumps(value, ensure_ascii=False)
    return str(value)


def coerce_string_list(value: Any) -> list[str]:
    if value is None:
        return []
    if isinstance(value, str):
        return [value] if value.strip() else []
    if isinstance(value, list):
        return [coerce_string(v) for v in value if v is not None]
    return [coerce_string(value)]
