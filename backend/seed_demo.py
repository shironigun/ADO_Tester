"""Seed DEMO data for the Author Tests + Find Bugs modules.

Inserts mock sessions, drafts and Epic/Feature batches that exercise EVERY UI feature/state
for a walkthrough — without running any live agent (all rows are terminal, pure-read).

Idempotent: every demo row uses a work-item id in the reserved DEMO band [188000, 188999]
(batch parents in [188900, 188999]); re-running purges that band first, so it NEVER touches
real sessions (which live in the ~160000–171000 range).

Run from the backend/ dir:   .venv\\Scripts\\python.exe seed_demo.py
"""
from __future__ import annotations

import asyncio
import json
from datetime import datetime, timedelta

from sqlalchemy import select

from app.db import session_scope
from app.models import Batch, BugDraft, BugSession, TestCaseDraft, TestSession

# ── Reserved demo id bands (idempotency keys) ────────────────────────────────
DEMO_LO, DEMO_HI = 188000, 188999          # session work_item_id band
PARENT_LO, PARENT_HI = 188900, 188999      # batch parent_work_item_id band

PROJECT_ID = 1                              # frontend hardcodes PROJECT_ID = 1
BASE = datetime(2026, 6, 16, 9, 0, 0)       # deterministic timestamps
EPOCH = 1781000000.0                        # ~2026-06-16, for event-log ts (epoch seconds)


def at(mins: int) -> datetime:
    return BASE + timedelta(minutes=mins)


def J(obj) -> str:
    return json.dumps(obj, ensure_ascii=False)


# ── analysis_json builders (camelCase keys; trailing tags drive evidence chips) ──
AJ_QUOTE_BUILDER = {
    "module": "Deals", "feature": "Quote Builder — SAVE QUOTE action",
    "summary": "This story adds a SAVE QUOTE action to the Deals Quote Builder so a sales rep can "
               "persist an in-progress quote against an OPEN deal and resume it later. Verified on QA "
               "against Dealer 999888444 on the ASPEN back-end. The quote total must recalculate from "
               "its line items before it can be saved.",
    "happyPath": "From an OPEN deal, the rep opens the Quote Builder, adds vehicle + F&I line items, "
                 "clicks Recalculate, then clicks SAVE QUOTE; the quote is persisted, a success "
                 "snackbar appears, and the saved quote is listed under the deal's Quotes tab.",
    "acceptanceCriteria": [
        "The SAVE QUOTE button is disabled until at least one line item exists and the total has been recalculated. [ticket]",
        "A saved quote appears in the deal's Quotes tab with its total, created date, and author. [ticket]",
        "Saving a quote does not change the deal's STATE (it remains OPEN). [src: deals.md]",
    ],
    "businessRules": [
        "A quote can only be saved against an OPEN deal; CLOSED deals show the SAVE QUOTE button disabled. [src: deals.md]",
        "The quote total must equal the sum of its line items at save time; a stale total blocks the save. [ticket]",
        "Only one in-progress (unsaved) quote may exist per deal at a time. [ASSUMED]",
    ],
    "edgeCases": [
        "Deal with zero line items — SAVE QUOTE stays disabled. [ticket]",
        "Quote total of 0.00 after all line items are removed. [ASSUMED]",
        "Deal that was CLOSED in ASPEN mid-session while the Quote Builder was open. [UNCLEAR]",
    ],
    "negativeCases": [
        "Attempt to save after editing a line item without clicking Recalculate — the save is blocked and a warning appears. [ticket]",
        "Attempt to open the Quote Builder on a CLOSED deal. [src: deals.md]",
    ],
    "permissions": [
        "A Sales Rep role can create and save quotes. [src: roles.md]",
        "A Read-Only role sees the Quote Builder but the SAVE QUOTE button is hidden. [ASSUMED]",
    ],
    "crossFlow": [
        "Reports — the Saved Quotes report must include quotes created here. [ASSUMED]",
        "ASPEN sync — a saved quote must not push to ASPEN until the deal is CLOSED. [src: integrations.md]",
    ],
    "openQuestions": [
        "Should saving a quote lock the underlying line items from further edits, or are they still editable until the deal is CLOSED?",
    ],
    "questionAnswers": [
        "Line items remain editable until the deal is CLOSED; saving only snapshots the total.",
    ],
}

