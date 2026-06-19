"""
B2 golden capture — records the EXACT request bodies the live Python ADO client builds for the
byte-sensitive write paths, so the .NET port can be diffed against them. Run from repo root with the
backend venv:  backend\.venv\Scripts\python.exe golden_capture.py
Writes dotnet/tests/QaAutomation.Tests/golden/ado_golden.json (parsed json-patch structures + steps XML).
"""
from __future__ import annotations
import asyncio, json, os, sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "backend"))

from app.config import settings  # noqa: E402
from app.services.ado_client import AzureDevOpsClient, TestCaseStep, _build_steps_xml  # noqa: E402


class _Resp:
    status_code = 200
    is_success = True
    text = '{"id":123}'
    def json(self): return {"id": 123}
    def raise_for_status(self): pass


class _FakeHttp:
    def __init__(self): self.last = None
    async def post(self, url, content=None, json=None, headers=None):
        self.last = {"content": content, "json": json}; return _Resp()
    async def patch(self, url, content=None, json=None, headers=None):
        self.last = {"content": content, "json": json}; return _Resp()
    async def get(self, url, headers=None): return _Resp()
    async def aclose(self): pass


# Inputs deliberately exercise ' " & < > and a non-ASCII em-dash (U+2014).
STEPS = [
    {"action": 'Click "SAVE" & confirm', "expected": "Record's saved <ok> — done"},
    {"action": "Login to YourProduct", "expected": "Dashboard loads"},
]
TC = {"title": "Verify that user's data — saved & <validated>", "preconditions": "Logged in & 'ready' <prod>"}
BUG = {
    "title": "Bug: can't save — data & <lost>", "severity": "High",
    "preconditions": "User's logged in & on QA",
    "reproSteps": ["Open the 'Quote' tab & wait", "Click <Save> — twice"],
    "expected": "It's saved — no error & visible",
    "actual": "Error 500 — 'lost' & <broken>",
    "suggestedFix": "Guard the null & re-fetch user's id",
}
SUITE = {"planId": 10, "suiteId": 20, "testCaseId": 30}
TESTED_BY = {"storyId": 100, "testCaseId": 200}
RELATED = {"fromId": 100, "toId": 200}


async def main() -> None:
    c = AzureDevOpsClient()
    c._http = _FakeHttp()  # type: ignore[assignment]
    steps = [TestCaseStep(action=s["action"], expected=s["expected"]) for s in STEPS]

    await c.create_test_case(TC["title"], TC["preconditions"], steps)
    tc_patch = json.loads(c._http.last["content"])

    await c.create_bug(BUG["title"], BUG["severity"], BUG["preconditions"],
                       BUG["reproSteps"], BUG["expected"], BUG["actual"], BUG["suggestedFix"])
    bug_patch = json.loads(c._http.last["content"])

    await c.add_test_case_to_suite(SUITE["planId"], SUITE["suiteId"], SUITE["testCaseId"])
    suite_body = c._http.last["json"]

    await c.link_tested_by(TESTED_BY["storyId"], TESTED_BY["testCaseId"])
    tested_by_body = json.loads(c._http.last["content"])

    await c.link_related(RELATED["fromId"], RELATED["toId"])
    related_body = json.loads(c._http.last["content"])

    out = {
        "areaPath": settings.ado_bug_area_path,
        "qaOwner": settings.ado_qa_owner,
        "inputs": {"steps": STEPS, "tc": TC, "bug": BUG, "suite": SUITE, "testedBy": TESTED_BY, "related": RELATED},
        "stepsXml": _build_steps_xml(steps),
        "tcPatch": tc_patch,
        "bugPatch": bug_patch,
        "suiteBody": suite_body,
        "testedByBody": tested_by_body,
        "relatedBody": related_body,
    }

    dest = os.path.join("dotnet", "tests", "QaAutomation.Tests", "golden")
    os.makedirs(dest, exist_ok=True)
    path = os.path.join(dest, "ado_golden.json")
    with open(path, "w", encoding="utf-8") as fh:
        json.dump(out, fh, ensure_ascii=False, indent=2)
    print("wrote", path)
    print("stepsXml:", out["stepsXml"])


asyncio.run(main())
