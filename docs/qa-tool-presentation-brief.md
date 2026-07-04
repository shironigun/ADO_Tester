# QA Automation Platform — Presentation Brief

> **For:** a presentation-generation AI.
> **Audience:** QA engineers, Business Analysts, QA leads, and engineering managers.
> **Core message:** This tool compresses the **entire QA testing lifecycle** — from reading a
> requirement to filing a defect — into an AI‑assisted, Azure DevOps–native loop, so a QA/BA spends
> time on *judgement* instead of *typing*.
>
> Use this brief to build a slide deck. Each `## Slide` block below is one suggested slide with a
> title, the key points to show, and speaker notes. Keep the tone confident, concrete, and
> QA‑centric (not developer‑centric). Prefer diagrams and before/after comparisons over dense text.

---

## Slide 1 — Title

**Title:** From Story to Test to Bug — an AI copilot for the QA lifecycle
**Subtitle:** Turn Azure DevOps stories into test cases, executable tests, real runs, and house‑style bug reports — automatically.

**Speaker notes:** Open with the pain: QA is stuck in a manual‑first loop — manual execution eats
all the time, so nothing gets automated, so the next regression is manual again. This tool breaks
that loop.

---

## Slide 2 — The problem (why QA teams need this)

**Show as a "day in the life" list of pain points:**
- Reading a story and hand‑writing 15–30 test cases in the ADO house style — hours per story.
- Re‑typing the same boilerplate: preconditions, steps, expected results, priorities.
- Writing automation separately (if ever), so tests drift from the cases.
- Running tests manually against QA, eyeballing responses, updating the test run by hand.
- Filing bugs by hand — copy/paste repro steps, screenshots, expected vs actual, severity.
- Regression = re‑doing all of the above under time pressure.

**Speaker notes:** Every one of these is repetitive, high‑volume, low‑judgement work. It's exactly
what AI + automation should absorb, leaving the human to decide *what* matters.

---

## Slide 3 — What this tool is (one sentence)

**Big statement:** A locally‑hosted QA copilot that plugs into **Azure DevOps** and drives the full
QA lifecycle — requirement analysis → test design → automation → execution → defect reporting →
reporting — using AI agents in a specific QA author's house style.

**Show a simple badge row:**
- Works *inside* your ADO org, project, boards, test plans, and repos.
- AI agents author artifacts; **the human reviews and approves** before anything is pushed.
- Everything lands back in ADO: Test Cases, Test Runs, Bugs, links, and reports.

**Speaker notes:** Emphasise "human‑in‑the‑loop": AI drafts, QA approves. Nothing is filed silently.

---

## Slide 4 — The QA testing lifecycle, mapped (the centerpiece)

**Show a horizontal lifecycle diagram. Each stage = one feature of the tool.**

| QA lifecycle stage           | Manual today                      | With this tool                                                                             |
| ---------------------------- | --------------------------------- | ------------------------------------------------------------------------------------------ |
| 1. Requirement analysis (BA) | Read story, dig through code/docs | **Knowledgebase** + **Analyst agent** read the story, acceptance criteria, and linked code |
| 2. Test design               | Hand‑write cases in house style   | **Author Tests** generates house‑style Test Cases (steps, expected, priority)              |
| 3. Test automation           | Separate, often skipped           | **QA Loop** generates executable Playwright API tests from the module's API surface        |
| 4. Test execution            | Run manually, eyeball results     | **QA Runs** execute against the real QA environment, with authenticated API calls          |
| 5. Results & traceability    | Update test run by hand           | Auto‑created **ADO Test Run** with per‑case pass/fail + **Allure report**                  |
| 6. Defect reporting          | Hand‑file each bug                | **Draft Bugs** built from failures in the house style; one click to file to ADO            |
| 7. Regression / CI           | Re‑run everything                 | **QA Pipelines** watch builds and run only the *impacted* tests                            |
| 8. Reporting / metrics       | Manual status decks               | **Weekly board‑activity reports** + ADO dashboards                                         |

**Speaker notes:** This is the heart of the talk. Walk left‑to‑right. The message: *every* stage of
the QA lifecycle now has an AI‑assisted shortcut, and they all connect into one loop.

---