AJ_FI_RECALC = {
    "module": "Deals", "feature": "F&I Quote — Cash Down vs. Amount Financed recalculation",
    "summary": "This story builds the recalculation logic on the DEALS quote screen so that when an "
               "F&I manager edits the Cash Down field, the Amount Financed, monthly payment, and tax "
               "lines recompute live against the IDEAL DMS deal record. Verified on STAGING against "
               "dealer 4815.",
    "happyPath": "From a draft deal on STAGING (dealer 4815), the F&I manager opens the F&I Quote tab, "
                 "enters a vehicle price and a Cash Down amount, and the Amount Financed, monthly "
                 "PAYMENT, and tax line recalculate instantly; clicking SAVE persists the quote and "
                 "pushes the updated figures to the IDEAL deal record.",
    "acceptanceCriteria": [
        "Editing the Cash Down field recalculates Amount Financed = Selling Price + Taxes + Fees - Cash Down - Trade Allowance, live, without a page reload. [ticket]",
        "Amount Financed can never be negative; if Cash Down exceeds the financeable total the field clamps to 0.00 and a 'Cash down exceeds amount financed' inline warning shows. [ticket]",
        "The recalculated monthly PAYMENT respects the deal's APR and term from the IDEAL deal record. [src: deals.md]",
    ],
    "businessRules": [
        "Cash Down must be a non-negative currency value with at most 2 decimal places; commas and currency symbols are stripped on blur. [src: deals.md]",
        "A deal in CLOSED state is read-only — recalculation fields are disabled until the deal is reopened. [src: deals.md]",
        "Tax is recomputed on the post-Cash-Down taxable amount per the dealer's tax jurisdiction setting. [src: fi-tax-rules.md]",
    ],
    "edgeCases": [
        "Cash Down entered as exactly the full Selling Price + Taxes + Fees -> Amount Financed = 0.00 and PAYMENT = 0.00. [ASSUMED]",
        "Customer record imported from ASPEN with a NULL APR -> PAYMENT shows '-' and SAVE is blocked with a 'Missing APR' notice. [src: deals.md]",
        "Trade Allowance greater than Selling Price (negative equity) -> Amount Financed increases accordingly and no clamp is applied. [ticket]",
    ],
    "negativeCases": [
        "Entering letters or a negative sign in Cash Down -> input rejected, prior valid value restored on blur. [ticket]",
        "Submitting with Selling Price present but Cash Down empty -> SAVE stays disabled. [ticket]",
    ],
    "permissions": [
        "F&I Manager and Sales Manager roles can edit Cash Down and SAVE the quote. [src: permissions.md]",
        "Salesperson role sees the F&I Quote tab read-only — Cash Down is disabled and SAVE is hidden. [src: permissions.md]",
    ],
    "crossFlow": [
        "Customer Import (ASPEN/EVEREST) — an imported customer with missing finance fields must not break recalculation. [src: customer-import.md]",
        "Reports — the F&I production report reads the saved Amount Financed; verify the pushed figure matches. [src: reports.md]",
    ],
    "openQuestions": [
        "When a deal is reopened from CLOSED, should the quote reload the latest IDEAL figures or keep the F&I manager's in-progress edits? [UNCLEAR]",
        "Is the 'Cash down exceeds amount financed' state a hard block on SAVE or only a non-blocking warning?",
    ],
    "questionAnswers": [
        "Reload the latest IDEAL figures on reopen and discard unsaved edits, with a confirm dialog. Confirmed with PO on the 2026-06-12 grooming call.",
        "",
    ],
}

AJ_CUSTOMER_IMPORT = {
    "module": "Customers", "feature": "Customer Import via CSV / Excel with Guided Mapping",
    "summary": "This story lets a BDC user bulk-import customers from a CSV/Excel file into YourProduct "
               "with a guided column-mapping step and row-level validation before commit. Verified on "
               "QA against dealer 4471.",
    "happyPath": "The user uploads a .xlsx, maps each spreadsheet column to a CRM field, reviews the "
                 "validation summary, and clicks Import; valid rows are created as CUSTOMER records and "
                 "a results report lists created/skipped counts.",
    "acceptanceCriteria": [
        "The mapping step pre-suggests CRM fields from the header row and requires First Name, Last Name, and Phone to be mapped. [ticket]",
        "Rows that fail validation are listed with a reason and excluded from the import, not silently dropped. [ticket]",
        "A duplicate customer (matching phone + last name) is flagged and skipped unless 'merge duplicates' is enabled. [src: customers.md]",
    ],
    "businessRules": [
        "Phone numbers are normalized to 10 digits before dedupe; non-numeric characters are stripped. [src: customers.md]",
        "An import batch over 5,000 rows is processed asynchronously with a progress indicator. [ASSUMED]",
    ],
    "edgeCases": [
        "A file with a missing header row. [ticket]",
        "A column mapped to two CRM fields. [UNCLEAR]",
    ],
    "negativeCases": [
        "Uploading a non-spreadsheet file type is rejected with a clear message. [ticket]",
        "Committing with required fields unmapped keeps the Import button disabled. [ticket]",
    ],
    "permissions": [
        "BDC Manager and Admin roles can run an import; a BDC Agent can only preview. [src: roles.md]",
    ],
    "crossFlow": [
        "Deals — an imported customer must be selectable when building a new quote. [src: deals.md]",
    ],
    "openQuestions": [
        "Should a partially-failed import be rolled back entirely, or commit the valid rows?",
    ],
    "questionAnswers": [
        "Commit the valid rows and surface a downloadable error report for the failed ones.",
    ],
}


