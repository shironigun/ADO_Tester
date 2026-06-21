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
import logging
from typing import Any

log = logging.getLogger("qa.agents")


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


def extract_json(raw: str, opener: str = "{") -> str:
    """Pull the JSON value out of a possibly-noisy LLM response. Handles ``` fences AND
    leading/trailing prose ("Here are the test cases: [...]") by scanning for the first
    balanced, string-aware bracket structure. Returns '' if none is found."""
    closer = "}" if opener == "{" else "]"
    s = strip_fences(raw, opener)
    start = s.find(opener)
    if start < 0:
        return ""
    depth, in_str, esc = 0, False, False
    for i in range(start, len(s)):
        ch = s[i]
        if in_str:
            if esc:
                esc = False
            elif ch == "\\":
                esc = True
            elif ch == '"':
                in_str = False
            continue
        if ch == '"':
            in_str = True
        elif ch == opener:
            depth += 1
        elif ch == closer:
            depth -= 1
            if depth == 0:
                return s[start:i + 1]
    return s[start:]  # unbalanced (truncated) — hand the remainder to json and let it decide


async def complete_json(
    llm, system_prompt: str, user_prompt: str, *, opener: str = "{",
    progress=None, emit=None, attempts: int = 3, label: str = "Agent",
) -> Any:
    """Call the LLM and parse JSON robustly. Tolerates prose-wrapped JSON (extract_json) and
    retries on empty/invalid output — so a transient empty CLI response or a stray preamble no
    longer hard-fails the pipeline. Raises RuntimeError with a raw snippet only after all tries."""
    last_err: Exception | None = None
    last_raw = ""
    for attempt in range(1, attempts + 1):
        sys_p = system_prompt
        if attempt > 1:
            # Nudge the model back to strict JSON on a retry.
            sys_p = (
                system_prompt
                + "\n\nIMPORTANT: Your previous response was not valid JSON. Respond with ONLY the "
                "JSON value — no prose, no markdown fences, no explanation. Start your reply with "
                f"`{opener}`."
            )
        last_raw = await llm.complete(sys_p, user_prompt, progress=progress, emit=emit)
        candidate = extract_json(last_raw, opener)
        if candidate.strip():
            try:
                return json.loads(candidate)
            except json.JSONDecodeError as e:
                last_err = e
        else:
            last_err = ValueError("empty or non-JSON response")
        log.warning("%s JSON parse failed (attempt %d/%d): %s", label, attempt, attempts, last_err)
    snippet = (last_raw or "").strip()[:200].replace("\n", " ")
    raise RuntimeError(
        f"{label} returned invalid JSON after {attempts} attempts: {last_err}. "
        f"Raw response head: {snippet!r}"
    )


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
