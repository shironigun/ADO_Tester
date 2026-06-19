"""
Azure DevOps REST client — PAT (Basic) auth. Async via httpx.AsyncClient.

P1 implements the read paths used by Diagnostics + the work-item picker:
  - get_work_item(id)
  - search_work_items(q)
P3/P4/P5 add the write methods (Test Case / Bug create + suite + link, plus report query).
"""
from __future__ import annotations

import asyncio
import base64
import html as html_lib
import json
import logging
import re
import time
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field
from urllib.parse import quote

import httpx

from app.config import settings
from app.services.sanitizer import sanitize

log = logging.getLogger("qa.ado")

WORK_ITEM_FIELDS = [
    "System.Id",
    "System.Title",
    "System.Description",
    "System.WorkItemType",
    "System.AssignedTo",
    "System.State",
    "Microsoft.VSTS.Common.AcceptanceCriteria",
]

# Work-item hierarchy for Epic/Feature runs: descend through containers and collect the most-specific
# testable level below them. Types vary by project (some go Epic→Feature→Story, others Epic→Task), so
# rather than an allow-list of leaf types we descend only through containers and collect everything
# else, excluding pure test artifacts. The user then picks which to run.
CONTAINER_WORK_ITEM_TYPES = {"Epic", "Feature"}
NON_TESTABLE_WORK_ITEM_TYPES = {"Test Case", "Test Plan", "Test Suite", "Shared Steps", "Shared Parameter"}


class AdoApiError(RuntimeError):
    """Raised when ADO returns a non-success status. Carries the sanitized body."""

    def __init__(self, op: str, status: int, body: str):
        super().__init__(f"ADO [{op}] {status}: {body[:512]}")
        self.op = op
        self.status = status
        self.body = body


@dataclass
class AdoWorkItem:
    id: int
    work_item_type: str = ""
    title: str = ""
    description: str | None = None
    acceptance_criteria: str | None = None
    assigned_to: str | None = None
    state: str | None = None
    url: str = ""


@dataclass(frozen=True)
class TestCaseStep:
    action: str
    expected: str


@dataclass(frozen=True)
class LinkedTestCase:
    """A Test Case work item linked to a story in ADO (source of truth)."""
    id: int
    title: str
    preconditions: str
    steps: list[TestCaseStep]


@dataclass(frozen=True)
class ChangedFile:
    """One file touched by a PR or commit (the diff anchor)."""
    path: str
    change_type: str  # add | edit | delete | rename | edit, rename | seed


@dataclass(frozen=True)
class LinkedChange:
    """A PR or commit linked to a work item, enriched with a human title + browser URL."""
    kind: str   # "pr" | "commit"
    id: str     # PR id or commit sha
    title: str
    url: str    # Azure DevOps web URL for the changeset


@dataclass(frozen=True)
class AdoTestPlan:
    id: int
    name: str


@dataclass(frozen=True)
class AdoTestSuite:
    id: int
    name: str


@dataclass(frozen=True)
class TicketComment:
    """One discussion comment on a work item (for the report spotlight modal)."""
    author: str
    date: str | None
    html: str


@dataclass(frozen=True)
class TimelineEvent:
    """One derived history event on a work item (for the report spotlight modal)."""
    date: str | None
    actor: str
    kind: str  # created | state | assignment | comment | other
    detail: str


@dataclass
class ReportTicket:
    """Flat ticket shape used by the weekly HTML report (P5).

    Base fields come from the WIQL batch fetch; ``comments``/``timeline`` are populated
    lazily by ``enrich_tickets_for_report`` (hence this is not frozen)."""
    id: int
    title: str = ""
    work_item_type: str = ""
    state: str | None = None
    assigned_to: str | None = None
    issue_description: str | None = None
    fix: str | None = None
    closed_at: str | None = None
    created_by: str | None = None
    created_date: str | None = None
    description: str | None = None
    root_cause: str | None = None
    area_path: str | None = None
    comments: list[TicketComment] = field(default_factory=list)
    timeline: list[TimelineEvent] = field(default_factory=list)


def _patch_op(op: str, path: str, value: str) -> dict:
    return {"op": op, "path": path, "value": value}


def _build_steps_xml(steps: list[TestCaseStep]) -> str:
    """Microsoft.VSTS.TCM.Steps XML — byte-identical shape to the .NET BuildStepsXml."""
    parts = [f'<steps id="0" last="{len(steps) + 1}">']
    for i, s in enumerate(steps):
        step_id = i + 2
        action = html_lib.escape(s.action)
        expected = html_lib.escape(s.expected)
        parts.append(
            f'<step id="{step_id}" type="ActionStep">'
            f'<parameterizedString isformatted="true">&lt;DIV&gt;{action}&lt;/DIV&gt;</parameterizedString>'
            f'<parameterizedString isformatted="true">&lt;DIV&gt;{expected}&lt;/DIV&gt;</parameterizedString>'
            f'<description/></step>'
        )
    parts.append("</steps>")
    return "".join(parts)


_TAG_RE = re.compile(r"<[^>]+>")


def _strip_tags(s: str) -> str:
    return _TAG_RE.sub(" ", s or "").replace("&nbsp;", " ").strip()


def _parse_steps_xml(xml: str) -> list[TestCaseStep]:
    """Inverse of _build_steps_xml: Microsoft.VSTS.TCM.Steps XML → action/expected pairs.
    Each <step> holds two <parameterizedString> nodes (action, expected) of inner HTML."""
    if not xml or "<step" not in xml:
        return []
    try:
        root = ET.fromstring(xml)
    except ET.ParseError:
        return []
    steps: list[TestCaseStep] = []
    for step in root.iter("step"):
        ps = step.findall("parameterizedString")
        if len(ps) < 2:
            continue
        action = _strip_tags(html_lib.unescape(ps[0].text or ""))
        expected = _strip_tags(html_lib.unescape(ps[1].text or ""))
        if action or expected:
            steps.append(TestCaseStep(action=action, expected=expected))
    return steps