# ── event_log_json builders (camelCase ConsoleEvent array; ts = epoch seconds) ──
def at_events(wid: int, title: str, drafted_n: int, pushed: bool = False) -> str:
    files = ["deals.md", "roles.md", "integrations.md"]
    seq = 1
    t = EPOCH
    out: list[dict] = []

    def add(kind, phase, level, text, data=None):
        nonlocal seq, t
        out.append({"seq": seq, "ts": float(t), "sessionKey": f"session:{wid}",
                    "kind": kind, "phase": phase, "level": level, "text": text, "data": data})
        seq += 1
        t += 7

    add("system", None, "info", "Pipeline started · provider anthropic")
    add("phase", "fetch", "success", f"Fetched work item #{wid} — {title}", {"workItemId": wid, "type": "User Story"})
    add("result", "retrieve", "success", f"Retrieved {len(files)} knowledgebase chunk(s)", {"chunks": len(files), "files": files})
    add("phase", "analyst", "info", "Analyst started")
    add("result", "analyst", "success", "Analyst produced: 3 ACs · 3 edge · 2 negative", {"acceptanceCriteria": 3, "edgeCases": 3})
    add("phase", "tester", "info", "Tester started — generating test cases in QA style")
    add("system", "tester", "success", f"Drafted {drafted_n} test case(s) — awaiting review", {"terminal": True, "count": drafted_n})
    if pushed:
        add("phase", "push", "info", "Push to ADO started")
        add("system", "push", "success", f"Pushed {drafted_n} test case(s) to ADO", {"terminal": True, "count": drafted_n})
    return J(out)


def fb_events(wid: int, title: str, drafted_n: int) -> str:
    files = ["deals.md", "roles.md", "inventory.md"]
    seq = 1
    t = EPOCH
    out: list[dict] = []

    def add(kind, phase, level, text, data=None):
        nonlocal seq, t
        out.append({"seq": seq, "ts": float(t), "sessionKey": f"bug:{wid}",
                    "kind": kind, "phase": phase, "level": level, "text": text, "data": data})
        seq += 1
        t += 7

    add("system", None, "info", "Pipeline started · provider anthropic")
    add("phase", "fetch", "success", f"Fetched ticket #{wid} — {title}", {"workItemId": wid, "type": "User Story"})
    add("result", "retrieve", "success", f"Retrieved {len(files)} knowledgebase chunk(s)", {"chunks": len(files), "files": files})
    add("phase", "analyst", "info", "Analyst started")
    add("result", "analyst", "success", "Analyst produced: 3 ACs · 2 business rules", None)
    add("phase", "discover", "info", "Anchoring on the change — linked PRs/commits, then only relevant code")
    add("result", "discover", "success", "Anchored on 1 changed file(s); expanded to 3 total · 8,210 chars",
        {"files": ["client/src/deals/QuoteBuilder.tsx"]})
    add("phase", "code-verify", "info", "Verifying 3 test case(s) (1 from Test Plan, 2 drafts) against the change")
    add("result", "code-verify", "error", "FAIL: SAVE is disabled until a Customer and a line item exist — no disabled guard", None)
    add("phase", "bugwriter", "info", "Bug Writer started — comparing expected vs actual")
    add("system", "bugwriter", "success", f"Drafted {drafted_n} bug(s) — awaiting review", {"terminal": True, "count": drafted_n})
    return J(out)


