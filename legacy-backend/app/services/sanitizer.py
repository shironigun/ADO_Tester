"""
Redacts PAT / API keys from echoed error bodies before they bubble up — same belt-and-braces
the .NET code applied. Also truncates long bodies so logs/error messages stay manageable.
"""
from __future__ import annotations

MAX_LENGTH = 2048


def sanitize(body: str, *secrets: str | None) -> str:
    if not body:
        return ""
    s = body
    for secret in secrets:
        if secret and len(secret) > 6:
            s = s.replace(secret, "***REDACTED***")
    if len(s) > MAX_LENGTH:
        s = s[:MAX_LENGTH] + "…[truncated]"
    return s