def _get_str(d: dict, key: str) -> str | None:
    v = d.get(key)
    return v if isinstance(v, str) else None


def _change_paths(changes: list) -> list[str]:
    """Pull file paths from PR/commit change entries — skip folders and deletes."""
    out: list[str] = []
    for c in changes or []:
        item = c.get("item") or {}
        if item.get("isFolder") or item.get("gitObjectType") == "tree":
            continue
        if str(c.get("changeType", "")).lower() == "delete":
            continue
        p = item.get("path")
        if isinstance(p, str) and p:
            out.append(p)
    return out


def _change_entries(changes: list) -> list[ChangedFile]:
    """Like _change_paths but keeps the change type and (unlike it) keeps deletes —
    a deleted file is still part of 'what changed' for the diff summary."""
    out: list[ChangedFile] = []
    seen: set[str] = set()
    for c in changes or []:
        item = c.get("item") or {}
        if item.get("isFolder") or item.get("gitObjectType") == "tree":
            continue
        p = item.get("path")
        if isinstance(p, str) and p and p not in seen:
            out.append(ChangedFile(path=p, change_type=str(c.get("changeType", "") or "").lower()))
            seen.add(p)
    return out


def _get_identity(d: dict, key: str) -> str | None:
    v = d.get(key)
    if isinstance(v, dict):
        dn = v.get("displayName")
        return dn if isinstance(dn, str) else None
    return None


def _basic_auth_header(pat: str) -> str:
    token = base64.b64encode(f":{pat}".encode("ascii")).decode("ascii")
    return f"Basic {token}"