# ── code_verification_json (camelCase verdict array; matches the QB case titles below) ──
QB_CASES = [
    "Deals - Quote Builder - Verify SAVE is disabled until a Customer and a line item exist.",
    "Deals - Quote Builder - Verify the quote total recalculates when a line item price is edited.",
    "Deals - Quote Builder - Verify a saved quote syncs to TARGET within 30 seconds for ASPEN dealers.",
]

CODE_VERIFICATION = J([
    {
        "testCase": QB_CASES[0], "status": "fail", "source": "draft", "failedStepIndex": 3,
        "evidence": "client/src/deals/QuoteBuilder.tsx: the SAVE button has no disabled guard; onClick fires saveQuote() unconditionally.",
        "reasoning": "The Save button binds onClick directly to saveQuote without checking customerId or lineItems.length, so it stays enabled on an empty quote.",
        "steps": [
            {"action": "Login to YourProduct on the ASPEN QA environment.", "expected": "The dashboard loads.", "status": "pass", "actual": ""},
            {"action": "Open Deals > Quote Builder.", "expected": "The Quote Builder screen opens.", "status": "pass", "actual": ""},
            {"action": "Leave the Customer field empty and add no line items.", "expected": "The SAVE button is disabled.", "status": "pass", "actual": ""},
            {"action": "Click the SAVE button.", "expected": "The quote is not saved and a validation message appears.",
             "status": "fail", "actual": "QuoteBuilder.tsx: SAVE remains enabled and saveQuote() persists an empty quote — no validation guard."},
        ],
    },
    {
        "testCase": QB_CASES[1], "status": "pass", "source": "draft", "failedStepIndex": None,
        "evidence": "client/src/deals/QuoteBuilder.tsx: a useEffect recomputes the total whenever lineItems change.",
        "reasoning": "A useEffect dependency on lineItems recomputes the total whenever a price changes.",
        "steps": [
            {"action": "Open a quote with one line item on QA.", "expected": "The quote total is shown.", "status": "pass", "actual": ""},
            {"action": "Edit the line item price to 500.", "expected": "The quote total updates to reflect 500.", "status": "pass", "actual": ""},
        ],
    },
    {
        "testCase": QB_CASES[2], "status": "inconclusive", "source": "plan", "adoTestCaseId": 160902, "failedStepIndex": None,
        "evidence": "No ASPEN->TARGET sync worker code was retrieved in the anchored slice.",
        "reasoning": "The supplied code does not include the sync job, so the 30s timing cannot be judged.",
        "steps": [
            {"action": "Save a valid quote on the ASPEN QA environment.", "expected": "The quote is persisted.", "status": "pass", "actual": ""},
            {"action": "Wait 30 seconds and check TARGET.", "expected": "The quote appears in TARGET.",
             "status": "inconclusive", "actual": "Sync job code not present in the retrieved excerpt."},
        ],
    },
    {
        "testCase": "Manual: empty-name quote still saves on STAGING dealer 4471", "status": "fail",
        "evidence": "Matches the QuoteBuilder.tsx missing-guard finding.",
        "reasoning": "The actual behavior pasted by QA aligns with the missing SAVE guard.",
    },
])


# ── draft step builders ──────────────────────────────────────────────────────
def tc_steps(*pairs) -> str:
    return J([{"action": a, "expected": e} for a, e in pairs])


QB_SAVE_STEPS = tc_steps(
    ("Login to YourProduct.", "User is logged in to Dealer 999888444 on the ASPEN QA environment and the dashboard loads."),
    ("Navigate to the Deals module.", "The Deals module opens and the deals pipeline is displayed."),
    ("Locate an OPEN deal for the customer.", "The OPEN deal is displayed with its STATE shown as OPEN."),
    ("Click the Quote Builder button.", "The Quote Builder panel opens with an empty line-item list."),
    ("Click the Add Line Item button.", "A new line-item row is added to the Quote Builder."),
    ("Click the Recalculate button.", "The quote total updates to the sum of the line items."),
    ("Verify that the rep is able to save the quote against the OPEN deal.",
     "The SAVE QUOTE button is enabled, the quote is persisted, a success snackbar appears, and the saved quote is listed under the deal's Quotes tab."),
)

FI_RECALC_STEPS = tc_steps(
    ("Open a draft deal on STAGING (dealer 4815) and select the F&I Quote tab.",
     "The F&I Quote tab opens with editable Selling Price and Cash Down fields."),
    ("Enter Selling Price 32,000 then type 5,000 in Cash Down.",
     "Amount Financed, monthly PAYMENT, and tax line recalculate live without a page reload."),
    ("Click SAVE.", "The quote persists and the updated Amount Financed is pushed to the IDEAL deal record."),
)


