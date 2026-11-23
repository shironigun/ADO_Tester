"""
B4 prompt extraction — writes the FROZEN agent system prompts byte-exact from the live Python into
.txt files embedded by the .NET Infrastructure project (so the port never transcribes them by hand).
Run from repo root with the backend venv:  backend\.venv\Scripts\python.exe write_prompts.py
Re-run whenever a Python prompt changes.
"""
from __future__ import annotations
import os, sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "backend"))

from app.services.agents import analyst, tester, bug_writer  # noqa: E402
from app.services.agents import code_verifier  # noqa: E402

DEST = os.path.join("dotnet", "src", "QaAutomation.Infrastructure", "Agents", "Prompts")
os.makedirs(DEST, exist_ok=True)


def write(name: str, text: str) -> None:
    # newline="" → no CRLF translation; keep the exact LF bytes the Python prompt uses.
    with open(os.path.join(DEST, name), "w", encoding="utf-8", newline="") as fh:
        fh.write(text)


write("analyst_system.txt", analyst.SYSTEM_PROMPT)
write("tester_system.txt", tester.SYSTEM_PROMPT)
write("bug_writer_system.txt", bug_writer.SYSTEM_PROMPT)
write("code_verifier_system.txt", code_verifier.SYSTEM_PROMPT)
write("code_verifier_step_system.txt", code_verifier.STEP_SYSTEM_PROMPT)
write("actual_rule_full.txt", code_verifier._ACTUAL_RULE_FULL)
write("actual_rule_lite.txt", code_verifier._ACTUAL_RULE_LITE)

print("wrote prompts to", DEST)
for fn in sorted(os.listdir(DEST)):
    print(f"  {fn}: {os.path.getsize(os.path.join(DEST, fn))} bytes")