## Slide 5 — Feature 1: Knowledgebase (grounding the AI in your domain)

**Points:**
- Ingests your domain docs and code context so the AI writes *domain‑correct* tests and bugs.
- Uses full‑text retrieval (FTS) to pull the relevant chunks per story — no generic, hallucinated tests.
- This is what makes the output read like *your* QA author wrote it, not a generic bot.

**Speaker notes (BA angle):** As a Business Analyst, this is where your product knowledge is
captured once and reused by every agent — the AI inherits your context.

---

## Slide 6 — Feature 2: Author Tests (story → test cases)

**Flow to show (pipeline diagram):**
`ADO Story → Analyst agent (module, feature, acceptance criteria, edge & negative cases) → Tester agent → Test Case drafts → human review → push to ADO Test Plan/Suite`

**Points:**
- Pick a story; the Analyst extracts acceptance criteria, edge cases, and negative cases.
- The Tester turns them into full Test Cases: preconditions, numbered steps, expected results, priority.
- You **review and edit** every draft, then push. The tool creates the ADO Test Case, adds it to the
  suite, and links it back to the story (Tested‑By).

**Speaker notes:** Hours of test‑case writing become minutes of review. Traceability (story ↔ case)
is automatic.

---

## Slide 7 — Feature 3: QA Loop (module → executable automation)

**Flow to show:**
`Pick a module → scan its API surface → generate Contract / Endpoint / Workflow tests → review → push as ADO Test Cases (with runnable Playwright specs)`

**Points:**
- Three test levels, generated automatically:
  - **Contract** — response shape matches the expected schema.
  - **Endpoint** — status codes, auth (200/401), validation (400/422).
  - **Workflow** — chained business flows across multiple endpoints.
- Output is *executable* Playwright/TypeScript — not just prose. The test case and the automation are
  the same artifact, so they can't drift.

**Speaker notes:** This is the "automation‑first" leap: the automated check is a byproduct of testing
the story, generated by AI, run by machines.

---

## Slide 8 — Feature 4: QA Runs (execute for real + traceable results)

**Flow to show:**
`Select module → authenticate to QA (auto bearer‑token login) → run the Playwright suite → create an ADO Test Run → post per‑case outcomes → generate an Allure report`

**Points:**
- Runs the generated tests against the **real QA environment** — a single shared API base URL per
  environment, with a bearer token minted automatically at run start.
- Produces a proper **ADO Test Run**: every test case gets a real Passed/Failed outcome, visible on
  ADO dashboards.
- Ships a full **Allure HTML report** (trends, per‑test detail) linked from the run.
- Live console shows progress; results persist for later review.

**Speaker notes:** For a QA lead, this is the money slide: the same test cases you designed now show
real pass/fail in ADO with an executive‑grade report — no manual test‑run bookkeeping.

---

## Slide 9 — Feature 5: Draft Bugs (failures → house‑style bug reports)

**Show a before/after of a bug card.**

**Points:**
- Every failed test automatically becomes a **draft bug** in your house style:
  - Descriptive, problem‑focused title (e.g. *"DELETE /lead/task/{taskId} returns HTTP 500 instead of 200"*).
  - Preconditions → numbered Steps to reproduce → Expected Result → Actual Result.
  - Auto‑set severity, the failing assertion, and a "How this bug was found" provenance block.
  - Linked to the exact ADO Test Case.
- Review the drafts in a dedicated **Draft Bugs** tab, pick the real ones, and **file to ADO in one
  click** — optionally linked to a story.
- A separate **Find Bugs** flow does code‑aware bug hunting against a story + its linked changes.

**Speaker notes:** Defect reporting — historically the most tedious QA task — becomes review‑and‑click.
The format matches what your team already files by hand.

---

## Slide 10 — Feature 6: QA Pipelines (regression on autopilot)

**Points:**
- Watches an Azure DevOps build pipeline.
- On each new build, analyses the changed files and runs **only the impacted tests** (path‑ +
  logical‑dependency analysis), not the whole suite.
- Skips builds that don't touch the module — no noise.

**Speaker notes:** This is how the loop closes: every code change gets fast, targeted QA feedback
without a human kicking off runs.

---