def bug_repro(*steps) -> str:
    return J(list(steps))


# ── insert helpers ───────────────────────────────────────────────────────────
def new_ts(*, wid, title, status, created_min, analysis=None, events=None,
           current=None, error=None, batch_id=None):
    return TestSession(
        project_id=PROJECT_ID, work_item_id=wid, work_item_title=title, status=status,
        current_activity=current, analysis_json=(J(analysis) if analysis else None),
        error_message=error, event_log_json=events, batch_id=batch_id,
        created_at=at(created_min), updated_at=at(created_min + 5),
    )


def new_tc(session, *, title, steps, status="Draft", ado_id=None, order=0):
    return TestCaseDraft(session=session, title=title, preconditions="", steps_json=steps,
                         status=status, ado_test_case_id=ado_id, sort_order=order)


def new_bs(*, wid, title, status, created_min, analysis=None, events=None, verification=None,
           current=None, error=None, actual=None, batch_id=None):
    return BugSession(
        project_id=PROJECT_ID, work_item_id=wid, work_item_title=title, status=status,
        current_activity=current, analysis_json=(J(analysis) if analysis else None),
        actual_behavior=actual, code_context=None, error_message=error,
        event_log_json=events, code_verification_json=verification, batch_id=batch_id,
        created_at=at(created_min), updated_at=at(created_min + 5),
    )


def new_bug(session, *, title, severity, repro, expected, actual, fix="", status="Draft", ado_id=None, order=0):
    return BugDraft(session=session, title=title, severity=severity, preconditions="",
                    repro_steps_json=repro, expected_behavior=expected, actual_behavior=actual,
                    suggested_fix=fix, status=status, ado_bug_id=ado_id, sort_order=order)


async def purge(s) -> None:
    removed = 0
    for model in (TestSession, BugSession):
        rows = (await s.execute(select(model).where(model.work_item_id.between(DEMO_LO, DEMO_HI)))).scalars().all()
        for r in rows:
            await s.delete(r)
            removed += 1
    batches = (await s.execute(select(Batch).where(Batch.parent_work_item_id.between(PARENT_LO, PARENT_HI)))).scalars().all()
    for b in batches:
        await s.delete(b)
    await s.flush()
    print(f"purged {removed} demo session(s) + {len(batches)} demo batch(es)")