class AzureDevOpsClient:
    """One client per process. Reuses a single AsyncClient for connection pooling."""

    # Short-TTL cache so one logical operation (validate → stamp title → analyze) doesn't
    # re-fetch the same work item several times. Expires quickly so re-runs still see edits.
    _WI_CACHE_TTL = 60.0  # seconds

    def __init__(self) -> None:
        self._http = httpx.AsyncClient(
            timeout=30.0,
            headers={
                "Authorization": _basic_auth_header(settings.adoaccess_pat),
                "Accept": "application/json",
            },
        )
        self._wi_cache: dict[int, tuple[float, AdoWorkItem]] = {}

    async def close(self) -> None:
        await self._http.aclose()

    # ── Discovery (PAT → orgs / projects / area paths) ─────────────────────
    # These endpoints don't need the project-scoped base URL; they hit the global ADO
    # services (vssps for profile/accounts, dev.azure.com/{org} for projects + area-paths).

    async def discover_organizations(self) -> list[str]:
        """Member-of endpoint: profile/me → accounts(memberId)."""
        prof_url = "https://app.vssps.visualstudio.com/_apis/profile/profiles/me?api-version=7.1-preview.3"
        pr = await self._http.get(prof_url)
        await self._raise_if_failed(pr, "Profile /me")
        member_id = pr.json().get("id")
        if not member_id:
            return []
        acc_url = (
            "https://app.vssps.visualstudio.com/_apis/accounts"
            f"?memberId={member_id}&api-version=7.1-preview.1"
        )
        ar = await self._http.get(acc_url)
        await self._raise_if_failed(ar, "Accounts (orgs)")
        return [a["accountName"] for a in ar.json().get("value", []) if a.get("accountName")]

    async def discover_projects(self, organization: str) -> list[dict[str, str]]:
        url = f"https://dev.azure.com/{organization}/_apis/projects?api-version=7.1&$top=200"
        r = await self._http.get(url)
        await self._raise_if_failed(r, f"List projects ({organization})")
        return [
            {"id": str(p["id"]), "name": str(p["name"])}
            for p in r.json().get("value", []) if p.get("name")
        ]

    async def discover_area_paths(self, organization: str, project: str) -> list[str]:
        """Recursively flatten the Areas classification tree into a list of full paths."""
        url = (
            f"https://dev.azure.com/{organization}/{quote(project)}"
            f"/_apis/wit/classificationnodes/Areas?$depth=10&api-version=7.1"
        )
        r = await self._http.get(url)
        await self._raise_if_failed(r, f"Area paths ({project})")
        root = r.json()

        out: list[str] = []

        def walk(node: dict, prefix: str) -> None:
            name = node.get("name", "")
            path = name if not prefix else f"{prefix}\\{name}"
            out.append(path)
            for child in node.get("children", []) or []:
                walk(child, path)

        walk(root, "")
        return out

    # ── reads ───────────────────────────────────────────────────────────────

    async def get_work_item(
        self, work_item_id: int, *, force_refresh: bool = False,
    ) -> AdoWorkItem:
        if not force_refresh:
            hit = self._wi_cache.get(work_item_id)
            if hit is not None and (time.monotonic() - hit[0]) < self._WI_CACHE_TTL:
                return hit[1]

        fields = ",".join(WORK_ITEM_FIELDS)
        url = (
            f"{settings.ado_base_url}/_apis/wit/workitems/{work_item_id}"
            f"?fields={fields}&api-version={settings.ado_api_version}"
        )
        r = await self._http.get(url)
        await self._raise_if_failed(r, f"Get work item {work_item_id}")
        data = r.json()
        f = data.get("fields", {})
        identity = f.get("System.AssignedTo")
        assigned_to: str | None = None
        if isinstance(identity, dict):
            assigned_to = identity.get("displayName")
        wi = AdoWorkItem(
            id=int(data.get("id", work_item_id)),
            title=f.get("System.Title", "") or "",
            description=f.get("System.Description"),
            acceptance_criteria=f.get("Microsoft.VSTS.Common.AcceptanceCriteria"),
            work_item_type=f.get("System.WorkItemType", "") or "",
            state=f.get("System.State"),
            assigned_to=assigned_to,
            url=data.get("url", "") or "",
        )
        self._wi_cache[work_item_id] = (time.monotonic(), wi)
        return wi

    async def search_work_items(self, query: str, top: int = 20) -> list[AdoWorkItem]:
        """
        Title CONTAINS search. If the user typed a numeric ID, fetch that work item directly
        (matches the fix we shipped in .NET so id-based picks work in the autocomplete).
        """
        trimmed = (query or "").strip()
        if not trimmed:
            return []
        if trimmed.isdigit():
            try:
                return [await self.get_work_item(int(trimmed))]
            except AdoApiError:
                return []

        wiql_body = {
            "query": (
                f"SELECT [System.Id] FROM WorkItems WHERE "
                f"[System.TeamProject] = '{settings.ado_project}' "
                f"AND [System.Title] CONTAINS '{trimmed.replace(chr(39), chr(39) + chr(39))}' "
                f"ORDER BY [System.ChangedDate] DESC"
            )
        }
        url = f"{settings.ado_base_url}/_apis/wit/wiql?$top={top}&api-version={settings.ado_api_version}"
        r = await self._http.post(url, json=wiql_body)
        await self._raise_if_failed(r, "WIQL search")
        ids = [int(x["id"]) for x in r.json().get("workItems", [])][:top]
        if not ids:
            return []
        return [await self.get_work_item(i) for i in ids]

    async def get_linked_test_cases(self, story_id: int) -> list[LinkedTestCase]:
        """Test Case work items linked to a story via TestedBy relations, with their
        steps parsed from the Microsoft.VSTS.TCM.Steps XML. These are the real ADO
        Test Cases (source of truth), as opposed to local Author-Tests drafts."""
        url = (
            f"{settings.ado_base_url}/_apis/wit/workitems/{story_id}"
            f"?$expand=relations&api-version={settings.ado_api_version}"
        )
        r = await self._http.get(url)
        await self._raise_if_failed(r, f"Get work item relations {story_id}")

        ids: list[int] = []
        for rel in r.json().get("relations", []) or []:
            if "TestedBy" not in str(rel.get("rel") or ""):
                continue
            m = re.search(r"/work[iI]tems?/(\d+)\b", str(rel.get("url") or ""))
            if m:
                ids.append(int(m.group(1)))
        # de-dupe, preserve order
        ids = list(dict.fromkeys(ids))
        if not ids:
            return []

        out: list[LinkedTestCase] = []
        fields_csv = "System.Title,System.WorkItemType,Microsoft.VSTS.TCM.Steps"
        for chunk_start in range(0, len(ids), 200):
            chunk = ids[chunk_start:chunk_start + 200]
            batch_url = (
                f"{settings.ado_base_url}/_apis/wit/workitems?ids={','.join(map(str, chunk))}"
                f"&fields={fields_csv}&api-version={settings.ado_api_version}"
            )
            br = await self._http.get(batch_url)
            await self._raise_if_failed(br, "Linked test case batch fetch")
            for e in br.json().get("value", []):
                f = e.get("fields", {})
                if (_get_str(f, "System.WorkItemType") or "") != "Test Case":
                    continue
                out.append(LinkedTestCase(
                    id=int(e["id"]),
                    title=_get_str(f, "System.Title") or "",
                    preconditions="",
                    steps=_parse_steps_xml(_get_str(f, "Microsoft.VSTS.TCM.Steps") or ""),
                ))
        return out

    # ── Work-item hierarchy (Epic/Feature → child stories) ─────────────────

    async def _direct_children(self, parent_id: int) -> list[AdoWorkItem]:
        """One level of Hierarchy-Forward children of a work item, as lightweight AdoWorkItems
        (id/title/type/state). Modeled on get_linked_test_cases' relations+batch-fetch pattern."""
        url = (
            f"{settings.ado_base_url}/_apis/wit/workitems/{parent_id}"
            f"?$expand=relations&api-version={settings.ado_api_version}"
        )
        r = await self._http.get(url)
        await self._raise_if_failed(r, f"Get child relations {parent_id}")
        ids: list[int] = []
        for rel in r.json().get("relations", []) or []:
            if "Hierarchy-Forward" not in str(rel.get("rel") or ""):
                continue
            m = re.search(r"/work[iI]tems?/(\d+)\b", str(rel.get("url") or ""))
            if m:
                ids.append(int(m.group(1)))
        ids = list(dict.fromkeys(ids))
        if not ids:
            return []
        out: list[AdoWorkItem] = []
        fields_csv = "System.Title,System.WorkItemType,System.State"
        for chunk_start in range(0, len(ids), 200):
            chunk = ids[chunk_start:chunk_start + 200]
            batch_url = (
                f"{settings.ado_base_url}/_apis/wit/workitems?ids={','.join(map(str, chunk))}"
                f"&fields={fields_csv}&api-version={settings.ado_api_version}"
            )
            br = await self._http.get(batch_url)
            await self._raise_if_failed(br, "Child work item batch fetch")
            for e in br.json().get("value", []):
                f = e.get("fields", {})
                out.append(AdoWorkItem(
                    id=int(e["id"]),
                    title=_get_str(f, "System.Title") or "",
                    work_item_type=_get_str(f, "System.WorkItemType") or "",
                    state=_get_str(f, "System.State"),
                ))
        return out

    async def get_child_work_items(
        self, parent_id: int, *, recursive: bool = True,
        max_depth: int = 5, max_nodes: int = 500, max_leaves: int = 200,
    ) -> tuple[list[AdoWorkItem], bool]:
        """Children of an Epic/Feature. When recursive, BFS down the hierarchy — descending through
        CONTAINER_WORK_ITEM_TYPES (Epic/Feature) and collecting TESTABLE_LEAF_TYPES (User Story/Bug/
        Issue) as leaves. Returns (work_items, truncated); truncated is True if a cap was hit."""
        if not recursive:
            return await self._direct_children(parent_id), False

        leaves: list[AdoWorkItem] = []
        seen: set[int] = {parent_id}
        truncated = False
        nodes_fetched = 0
        frontier: list[tuple[int, int]] = [(parent_id, 0)]
        while frontier:
            next_frontier: list[tuple[int, int]] = []
            for pid, depth in frontier:
                children = await self._direct_children(pid)
                nodes_fetched += len(children)
                for c in children:
                    if c.id in seen:
                        continue
                    seen.add(c.id)
                    if c.work_item_type in CONTAINER_WORK_ITEM_TYPES:
                        # Descend into containers; a container is never itself a batch target.
                        if depth + 1 < max_depth:
                            next_frontier.append((c.id, depth + 1))
                        else:
                            truncated = True  # hit the depth cap with an unexpanded container
                        continue
                    if c.work_item_type not in NON_TESTABLE_WORK_ITEM_TYPES:
                        if len(leaves) >= max_leaves:
                            truncated = True
                            continue
                        leaves.append(c)
                    # else: a pure test artifact (Test Case/Plan/Suite) — never a batch target.
                if nodes_fetched >= max_nodes:
                    truncated = True
                    next_frontier = []
                    break
            frontier = next_frontier
        return leaves, truncated

    # ── Test Plans / Suites ────────────────────────────────────────────────

    async def get_test_plans(self) -> list[AdoTestPlan]:
        url = f"{settings.ado_base_url}/_apis/testplan/plans?api-version={settings.ado_api_version}"
        r = await self._http.get(url)
        await self._raise_if_failed(r, "Get test plans")
        return [
            AdoTestPlan(id=int(e["id"]), name=str(e.get("name") or ""))
            for e in r.json().get("value", [])
        ]

    async def get_test_suites(self, plan_id: int) -> list[AdoTestSuite]:
        url = (
            f"{settings.ado_base_url}/_apis/testplan/plans/{plan_id}"
            f"/suites?api-version={settings.ado_api_version}"
        )
        r = await self._http.get(url)
        await self._raise_if_failed(r, f"Get suites for plan {plan_id}")
        return [
            AdoTestSuite(id=int(e["id"]), name=str(e.get("name") or ""))
            for e in r.json().get("value", [])
        ]

    # ── Test Case create + suite add + Tested-By link ──────────────────────

    async def create_test_case(
        self, title: str, preconditions: str, steps: list[TestCaseStep],
    ) -> int:
        body: list[dict] = [
            _patch_op("add", "/fields/System.Title", title),
            _patch_op("add", "/fields/Microsoft.VSTS.TCM.Steps", _build_steps_xml(steps)),
        ]
        if preconditions and preconditions.strip():
            body.append(_patch_op(
                "add",
                "/fields/System.Description",
                f"<div><b>Preconditions:</b> {html_lib.escape(preconditions)}</div>",
            ))

        url = (
            f"{settings.ado_base_url}/_apis/wit/workitems/$Test%20Case"
            f"?api-version={settings.ado_api_version}"
        )
        r = await self._http.post(
            url, content=json.dumps(body),
            headers={"Content-Type": "application/json-patch+json"},
        )
        await self._raise_if_failed(r, "Create Test Case")
        return int(r.json()["id"])

    async def add_test_case_to_suite(
        self, plan_id: int, suite_id: int, test_case_id: int,
    ) -> None:
        # Modern `testplan` REST shape — { "workItem": { "id": "<id>" } }.
        body = [{"workItem": {"id": str(test_case_id)}, "pointAssignments": []}]
        url = (
            f"{settings.ado_base_url}/_apis/testplan/Plans/{plan_id}/Suites/{suite_id}"
            f"/TestCase?api-version={settings.ado_api_version}"
        )
        r = await self._http.post(url, json=body)
        await self._raise_if_failed(r, f"Add TC {test_case_id} to suite {suite_id}")

    async def link_tested_by(self, story_id: int, test_case_id: int) -> None:
        tc_url = (
            f"https://dev.azure.com/{settings.ado_organization}/"
            f"{quote(settings.ado_project)}/_apis/wit/workItems/{test_case_id}"
        )
        body = [{
            "op": "add",
            "path": "/relations/-",
            "value": {"rel": "Microsoft.VSTS.Common.TestedBy-Reverse", "url": tc_url},
        }]
        url = (
            f"{settings.ado_base_url}/_apis/wit/workitems/{story_id}"
            f"?api-version={settings.ado_api_version}"
        )
        r = await self._http.patch(
            url, content=json.dumps(body),
            headers={"Content-Type": "application/json-patch+json"},
        )
        await self._raise_if_failed(r, f"Link TC {test_case_id} to story {story_id}")

    # ── Bug create + Related link ──────────────────────────────────────────

    async def create_bug(
        self,
        title: str,
        severity: str,         # "Critical" | "High" | "Medium" | "Low"
        preconditions: str,
        repro_steps: list[str],
        expected: str,
        actual: str,
        suggested_fix: str,
    ) -> int:
        # Repro in Mahmood's house anatomy (mirrors the `bugger` skill): environment-first
        # <ul> steps, then a bold OBSERVATION block with the actual result, then a screenshot
        # placeholder for the user to attach in the ADO UI.
        repro_html_parts: list[str] = []
        if preconditions and preconditions.strip():
            repro_html_parts.append(
                f"<div><b>Preconditions:</b> {html_lib.escape(preconditions)}</div>"
            )
        repro_html_parts.append("<ul>")
        for s in repro_steps:
            repro_html_parts.append(f"<li>{html_lib.escape(s)}</li>")
        repro_html_parts.append("</ul>")
        if actual and actual.strip():
            repro_html_parts.append(
                "<div><b>OBSERVATION:</b></div>"
                f"<div><ul><li>{html_lib.escape(actual)}</li></ul></div>"
            )
        repro_html_parts.append("<div><i>[Attach screenshot/GIF here]</i></div>")
        repro_html = "".join(repro_html_parts)

        # Acceptance criteria phrased the way Mahmood closes the loop.
        acceptance_html = ""
        if expected and expected.strip():
            acceptance_html = (
                "<div>I know this bug is resolved when:</div>"
                f"<div><ul><li>{html_lib.escape(expected)}</li></ul></div>"
            )

        # SystemInfo is reserved for test creds (DmsDealerId / Username / Password); we don't
        # collect those, so use it to carry the optional dev-facing suggested fix instead.
        sys_info = ""
        if suggested_fix and suggested_fix.strip():
            sys_info = f"<div><b>Suggested fix:</b> {html_lib.escape(suggested_fix)}</div>"

        sev_value = {
            "Critical": "1 - Critical",
            "High":     "2 - High",
            "Medium":   "3 - Medium",
            "Low":      "4 - Low",
        }.get(severity, "3 - Medium")

        body = [
            _patch_op("add", "/fields/System.Title", title),
            _patch_op("add", "/fields/Microsoft.VSTS.TCM.ReproSteps", repro_html),
            _patch_op("add", "/fields/Microsoft.VSTS.Common.Severity", sev_value),
            # House-style standing fields (from the bug-suite field reference).
            _patch_op("add", "/fields/System.AreaPath", settings.ado_bug_area_path),
            _patch_op("add", "/fields/Microsoft.VSTS.Common.Priority", 2),
            _patch_op("add", "/fields/Microsoft.VSTS.Common.ValueArea", "Business"),
            _patch_op("add", "/fields/Custom.QA", settings.ado_qa_owner),
            _patch_op("add", "/fields/Custom.FoundWith", "Manual Test"),
        ]
        if acceptance_html:
            body.append(_patch_op(
                "add", "/fields/Microsoft.VSTS.Common.AcceptanceCriteria", acceptance_html,
            ))
        if sys_info:
            body.append(_patch_op("add", "/fields/Microsoft.VSTS.TCM.SystemInfo", sys_info))

        url = (
            f"{settings.ado_base_url}/_apis/wit/workitems/$Bug"
            f"?api-version={settings.ado_api_version}"
        )
        r = await self._http.post(
            url, content=json.dumps(body),
            headers={"Content-Type": "application/json-patch+json"},
        )
        await self._raise_if_failed(r, "Create Bug")
        return int(r.json()["id"])

    async def link_related(self, from_id: int, to_id: int) -> None:
        to_url = (
            f"https://dev.azure.com/{settings.ado_organization}/"
            f"{quote(settings.ado_project)}/_apis/wit/workItems/{to_id}"
        )
        body = [{
            "op": "add",
            "path": "/relations/-",
            "value": {"rel": "System.LinkTypes.Related", "url": to_url},
        }]
        url = (
            f"{settings.ado_base_url}/_apis/wit/workitems/{from_id}"
            f"?api-version={settings.ado_api_version}"
        )
        r = await self._http.patch(
            url, content=json.dumps(body),
            headers={"Content-Type": "application/json-patch+json"},
        )
        await self._raise_if_failed(r, f"Link {from_id} <-> {to_id} as Related")

    # ── Reports query (P5) ─────────────────────────────────────────────────

    # System.ClosedDate isn't present in every process template (TF51535 on Your Project).
    # Skip it — HTML builder's ticket sort handles closed_at=None just fine.
    REPORT_FIELDS = [
        "System.Id", "System.Title", "System.WorkItemType", "System.AssignedTo",
        "System.State", "System.Description", "System.History", "System.AreaPath",
        "System.CreatedBy", "System.CreatedDate",
        "Microsoft.VSTS.TCM.ReproSteps", "Microsoft.VSTS.Common.ResolvedReason",
        "Microsoft.VSTS.Common.AcceptanceCriteria", "Custom.RootCauseDetails",
    ]

    # Fallback work-item types when the caller passes none (matches the historical set
    # plus Defect, which the board/spotlight tallies expect).
    _DEFAULT_REPORT_TYPES = ["User Story", "Bug", "Defect", "Task", "Issue", "Feature"]

    async def get_tickets_in_range(
        self, from_utc, to_utc, boards: list[str], types: list[str] | None = None,
    ) -> list[ReportTicket]:
        # [System.ChangedDate] is date-precision in WIQL — supplying a time triggers
        # "You cannot supply a time with the date when running a query using date precision."
        from_s = from_utc.strftime("%Y-%m-%d")
        to_s = to_utc.strftime("%Y-%m-%d")

        esc = lambda v: v.replace("'", "''")  # noqa: E731
        area_clause = ""
        if boards:
            parts = [f"[System.AreaPath] UNDER '{esc(b)}'" for b in boards]
            area_clause = " AND (" + " OR ".join(parts) + ")"

        chosen_types = [t.strip() for t in (types or []) if t.strip()] or self._DEFAULT_REPORT_TYPES
        types_csv = ", ".join(f"'{esc(t)}'" for t in chosen_types)

        wiql_body = {
            "query": (
                f"SELECT [System.Id] FROM WorkItems "
                f"WHERE [System.TeamProject] = '{settings.ado_project}' "
                f"AND [System.ChangedDate] >= '{from_s}' "
                f"AND [System.ChangedDate] <= '{to_s}' "
                f"AND [System.WorkItemType] IN ({types_csv}) "
                f"{area_clause}"
                f" ORDER BY [System.ChangedDate] DESC"
            )
        }

        url = f"{settings.ado_base_url}/_apis/wit/wiql?api-version={settings.ado_api_version}"
        r = await self._http.post(url, json=wiql_body)
        await self._raise_if_failed(r, "Report WIQL")
        ids = [int(x["id"]) for x in r.json().get("workItems", [])]
        if not ids:
            return []

        fields_csv = ",".join(self.REPORT_FIELDS)
        results: list[ReportTicket] = []
        for chunk_start in range(0, len(ids), 200):
            chunk = ids[chunk_start:chunk_start + 200]
            batch_url = (
                f"{settings.ado_base_url}/_apis/wit/workitems?ids={','.join(map(str, chunk))}"
                f"&fields={fields_csv}&api-version={settings.ado_api_version}"
            )
            br = await self._http.get(batch_url)
            await self._raise_if_failed(br, "Report batch fetch")
            for e in br.json().get("value", []):
                f = e.get("fields", {})
                repro = _get_str(f, "Microsoft.VSTS.TCM.ReproSteps")
                desc = _get_str(f, "System.Description")
                resolved = _get_str(f, "Microsoft.VSTS.Common.ResolvedReason")
                history = _get_str(f, "System.History")
                results.append(ReportTicket(
                    id=int(e["id"]),
                    title=_get_str(f, "System.Title") or "",
                    work_item_type=_get_str(f, "System.WorkItemType") or "",
                    state=_get_str(f, "System.State"),
                    assigned_to=_get_identity(f, "System.AssignedTo"),
                    issue_description=repro if repro else desc,
                    fix=resolved if resolved else history,
                    closed_at=_get_str(f, "System.ClosedDate"),
                    created_by=_get_identity(f, "System.CreatedBy"),
                    created_date=_get_str(f, "System.CreatedDate"),
                    # Description tab prefers the real Description; bugs usually only
                    # carry repro steps, so fall back to those.
                    description=desc if desc else repro,
                    root_cause=_get_str(f, "Custom.RootCauseDetails"),
                    area_path=_get_str(f, "System.AreaPath"),
                ))
        return results

    # ── Ticket enrichment for the report spotlight modal ───────────────────────

    async def get_work_item_comments(self, work_item_id: int) -> list[TicketComment]:
        """Discussion comments, oldest→newest. Empty list on any failure."""
        url = (
            f"{settings.ado_base_url}/_apis/wit/workItems/{work_item_id}/comments"
            f"?api-version=7.0-preview.3"
        )
        try:
            r = await self._http.get(url)
            r.raise_for_status()
        except Exception:  # noqa: BLE001 — enrichment is best-effort
            return []
        out: list[TicketComment] = []
        for c in r.json().get("comments", []):
            text = _get_str(c, "text") or _get_str(c, "renderedText") or ""
            if not text.strip():
                continue
            out.append(TicketComment(
                author=_get_identity(c, "createdBy") or "Unknown",
                date=_get_str(c, "createdDate"),
                html=text,
            ))
        out.sort(key=lambda x: x.date or "")
        return out

    async def get_work_item_updates(self, work_item_id: int) -> list[TimelineEvent]:
        """Field-change history → derived timeline events (created / state / assignment).
        Empty list on any failure."""
        url = (
            f"{settings.ado_base_url}/_apis/wit/workItems/{work_item_id}/updates"
            f"?api-version=7.0"
        )
        try:
            r = await self._http.get(url)
            r.raise_for_status()
        except Exception:  # noqa: BLE001
            return []
        events: list[TimelineEvent] = []
        for u in r.json().get("value", []):
            fields = u.get("fields") or {}
            actor = _get_identity(u, "revisedBy") or "Unknown"
            date = _get_str(u, "revisedDate")
            created = fields.get("System.CreatedDate")
            if isinstance(created, dict) and created.get("newValue") and "oldValue" not in created:
                cb = fields.get("System.CreatedBy") or {}
                who = (cb.get("newValue") or {}).get("displayName") if isinstance(cb, dict) else None
                events.append(TimelineEvent(
                    date=_get_str(created, "newValue") or date,
                    actor=who or actor, kind="created", detail="Work item created",
                ))
            st = fields.get("System.State")
            if isinstance(st, dict) and st.get("newValue"):
                old = st.get("oldValue")
                detail = f"{old} → {st['newValue']}" if old else f"Set to {st['newValue']}"
                events.append(TimelineEvent(date=date, actor=actor, kind="state",
                                            detail=f"State {detail}"))
            asg = fields.get("System.AssignedTo")
            if isinstance(asg, dict) and ("newValue" in asg or "oldValue" in asg):
                old = (asg.get("oldValue") or {}).get("displayName") if isinstance(asg.get("oldValue"), dict) else None
                new = (asg.get("newValue") or {}).get("displayName") if isinstance(asg.get("newValue"), dict) else None
                detail = f"{old or 'Unassigned'} → {new or 'Unassigned'}"
                events.append(TimelineEvent(date=date, actor=actor, kind="assignment",
                                            detail=f"Assigned to {detail}"))
        return events

    async def enrich_tickets_for_report(
        self, tickets: list[ReportTicket], *, limit: int = 150, concurrency: int = 8,
    ) -> list[ReportTicket]:
        """Attach comments + timeline (incl. comments folded into the timeline) to each
        ticket, in place. Bounded by ``limit`` (logged if exceeded) and ``concurrency``."""
        target = tickets[:limit]
        if len(tickets) > limit:
            log.warning("Report enrichment capped at %d of %d tickets", limit, len(tickets))
        sem = asyncio.Semaphore(concurrency)

        async def _one(t: ReportTicket) -> None:
            async with sem:
                comments, timeline = await asyncio.gather(
                    self.get_work_item_comments(t.id),
                    self.get_work_item_updates(t.id),
                )
            t.comments = comments
            for c in comments:
                timeline.append(TimelineEvent(
                    date=c.date, actor=c.author, kind="comment", detail="Comment added",
                ))
            timeline.sort(key=lambda e: e.date or "")
            t.timeline = timeline

        await asyncio.gather(*(_one(t) for t in target))
        return tickets

    # ── Git repo reads + Code Search (code-analysis phase) ─────────────────────

    _repo_id_cache: dict[str, str] = {}

    async def list_repositories(self) -> list[dict[str, str]]:
        url = f"{settings.ado_base_url}/_apis/git/repositories?api-version={settings.ado_api_version}"
        r = await self._http.get(url)
        await self._raise_if_failed(r, "List repositories")
        return [{"id": str(e["id"]), "name": str(e.get("name") or "")} for e in r.json().get("value", [])]

    async def resolve_repository_id(self, name: str | None = None) -> str:
        """Resolve a repo name → id (cached). Defaults to settings.ado_repository."""
        repo_name = (name or settings.ado_repository or "").strip()
        if not repo_name:
            raise AdoApiError("Resolve repo", 400, "No repository configured (ado.repository).")
        cached = self._repo_id_cache.get(repo_name.lower())
        if cached:
            return cached
        for repo in await self.list_repositories():
            self._repo_id_cache[repo["name"].lower()] = repo["id"]
        rid = self._repo_id_cache.get(repo_name.lower())
        if not rid:
            raise AdoApiError("Resolve repo", 404, f"Repository '{repo_name}' not found.")
        return rid

    async def get_work_item_git_refs(self, work_item_id: int) -> dict[str, list[str]]:
        """Parse a work item's ArtifactLink relations → linked PR ids + commit SHAs."""
        url = (
            f"{settings.ado_base_url}/_apis/wit/workitems/{work_item_id}"
            f"?$expand=relations&api-version={settings.ado_api_version}"
        )
        r = await self._http.get(url)
        await self._raise_if_failed(r, f"Get work item relations {work_item_id}")
        pr_ids: list[str] = []
        commit_shas: list[str] = []
        from urllib.parse import unquote
        for rel in r.json().get("relations", []) or []:
            if rel.get("rel") != "ArtifactLink":
                continue
            raw = unquote(str(rel.get("url") or ""))
            # vstfs:///Git/PullRequestId/{proj}/{repo}/{prId}  |  vstfs:///Git/Commit/{proj}/{repo}/{sha}
            if "/Git/PullRequestId/" in raw:
                pr_ids.append(raw.rsplit("/", 1)[-1])
            elif "/Git/Commit/" in raw:
                commit_shas.append(raw.rsplit("/", 1)[-1])
        return {"pull_request_ids": pr_ids, "commit_shas": commit_shas}

    async def get_linked_changes(self, work_item_id: int) -> list[LinkedChange]:
        """Linked PRs/commits for a work item, enriched with a title + ADO web URL so the UI
        can list them (title + id) and deep-link to the changeset. Best-effort: never raises;
        falls back to an id-only label if the detail lookup fails."""
        try:
            refs = await self.get_work_item_git_refs(work_item_id)
        except AdoApiError:
            return []
        pr_ids = refs.get("pull_request_ids", [])
        commit_shas = refs.get("commit_shas", [])
        if not pr_ids and not commit_shas:
            return []
        try:
            rid: str | None = await self.resolve_repository_id()
        except AdoApiError:
            rid = None
        repo = quote(settings.ado_repository)
        out: list[LinkedChange] = []

        for pr in pr_ids:
            title = f"Pull Request #{pr}"
            if rid:
                try:
                    r = await self._http.get(
                        f"{settings.ado_base_url}/_apis/git/repositories/{rid}"
                        f"/pullRequests/{pr}?api-version={settings.ado_api_version}"
                    )
                    if r.status_code < 400:
                        t = str(r.json().get("title") or "").strip()
                        if t:
                            title = t
                except httpx.HTTPError:
                    pass
            out.append(LinkedChange(
                kind="pr", id=str(pr), title=title,
                url=f"{settings.ado_base_url}/_git/{repo}/pullrequest/{pr}",
            ))

        for sha in commit_shas:
            title = f"Commit {sha[:8]}"
            if rid:
                try:
                    r = await self._http.get(
                        f"{settings.ado_base_url}/_apis/git/repositories/{rid}"
                        f"/commits/{sha}?api-version={settings.ado_api_version}"
                    )
                    if r.status_code < 400:
                        msg = str(r.json().get("comment") or "").strip()
                        if msg:
                            title = msg.splitlines()[0][:120]
                except httpx.HTTPError:
                    pass
            out.append(LinkedChange(
                kind="commit", id=str(sha), title=title,
                url=f"{settings.ado_base_url}/_git/{repo}/commit/{sha}",
            ))
        return out

    async def get_pull_request_changed_files(self, pr_id: int | str, repo_id: str | None = None) -> list[str]:
        rid = repo_id or await self.resolve_repository_id()
        base = f"{settings.ado_base_url}/_apis/git/repositories/{rid}/pullRequests/{pr_id}"
        it = await self._http.get(f"{base}/iterations?api-version={settings.ado_api_version}")
        await self._raise_if_failed(it, f"PR {pr_id} iterations")
        iters = it.json().get("value", [])
        if not iters:
            return []
        last = max(int(i["id"]) for i in iters)
        ch = await self._http.get(f"{base}/iterations/{last}/changes?api-version={settings.ado_api_version}")
        await self._raise_if_failed(ch, f"PR {pr_id} changes")
        return _change_paths(ch.json().get("changeEntries", []))

    async def get_commit_changed_files(self, sha: str, repo_id: str | None = None) -> list[str]:
        rid = repo_id or await self.resolve_repository_id()
        url = (
            f"{settings.ado_base_url}/_apis/git/repositories/{rid}/commits/{sha}/changes"
            f"?api-version={settings.ado_api_version}"
        )
        r = await self._http.get(url)
        await self._raise_if_failed(r, f"Commit {sha} changes")
        return _change_paths(r.json().get("changes", []))

    async def get_pull_request_diff(self, pr_id: int | str, repo_id: str | None = None) -> list[ChangedFile]:
        """Changed files (+ change type) of a PR's latest iteration — the diff anchor."""
        rid = repo_id or await self.resolve_repository_id()
        base = f"{settings.ado_base_url}/_apis/git/repositories/{rid}/pullRequests/{pr_id}"
        it = await self._http.get(f"{base}/iterations?api-version={settings.ado_api_version}")
        await self._raise_if_failed(it, f"PR {pr_id} iterations")
        iters = it.json().get("value", [])
        if not iters:
            return []
        last = max(int(i["id"]) for i in iters)
        ch = await self._http.get(f"{base}/iterations/{last}/changes?api-version={settings.ado_api_version}")
        await self._raise_if_failed(ch, f"PR {pr_id} changes")
        return _change_entries(ch.json().get("changeEntries", []))

    async def get_commit_diff(self, sha: str, repo_id: str | None = None) -> list[ChangedFile]:
        """Changed files (+ change type) of a single commit — the diff anchor."""
        rid = repo_id or await self.resolve_repository_id()
        url = (
            f"{settings.ado_base_url}/_apis/git/repositories/{rid}/commits/{sha}/changes"
            f"?api-version={settings.ado_api_version}"
        )
        r = await self._http.get(url)
        await self._raise_if_failed(r, f"Commit {sha} diff")
        return _change_entries(r.json().get("changes", []))

    async def get_commits_for_path(self, path: str, top: int = 5, repo_id: str | None = None) -> list[str]:
        """Most recent commit SHAs that touched a given file path (no-ref fallback anchor)."""
        rid = repo_id or await self.resolve_repository_id()
        url = (
            f"{settings.ado_base_url}/_apis/git/repositories/{rid}/commits"
            f"?searchCriteria.itemPath={quote(path)}&searchCriteria.$top={top}"
            f"&api-version={settings.ado_api_version}"
        )
        r = await self._http.get(url)
        await self._raise_if_failed(r, f"Commits for {path}")
        return [str(c.get("commitId")) for c in r.json().get("value", []) if c.get("commitId")]

    async def get_file_content(
        self, path: str, ref: str | None = None, repo_id: str | None = None, version_type: str = "branch",
    ) -> str:
        """Fetch a file's text. ref = branch name (default settings.ado_default_branch) or commit sha."""
        rid = repo_id or await self.resolve_repository_id()
        version = ref or settings.ado_default_branch
        url = (
            f"{settings.ado_base_url}/_apis/git/repositories/{rid}/items"
            f"?path={quote(path)}&includeContent=true"
            f"&versionDescriptor.version={quote(version)}"
            f"&versionDescriptor.versionType={version_type}"
            f"&api-version={settings.ado_api_version}"
        )
        r = await self._http.get(url, headers={"Accept": "application/json"})
        await self._raise_if_failed(r, f"Get file {path}")
        try:
            return str(r.json().get("content", "") or "")
        except ValueError:
            return r.text  # some responses return raw text

    async def search_code(self, text: str, top: int = 20, repo_id: str | None = None) -> list[str]:
        """Code Search (almsearch host). Returns matching file paths in the configured repo.
        Raises AdoApiError if the Code Search extension is unavailable (caller may fall back)."""
        org = settings.ado_organization.strip("/")
        url = (
            f"https://almsearch.dev.azure.com/{org}/{quote(settings.ado_project)}"
            f"/_apis/search/codesearchresults?api-version={settings.ado_api_version}"
        )
        body = {
            "searchText": text,
            "$top": top,
            "filters": {"Project": [settings.ado_project], "Repository": [settings.ado_repository]},
        }
        r = await self._http.post(url, json=body)
        await self._raise_if_failed(r, "Code Search")
        paths: list[str] = []
        for res in r.json().get("results", []) or []:
            p = res.get("path")
            if isinstance(p, str):
                paths.append(p)
        return paths

    # ── error helper ────────────────────────────────────────────────────────

    async def _raise_if_failed(self, r: httpx.Response, op: str) -> None:
        if r.is_success:
            return
        body = sanitize(r.text, settings.adoaccess_pat)
        # ADO often returns { "message": "..." } JSON on errors — pull it for cleaner messages.
        try:
            j = json.loads(body)
            if isinstance(j, dict) and "message" in j:
                raise AdoApiError(op, r.status_code, str(j["message"]))
        except (ValueError, TypeError):
            pass
        raise AdoApiError(op, r.status_code, body)


# Module-level singleton — created lazily so import-time doesn't open a connection pool.
_client: AzureDevOpsClient | None = None


def get_ado_client() -> AzureDevOpsClient:
    global _client
    if _client is None:
        _client = AzureDevOpsClient()
    return _client
