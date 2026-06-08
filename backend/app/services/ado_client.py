"""
Azure DevOps REST client — PAT (Basic) auth. Async via httpx.AsyncClient.

P1 implements the read paths used by Diagnostics + the work-item picker:
  - get_work_item(id)
  - search_work_items(q)
P3/P4/P5 add the write methods (Test Case / Bug create + suite + link, plus report query).
"""
from __future__ import annotations

import base64
import html as html_lib
import json
import logging
from dataclasses import dataclass
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
class AdoTestPlan:
    id: int
    name: str


@dataclass(frozen=True)
class AdoTestSuite:
    id: int
    name: str


@dataclass(frozen=True)
class ReportTicket:
    """Flat ticket shape used by the weekly HTML report (P5)."""
    id: int
    title: str = ""
    work_item_type: str = ""
    state: str | None = None
    assigned_to: str | None = None
    issue_description: str | None = None
    fix: str | None = None
    closed_at: str | None = None


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


def _get_str(d: dict, key: str) -> str | None:
    v = d.get(key)
    return v if isinstance(v, str) else None


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

    def __init__(self) -> None:
        self._http = httpx.AsyncClient(
            timeout=30.0,
            headers={
                "Authorization": _basic_auth_header(settings.adoaccess_pat),
                "Accept": "application/json",
            },
        )

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

    async def get_work_item(self, work_item_id: int) -> AdoWorkItem:
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
        return AdoWorkItem(
            id=int(data.get("id", work_item_id)),
            title=f.get("System.Title", "") or "",
            description=f.get("System.Description"),
            acceptance_criteria=f.get("Microsoft.VSTS.Common.AcceptanceCriteria"),
            work_item_type=f.get("System.WorkItemType", "") or "",
            state=f.get("System.State"),
            assigned_to=assigned_to,
            url=data.get("url", "") or "",
        )

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
        repro_html_parts: list[str] = []
        if preconditions and preconditions.strip():
            repro_html_parts.append(
                f"<div><b>Preconditions:</b> {html_lib.escape(preconditions)}</div>"
            )
        repro_html_parts.append("<div><b>Steps to reproduce:</b></div><ol>")
        for s in repro_steps:
            repro_html_parts.append(f"<li>{html_lib.escape(s)}</li>")
        repro_html_parts.append("</ol>")
        repro_html = "".join(repro_html_parts)

        sys_info_parts = [
            f"<div><b>Expected:</b> {html_lib.escape(expected)}</div>",
            f"<div><b>Actual:</b> {html_lib.escape(actual)}</div>",
        ]
        if suggested_fix and suggested_fix.strip():
            sys_info_parts.append(
                f"<div><b>Suggested fix:</b> {html_lib.escape(suggested_fix)}</div>"
            )
        sys_info = "".join(sys_info_parts)

        sev_value = {
            "Critical": "1 - Critical",
            "High":     "2 - High",
            "Medium":   "3 - Medium",
            "Low":      "4 - Low",
        }.get(severity, "3 - Medium")

        body = [
            _patch_op("add", "/fields/System.Title", title),
            _patch_op("add", "/fields/Microsoft.VSTS.TCM.ReproSteps", repro_html),
            _patch_op("add", "/fields/Microsoft.VSTS.TCM.SystemInfo", sys_info),
            _patch_op("add", "/fields/Microsoft.VSTS.Common.Severity", sev_value),
        ]

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

    # System.ClosedDate isn't present in every process template (TF51535 on Ideal Agile).
    # Skip it — HTML builder's ticket sort handles closed_at=None just fine.
    REPORT_FIELDS = [
        "System.Id", "System.Title", "System.WorkItemType", "System.AssignedTo",
        "System.State", "System.Description", "System.History",
        "Microsoft.VSTS.TCM.ReproSteps", "Microsoft.VSTS.Common.ResolvedReason",
    ]

    async def get_tickets_in_range(
        self, from_utc, to_utc, boards: list[str],
    ) -> list[ReportTicket]:
        # [System.ChangedDate] is date-precision in WIQL — supplying a time triggers
        # "You cannot supply a time with the date when running a query using date precision."
        from_s = from_utc.strftime("%Y-%m-%d")
        to_s = to_utc.strftime("%Y-%m-%d")

        area_clause = ""
        if boards:
            esc = lambda b: b.replace("'", "''")  # noqa: E731
            parts = [f"[System.AreaPath] UNDER '{esc(b)}'" for b in boards]
            area_clause = " AND (" + " OR ".join(parts) + ")"

        wiql_body = {
            "query": (
                f"SELECT [System.Id] FROM WorkItems "
                f"WHERE [System.TeamProject] = '{settings.ado_project}' "
                f"AND [System.ChangedDate] >= '{from_s}' "
                f"AND [System.ChangedDate] <= '{to_s}' "
                f"AND [System.WorkItemType] IN ('User Story','Bug','Task','Issue','Feature') "
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
                ))
        return results

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