async def main() -> None:
    async with session_scope() as s:
        await purge(s)

        # ════════════════════════ AUTHOR TESTS — standalone ════════════════════════
        # 1) Done + pushed (full happy path: analysis, events, pushed drafts, push-results card)
        ts_done = new_ts(wid=188010, title="Deals - Quote Builder - Add SAVE QUOTE action", status="Done",
                         created_min=300, analysis=AJ_QUOTE_BUILDER,
                         events=at_events(188010, "Add SAVE QUOTE action", 6, pushed=True),
                         current="Pushed 6 test case(s) to ADO")
        s.add(ts_done)
        for i, title in enumerate([
            "Deals - Quote Builder - Verify a rep can save a quote against an OPEN deal.",
            "Deals - Quote Builder - Verify SAVE QUOTE is disabled with zero line items.",
            "Deals - Quote Builder - Verify SAVE is blocked after editing a line item without Recalculate.",
            "Deals - Quote Builder - Verify the Quote Builder cannot be opened on a CLOSED deal.",
            "Deals - Quote Builder - Verify a saved quote appears under the deal's Quotes tab.",
            "Deals - Quote Builder - Verify saving a quote does not change the deal STATE.",
        ]):
            s.add(new_tc(ts_done, title=title, steps=QB_SAVE_STEPS, status="Pushed",
                         ado_id=161002 + i, order=i))

        # 2) Drafted — drafts awaiting review (Draft/Approved/Rejected mix, push controls live)
        ts_draft = new_ts(wid=188011, title="Deals - F&I Quote - Cash Down recalculation", status="Drafted",
                          created_min=260, analysis=AJ_FI_RECALC,
                          events=at_events(188011, "Cash Down recalculation", 5),
                          current="Drafted 5 test case(s) — awaiting review")
        s.add(ts_draft)
        draft_mix = [
            ("Deals - F&I Quote - Verify Cash Down edit recalculates Amount Financed live.", "Approved"),
            ("Deals - F&I Quote - Verify Amount Financed clamps to 0.00 when Cash Down exceeds total.", "Approved"),
            ("Deals - F&I Quote - Verify SAVE stays disabled until Selling Price and Cash Down are present.", "Draft"),
            ("Deals - F&I Quote - Verify non-numeric Cash Down input is rejected on blur.", "Draft"),
            ("Deals - F&I Quote - Verify Cash Down field is disabled on a CLOSED deal.", "Rejected"),
        ]
        for i, (title, st) in enumerate(draft_mix):
            s.add(new_tc(ts_draft, title=title, steps=FI_RECALC_STEPS, status=st, order=i))

        # 3) AwaitingTester — Analyst done, parked for manual Tester run (lands on Analyst findings)
        s.add(new_ts(wid=188012, title="Customers - Customer Import - Guided column mapping", status="AwaitingTester",
                     created_min=220, analysis=AJ_CUSTOMER_IMPORT,
                     events=at_events(188012, "Guided column mapping", 0),
                     current="Analyst done — review the findings, then run the Tester"))

        # 4) Failed — analyst reached, then errored
        s.add(new_ts(wid=188013, title="Deals - Quote Builder - SAVE QUOTE action (retry)", status="Failed",
                     created_min=180, analysis=AJ_QUOTE_BUILDER,
                     events=at_events(188013, "SAVE QUOTE action (retry)", 0),
                     current="Tester failed", error="Tester returned invalid JSON after 2 retries (token limit)."))

        # 5) Cancelled — cancelled mid-run, with a couple of drafts already made
        ts_canc = new_ts(wid=188014, title="Inventory - Unit Detail - VIN decode banner", status="Cancelled",
                         created_min=140, analysis=AJ_QUOTE_BUILDER, current="Cancelled by user")
        s.add(ts_canc)
        s.add(new_tc(ts_canc, title="Inventory - Unit Detail - Verify VIN decode populates make/model.",
                     steps=QB_SAVE_STEPS, status="Draft", order=0))

        # ════════════════════════ FIND BUGS — standalone ════════════════════════
        # Paired Author-Tests session sharing work_item_id 188020 so the Find Bugs report's
        # "Drafts (Author Tests)" group + per-case checkboxes populate (matched by case title).
        ts_paired = new_ts(wid=188020, title="Deals - Quote Builder: block SAVE on empty quote", status="Drafted",
                           created_min=120, analysis=AJ_QUOTE_BUILDER,
                           events=at_events(188020, "block SAVE on empty quote", 3),
                           current="Drafted 3 test case(s) — awaiting review")
        s.add(ts_paired)
        for i, title in enumerate(QB_CASES):
            s.add(new_tc(ts_paired, title=title, steps=QB_SAVE_STEPS, status="Approved", order=i))

        # 1) Done + pushed bug (analysis, code-verification report, bug drafts incl. a Pushed one)
        bs_done = new_bs(wid=188020, title="Deals - Quote Builder: block SAVE on empty quote", status="Done",
                         created_min=305, analysis=AJ_QUOTE_BUILDER,
                         events=fb_events(188020, "block SAVE on empty quote", 2),
                         verification=CODE_VERIFICATION,
                         actual="On QA (dealer 999888444) the SAVE button stays enabled with an empty Customer and no line items.",
                         current="Pushed 1 bug(s) to ADO")
        s.add(bs_done)
        s.add(new_bug(bs_done, severity="High", status="Pushed", ado_id=161020, order=0,
                      title="SAVE button is not disabled for an empty quote in Deals > Quote Builder on ASPEN QA",
                      repro=bug_repro(
                          "Login to YourProduct as a Sales user on the ASPEN QA environment.",
                          "Open the Deals module.",
                          "Click the Quote Builder tab.",
                          "Leave the Customer field empty and do not add any line items.",
                          "Click the SAVE button."),
                      expected="The SAVE button is disabled until a CUSTOMER is selected and at least one line item is added.",
                      actual="The SAVE button stays enabled with an empty CUSTOMER and no line items, and clicking it persists an empty quote.",
                      fix="Gate the SAVE button's disabled prop on customerId && lineItems.length > 0 in client/src/deals/QuoteBuilder.tsx."))
        s.add(new_bug(bs_done, severity="Medium", status="Pushed", ado_id=161021, order=1,
                      title="Quote total does not refresh in the F&I menu after a line-item price edit",
                      repro=bug_repro("Open an OPEN deal with a saved quote.", "Edit a line-item price and Recalculate.",
                                      "Open the F&I menu for the same deal.", "Compare the grand total."),
                      expected="The F&I menu grand total matches the recalculated quote total.",
                      actual="The F&I menu shows the stale cached total until the page is reloaded.",
                      fix="Read the live quote total prop in client/src/fi/MenuPanel.tsx instead of the cached value."))

        # 2) Drafted — bugs awaiting review (Draft/Approved/Rejected), report + actual-behavior input
        bs_draft = new_bs(wid=188021, title="Deals - F&I Quote - Cash Down clamp + warning", status="Drafted",
                          created_min=250, analysis=AJ_FI_RECALC,
                          events=fb_events(188021, "Cash Down clamp + warning", 3),
                          verification=CODE_VERIFICATION,
                          actual="On STAGING dealer 4815 the Amount Financed shows -1,250.00 with no warning when Cash Down exceeds the total.",
                          current="Drafted 3 bug(s) — awaiting review")
        s.add(bs_draft)
        bug_mix = [
            ("Amount Financed is not clamped to 0.00 when Cash Down exceeds the financeable total", "Critical", "Approved"),
            ("No 'Cash down exceeds amount financed' warning is shown on negative Amount Financed", "High", "Approved"),
            ("Monthly PAYMENT shows a stale value after editing Cash Down on a reopened CLOSED deal", "Medium", "Draft"),
        ]
        for i, (title, sev, st) in enumerate(bug_mix):
            s.add(new_bug(bs_draft, severity=sev, status=st, order=i, title=title,
                          repro=bug_repro("Open a draft deal on STAGING dealer 4815 and go to the F&I Quote tab.",
                                          "Enter a Selling Price of 28,000.",
                                          "Enter a Cash Down of 40,000 (greater than the financeable total).",
                                          "Observe the Amount Financed field and the inline validation area."),
                          expected="Amount Financed clamps to 0.00 and a 'Cash down exceeds amount financed' warning is shown.",
                          actual="Amount Financed renders -1,250.00 and no warning is displayed.",
                          fix="Wrap the amountFinanced computation in Math.max(0, ...) and render the inline warning in FiQuotePanel.tsx."))

        # 3) AwaitingActual — Analyst done, parked for manual verification (Code Verify input shown)
        s.add(new_bs(wid=188022, title="Customers - Customer Import - Duplicate dedupe on phone", status="AwaitingActual",
                     created_min=210, analysis=AJ_CUSTOMER_IMPORT,
                     events=fb_events(188022, "Duplicate dedupe on phone", 0),
                     current="Analyst done — paste observed actual behavior to continue"))

        # 4) Failed
        s.add(new_bs(wid=188023, title="Deals - Quote Builder: block SAVE (retry)", status="Failed",
                     created_min=170, analysis=AJ_QUOTE_BUILDER,
                     events=fb_events(188023, "block SAVE (retry)", 0),
                     current="Code verification failed",
                     error="Code Verifier could not anchor on the linked change (no PR/commit found on the story)."))

        # 5) Cancelled — with a code-verification report already produced
        s.add(new_bs(wid=188024, title="Inventory - Pricing - Bulk price update guard", status="Cancelled",
                     created_min=130, analysis=AJ_QUOTE_BUILDER, verification=CODE_VERIFICATION,
                     current="Cancelled by user"))

        # ════════════════════════ EPIC / FEATURE BATCHES ════════════════════════
        # Batch A — Author Tests, derived "Completed" (all children Done/Drafted, no failures)
        batch_a = Batch(project_id=PROJECT_ID, module="author-tests", parent_work_item_id=188900,
                        title="Epic 188900 — Deals: Quote & F&I rework", mode=None,
                        created_at=at(400), updated_at=at(420))
        s.add(batch_a)
        await s.flush()
        a1 = new_ts(wid=188030, title="Deals - Quote Builder - SAVE QUOTE action", status="Done",
                    created_min=401, analysis=AJ_QUOTE_BUILDER, batch_id=batch_a.id, current="Pushed 4 test case(s) to ADO")
        a2 = new_ts(wid=188031, title="Deals - F&I Quote - Cash Down recalculation", status="Drafted",
                    created_min=402, analysis=AJ_FI_RECALC, batch_id=batch_a.id, current="Drafted 5 test case(s) — awaiting review")
        s.add_all([a1, a2])
        for i in range(4):
            s.add(new_tc(a1, title=f"Deals - Quote Builder - Verify behavior {i + 1}.", steps=QB_SAVE_STEPS, status="Pushed", ado_id=162000 + i, order=i))
        for i in range(5):
            s.add(new_tc(a2, title=f"Deals - F&I Quote - Verify recalculation rule {i + 1}.", steps=FI_RECALC_STEPS, status="Approved", order=i))

        # Batch B — Find Bugs (auto), derived "CompletedWithFailures" (one child Failed)
        batch_b = Batch(project_id=PROJECT_ID, module="find-bugs", parent_work_item_id=188901,
                        title="Epic 188901 — F&I menu & deal quote bug sweep", mode="auto",
                        created_at=at(380), updated_at=at(395))
        s.add(batch_b)
        await s.flush()
        b1 = new_bs(wid=188040, title="Deals - Quote total trade-in recalculation", status="Done",
                    created_min=381, analysis=AJ_QUOTE_BUILDER, verification=CODE_VERIFICATION, batch_id=batch_b.id,
                    current="Drafted 2 bug(s) — awaiting review")
        b2 = new_bs(wid=188041, title="F&I - Menu total mismatch", status="Failed", created_min=382,
                    analysis=AJ_FI_RECALC, batch_id=batch_b.id, current="Code verification failed",
                    error="Code Verifier returned invalid JSON.")
        b3 = new_bs(wid=188042, title="Customers - Bulk import dedupe", status="Drafted", created_min=383,
                    analysis=AJ_CUSTOMER_IMPORT, verification=CODE_VERIFICATION, batch_id=batch_b.id,
                    current="Drafted 1 bug(s) — awaiting review")
        s.add_all([b1, b2, b3])
        s.add(new_bug(b1, severity="High", status="Draft", order=0, title="Trade-in change does not recalculate quote tax on the OPEN quote",
                      repro=bug_repro("Open an OPEN deal quote.", "Change the trade-in allowance and SAVE.", "Observe the tax line and grand total."),
                      expected="The tax line and grand total recalculate within the same SAVE.",
                      actual="The total stays stale until a full page reload.", fix="Call recalcTax() from onTradeInChange in QuoteEditor.tsx."))
        s.add(new_bug(b1, severity="Medium", status="Draft", order=1, title="F&I menu total diverges from the recalculated quote total",
                      repro=bug_repro("Recalculate a quote total.", "Open the F&I menu.", "Compare totals."),
                      expected="The F&I menu reflects the recalculated total.", actual="The menu reads a cached value."))
        s.add(new_bug(b3, severity="Low", status="Draft", order=0, title="Duplicate customer not skipped when phone has formatting characters",
                      repro=bug_repro("Import a CSV with a duplicate phone formatted as (514) 555-0123.", "Run the import."),
                      expected="The duplicate is detected and skipped.", actual="A second CUSTOMER record is created."))

        # Batch C — Find Bugs (manual), derived "AwaitingInput" (children parked at AwaitingActual)
        batch_c = Batch(project_id=PROJECT_ID, module="find-bugs", parent_work_item_id=188902,
                        title="Feature 188902 — Customer Import hardening (manual)", mode="manual",
                        created_at=at(360), updated_at=at(370))
        s.add(batch_c)
        await s.flush()
        s.add(new_bs(wid=188043, title="Customers - Import - Required field mapping", status="AwaitingActual",
                     created_min=361, analysis=AJ_CUSTOMER_IMPORT, batch_id=batch_c.id,
                     current="Analyst done — paste observed actual behavior to continue"))
        s.add(new_bs(wid=188044, title="Customers - Import - Async batch over 5,000 rows", status="AwaitingActual",
                     created_min=362, analysis=AJ_CUSTOMER_IMPORT, batch_id=batch_c.id,
                     current="Analyst done — paste observed actual behavior to continue"))

        # Batch D — Author Tests, derived "Cancelled" (every child Cancelled, none reached Done)
        batch_d = Batch(project_id=PROJECT_ID, module="author-tests", parent_work_item_id=188903,
                        title="Epic 188903 — Reporting refresh (cancelled run)", mode=None,
                        created_at=at(340), updated_at=at(345))
        s.add(batch_d)
        await s.flush()
        s.add(new_ts(wid=188050, title="Reports - F&I Production - Date range filter", status="Cancelled",
                     created_min=341, analysis=AJ_QUOTE_BUILDER, batch_id=batch_d.id, current="Cancelled (epic run)"))
        s.add(new_ts(wid=188051, title="Reports - Saved Quotes - Author column", status="Cancelled",
                     created_min=342, batch_id=batch_d.id, current="Cancelled (epic run)"))

        await s.commit()
        print("demo seed complete")


if __name__ == "__main__":
    asyncio.run(main())