## Slide 11 — Feature 7: Reporting

**Points:**
- Weekly, self‑contained **HTML board‑activity report** (what moved, who did what, spotlight items).
- Plus the per‑run Allure reports and native ADO Test Run dashboards.

**Speaker notes (BA angle):** Status reporting for stakeholders becomes a generated artifact, not a
Friday‑afternoon deck.

---

## Slide 12 — How it helps *me* as a QA / Business Analyst

**Show as outcome bullets (benefit‑led, not feature‑led):**
- **Speed:** hours of test‑case writing and bug filing collapse into minutes of review.
- **Consistency:** every artifact is in the same house style — no more per‑person variation.
- **Traceability:** story ↔ test case ↔ test run ↔ bug are linked automatically in ADO.
- **Coverage:** contract, endpoint, and workflow levels are generated, so gaps shrink.
- **Focus:** the human does judgement (is this a real bug? is this coverage right?), not typing.
- **Automation‑first:** the automated regression test is a *byproduct* of testing the story.
- **No context loss:** the Knowledgebase means the AI writes with your domain knowledge.

**Speaker notes:** Land the "judgement over typing" theme. QA becomes higher‑leverage, not replaced.

---

## Slide 13 — A 60‑second demo storyline (for a live/recorded demo)

1. Open a story in **Author Tests** → AI drafts test cases → review → push to ADO.
2. Switch to **QA Loop** → scan the Deals module → 171 test cases generated across 3 levels → push.
3. Go to **QA Runs** → start a run → watch it authenticate and execute live.
4. Open the finished run → **ADO Test Run** shows per‑case pass/fail; open the **Allure report**.
5. Open the **Draft Bugs** tab → review the auto‑drafted, house‑style bugs → file one to ADO.
6. (Optional) Show **QA Pipelines** turning a build into an impacted‑tests run.

**Speaker notes:** Keep it to one module (Deals) end‑to‑end so the loop is obvious.

---

## Slide 14 — Human‑in‑the‑loop & trust

**Points:**
- AI **drafts**; QA **reviews and approves** before any Test Case, Bug, or Test Run is written to ADO.
- Everything is editable before it's pushed.
- Outputs match a verified house style (byte‑exact ADO write formats).

**Speaker notes:** Pre‑empt the "will it file junk?" objection. Nothing is filed without review.

---

## Slide 15 — Under the hood (1 optional technical slide)

**Keep light; only if the audience is technical:**
- Local app: .NET 8 backend + React frontend, integrated with Azure DevOps (PAT).
- Pluggable LLM providers (local Claude CLI by default; API providers optional).
- Playwright/TypeScript for execution; Allure for reporting; ADO Test Plans/Runs for traceability.
- Runs on your machine — your data, your ADO org.

**Speaker notes:** The point isn't the stack; it's that it's self‑contained and ADO‑native.

---

## Slide 16 — Closing / call to action

**Statement:** Stop hand‑writing what a machine can draft. Spend QA time on judgement, exploration,
and quality — let the copilot handle the boilerplate across the whole lifecycle.

**Suggested closing visual:** the lifecycle loop from Slide 4, animated as a continuous cycle:
*Story → Test Cases → Automation → Run → Results → Bugs → (regression) → back to Story.*

---

## Appendix — Key talking points / soundbites

- "Manual‑first is a trap: manual execution eats the time to automate, so the next regression is
  manual too. This tool makes automation the byproduct of testing a story."
- "From story to test to bug — without leaving Azure DevOps."
- "AI drafts, QA decides. Nothing is filed without a human's approval."
- "The test case and the automated test are the same artifact — they can't drift."
- "Every failed test is already a house‑style bug draft, linked to its test case."
- "Regression runs itself: only the impacted tests, on every build."

## Appendix — Suggested visuals for the designer

- A left‑to‑right **QA lifecycle ribbon** (8 stages) with a tool feature under each stage.
- A **pipeline diagram** for Author Tests (Analyst → Tester → review → ADO).
- A **before/after** split for a bug report (raw failure vs house‑style draft).
- A **screenshot mock** of the QA Runs drawer showing tabs: Console · Draft Bugs · Failures · Impact.
- A **loop animation** for the closing slide.
