import { useState, useEffect } from 'react'
import {
  Alert, Autocomplete, Box, Button, Chip, CircularProgress, Collapse, Divider, FormControlLabel,
  Link, Snackbar, Stack, Switch, Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography,
} from '@mui/material'
import { alpha } from '@mui/material/styles'
import MenuBookIcon from '@mui/icons-material/MenuBookOutlined'
import CodeIcon from '@mui/icons-material/CodeOutlined'
import BugReportIcon from '@mui/icons-material/BugReportOutlined'
import ArticleIcon from '@mui/icons-material/ArticleOutlined'
import OpenInNewIcon from '@mui/icons-material/OpenInNewOutlined'
import AssessmentIcon from '@mui/icons-material/AssessmentOutlined'
import PageHeader from '../components/PageHeader'
import Section from '../components/Section'
import { PROJECT_ID } from '../config/env'
import { axiosMessage } from '../common/errors'
import {
  extractKb, generateSpec, ingestResults, analyzeModule, generateFlowSpec, adoWorkItemUrl,
  type KbExtractionResult, type GenerateSpecResult, type IngestResultsResult, type FailureOutcome,
  type ModuleAnalyzeResult, type ModuleFlow, type ModuleFlowSpecResult,
} from '../api/qaLoop'
import {
  getConfig, getAdoRepositories, getAdoBranches, getAdoTestPlans, getAdoTestSuites,
  type AdoTestPlan, type AdoTestSuite,
} from '../api/settings'

// Local write target for generated specs (the /automation folder in a local clone). Editable per run.
const DEFAULT_OUTPUT_ROOT = 'C:\\Users\\temp\\Downloads\\Projects\\TCRN\\CRM Project\\web-app\\src\\target-crm\\automation'

const classColor = (c: string): 'error' | 'warning' | 'info' | 'default' =>
  c === 'real_bug' ? 'error' : c === 'flaky' ? 'warning' : c === 'test_issue' ? 'info' : 'default'
const classLabel = (c: string) =>
  ({ real_bug: 'Real bug', flaky: 'Flaky', test_issue: 'Test issue', stale_test: 'Stale test' } as Record<string, string>)[c] ?? c

export default function QaLoopPage() {
  // Shared context — source = which ADO repo + branch (defaults from Settings, overridable here)
  const [module, setModule] = useState('deals')
  const [repository, setRepository] = useState('')
  const [branch, setBranch] = useState('')
  const [repoOptions, setRepoOptions] = useState<string[]>([])
  const [branchOptions, setBranchOptions] = useState<string[]>([])
  const [branchesLoading, setBranchesLoading] = useState(false)
  const [toast, setToast] = useState('')

  // Prefill repo + branch from Settings (ado.repository / ado.default_branch), and load the repo list.
  useEffect(() => {
    getConfig()
      .then(cfg => {
        setRepository(String(cfg['ado.repository'] ?? ''))
        setBranch(String(cfg['ado.default_branch'] ?? ''))
      })
      .catch(() => {})
    getAdoRepositories().then(setRepoOptions).catch(() => setRepoOptions([]))
  }, [])

  // Load branches for the selected repository (debounced so typing doesn't spam ADO).
  useEffect(() => {
    if (!repository.trim()) { setBranchOptions([]); return }
    setBranchesLoading(true)
    const h = setTimeout(() => {
      getAdoBranches(repository)
        .then(setBranchOptions)
        .catch(() => setBranchOptions([]))
        .finally(() => setBranchesLoading(false))
    }, 400)
    return () => clearTimeout(h)
  }, [repository])

  // 0 — Analyze whole module (plan/suite coverage → per-flow specs on demand)
  const [plan, setPlan] = useState<AdoTestPlan | null>(null)
  const [suite, setSuite] = useState<AdoTestSuite | null>(null)
  const [planOptions, setPlanOptions] = useState<AdoTestPlan[]>([])
  const [suiteOptions, setSuiteOptions] = useState<AdoTestSuite[]>([])
  const [plansLoading, setPlansLoading] = useState(false)
  const [suitesLoading, setSuitesLoading] = useState(false)
  const [analyzing, setAnalyzing] = useState(false)
  const [analysis, setAnalysis] = useState<ModuleAnalyzeResult | null>(null)
  const [analyzeError, setAnalyzeError] = useState('')
  const [gapStoryId, setGapStoryId] = useState('')
  // per-flow spec generation, keyed by flow index
  const [rowState, setRowState] = useState<Record<number, { loading: boolean; result?: ModuleFlowSpecResult; error?: string }>>({})
  const [openCode, setOpenCode] = useState<Record<number, boolean>>({})

  // Load test plans once.
  useEffect(() => {
    setPlansLoading(true)
    getAdoTestPlans().then(setPlanOptions).catch(() => setPlanOptions([])).finally(() => setPlansLoading(false))
  }, [])

  // Load suites when the plan changes (and reset the chosen suite).
  useEffect(() => {
    setSuite(null); setSuiteOptions([])
    if (!plan) return
    setSuitesLoading(true)
    getAdoTestSuites(plan.id)
      .then(setSuiteOptions)
      .catch(() => setSuiteOptions([]))
      .finally(() => setSuitesLoading(false))
  }, [plan])

  // 1 — Extract
  const [flows, setFlows] = useState('Create Deal')
  const [extracting, setExtracting] = useState(false)
  const [kbResult, setKbResult] = useState<KbExtractionResult | null>(null)
  const [kbError, setKbError] = useState('')

  // 2 — Generate
  const [scenarioTitle, setScenarioTitle] = useState('Create a deal with a customer')
  const [kbFileName, setKbFileName] = useState('deals-create-deal.md')
  const [createAdo, setCreateAdo] = useState(true)
  const [genStoryId, setGenStoryId] = useState('')
  const [outputRoot, setOutputRoot] = useState(DEFAULT_OUTPUT_ROOT)
  const [generating, setGenerating] = useState(false)
  const [genResult, setGenResult] = useState<GenerateSpecResult | null>(null)
  const [genError, setGenError] = useState('')
  const [showCode, setShowCode] = useState(false)

  // 3 — Ingest
  const [junitPath, setJunitPath] = useState('')
  const [ingestKbFile, setIngestKbFile] = useState('deals-create-deal.md')
  const [fileBugs, setFileBugs] = useState(false)
  const [ingestStoryId, setIngestStoryId] = useState('')
  const [ingesting, setIngesting] = useState(false)
  const [ingestResult, setIngestResult] = useState<IngestResultsResult | null>(null)
  const [ingestError, setIngestError] = useState('')

  async function onAnalyze() {
    if (!plan || !suite) return
    setAnalyzeError(''); setAnalyzing(true); setAnalysis(null); setRowState({}); setOpenCode({})
    try {
      const res = await analyzeModule(PROJECT_ID, {
        module,
        repository: repository.trim() || undefined,
        branch: branch.trim() || undefined,
        planId: plan.id,
        suiteId: suite.id,
        recurse: true,
      })
      setAnalysis(res)
      setToast(`Analyzed ${module}: ${res.flowCount} flow(s) — ${res.coveredCount} covered, ${res.gapCount} gap(s)`)
    } catch (e) { setAnalyzeError(axiosMessage(e)) }
    finally { setAnalyzing(false) }
  }

  async function onGenerateFlow(index: number, flow: ModuleFlow) {
    if (!plan || !suite) return
    setRowState(s => ({ ...s, [index]: { loading: true } }))
    try {
      const res = await generateFlowSpec(PROJECT_ID, {
        module,
        repository: repository.trim() || undefined,
        branch: branch.trim() || undefined,
        planId: plan.id,
        suiteId: suite.id,
        flowName: flow.name,
        flowSummary: flow.summary,
        coveringTestCaseIds: flow.coveringTestCaseIds,
        coveringTestCaseTitles: flow.coveringTestCaseTitles,
        storyId: !flow.covered && gapStoryId.trim() ? Number(gapStoryId) : undefined,
        smoke: true,
        outputRoot,
      })
      setRowState(s => ({ ...s, [index]: { loading: false, result: res } }))
      const tc = res.spec.adoTestCaseId
      setToast(res.wasGap
        ? `Gap "${flow.name}": created Test Case #${tc} + spec`
        : `"${flow.name}": spec generated for Test Case #${tc}`)
    } catch (e) {
      setRowState(s => ({ ...s, [index]: { loading: false, error: axiosMessage(e) } }))
    }
  }

  async function onExtract() {
    setKbError(''); setExtracting(true); setKbResult(null)
    try {
      const flowList = flows.split(',').map(s => s.trim()).filter(Boolean)
      const res = await extractKb(PROJECT_ID, {
        module,
        repository: repository.trim() || undefined,
        branch: branch.trim() || undefined,
        flows: flowList,
      })
      setKbResult(res)
      setToast(`Extracted ${res.docs.length} KB doc(s) from ${res.filesRead} file(s)`)
    } catch (e) { setKbError(axiosMessage(e)) }
    finally { setExtracting(false) }
  }

  async function onGenerate() {
    setGenError(''); setGenerating(true); setGenResult(null); setShowCode(false)
    try {
      const res = await generateSpec(PROJECT_ID, {
        module,
        scenarioTitle,
        kbFileName,
        createAdoTestCase: createAdo,
        storyId: genStoryId.trim() ? Number(genStoryId) : undefined,
        outputRoot,
        smoke: true,
      })
      setGenResult(res)
      setToast(res.adoTestCaseCreated ? `Generated spec + created ADO Test Case #${res.adoTestCaseId}` : 'Spec generated')
    } catch (e) { setGenError(axiosMessage(e)) }
    finally { setGenerating(false) }
  }

  async function onIngest() {
    setIngestError(''); setIngesting(true); setIngestResult(null)
    try {
      const res = await ingestResults(PROJECT_ID, {
        junitPath,
        kbFileName: ingestKbFile || undefined,
        fileBugs,
        storyId: ingestStoryId.trim() ? Number(ingestStoryId) : undefined,
      })
      setIngestResult(res)
      setToast(fileBugs ? `Ingested ${res.failures} failure(s), filed ${res.bugsFiled} bug(s)` : `Triaged ${res.failures} failure(s) (dry run)`)
    } catch (e) { setIngestError(axiosMessage(e)) }
    finally { setIngesting(false) }
  }

  return (
    <Box sx={{ pb: 4 }}>
      <PageHeader
        title="QA Loop"
        subtitle="The Deals AI QA loop end to end: extract the knowledgebase, generate a Playwright spec (and its ADO Test Case), then ingest a run and file bugs from failures."
      />

      <Stack spacing={3} sx={{ maxWidth: 980 }}>

        {/* Shared context — the source: which repo + which branch (from Settings, overridable) */}
        <Section title="Context">
          <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1.5}>
            <TextField size="small" label="Module" value={module} onChange={e => setModule(e.target.value)} sx={{ width: { sm: 140 } }} />
            <Autocomplete freeSolo fullWidth options={repoOptions} value={repository}
              onChange={(_, v) => setRepository(v || '')}
              onInputChange={(_, v) => setRepository(v)}
              renderInput={p => (
                <TextField {...p} size="small" label="Source repository (Azure Repos)"
                  helperText={repoOptions.length ? `${repoOptions.length} repo(s) — default from Settings` : 'Type a repository'} />
              )} />
            <Autocomplete freeSolo options={branchOptions} value={branch} sx={{ width: { sm: 220 } }}
              onChange={(_, v) => setBranch(v || '')}
              onInputChange={(_, v) => setBranch(v)}
              loading={branchesLoading}
              renderInput={p => (
                <TextField {...p} size="small" label="Branch" placeholder="main"
                  helperText={branchesLoading ? 'Loading branches…' : branchOptions.length ? `${branchOptions.length} branch(es)` : ' '} />
              )} />
          </Stack>
          <Typography variant="caption" color="text.secondary" sx={{ mt: 0.25, display: 'block' }}>
            Source code is read from this repo + branch. Defaults come from Settings; override per run here.
          </Typography>
        </Section>

        {/* 0 — Analyze whole module: plan/suite coverage → per-flow specs on demand */}
        <Section title="Analyze module coverage">
          <Stack spacing={1.5}>
            <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1.5}>
              <Autocomplete fullWidth options={planOptions} value={plan}
                onChange={(_, v) => setPlan(v)}
                getOptionLabel={o => `${o.name} (#${o.id})`}
                isOptionEqualToValue={(a, b) => a.id === b.id}
                loading={plansLoading}
                renderInput={p => (
                  <TextField {...p} size="small" label="Test plan"
                    helperText={plansLoading ? 'Loading plans…' : planOptions.length ? `${planOptions.length} plan(s)` : ' '} />
                )} />
              <Autocomplete fullWidth options={suiteOptions} value={suite}
                onChange={(_, v) => setSuite(v)}
                getOptionLabel={o => `${o.name} (#${o.id})`}
                isOptionEqualToValue={(a, b) => a.id === b.id}
                loading={suitesLoading} disabled={!plan}
                renderInput={p => (
                  <TextField {...p} size="small" label="Test suite (includes sub-suites)"
                    helperText={!plan ? 'Pick a plan first' : suitesLoading ? 'Loading suites…' : suiteOptions.length ? `${suiteOptions.length} suite(s)` : ' '} />
                )} />
            </Stack>
            <Box>
              <Button variant="contained" startIcon={<AssessmentIcon />} onClick={onAnalyze}
                disabled={analyzing || !module.trim() || !plan || !suite}>
                {analyzing ? <CircularProgress size={18} sx={{ color: 'white' }} /> : 'Analyze module'}
              </Button>
              <Typography variant="caption" color="text.secondary" sx={{ ml: 1.5 }}>
                Reads the module source + the suite's existing cases, discovers every flow, and maps coverage.
              </Typography>
            </Box>
            {analyzeError && <Alert severity="error" onClose={() => setAnalyzeError('')}>{analyzeError}</Alert>}
            {analysis && (
              <Box>
                <Stack direction="row" spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap', gap: 1, mb: 1 }}>
                  <Chip size="small" label={`${analysis.flowCount} flows`} />
                  <Chip size="small" color="success" variant="outlined" label={`${analysis.coveredCount} covered`} />
                  <Chip size="small" color="warning" variant="outlined" label={`${analysis.gapCount} gaps`} />
                  <Chip size="small" variant="outlined" icon={<ArticleIcon sx={{ fontSize: 14 }} />} label={analysis.kbDocFileName} />
                  <Box sx={{ flex: 1 }} />
                  <TextField size="small" label="Link new gap cases to story id" value={gapStoryId}
                    onChange={e => setGapStoryId(e.target.value)} sx={{ width: 220 }} />
                </Stack>
                <Box sx={{ border: '1px solid', borderColor: 'divider', borderRadius: 2, overflow: 'hidden' }}>
                  <Table size="small">
                    <TableHead>
                      <TableRow>
                        <TableCell>Flow</TableCell>
                        <TableCell sx={{ width: 220 }}>Coverage</TableCell>
                        <TableCell sx={{ width: 260 }}>Spec</TableCell>
                      </TableRow>
                    </TableHead>
                    <TableBody>
                      {analysis.flows.map((f, i) => (
                        <FlowRow key={i} flow={f}
                          state={rowState[i]} codeOpen={!!openCode[i]}
                          onGenerate={() => onGenerateFlow(i, f)}
                          onToggleCode={() => setOpenCode(s => ({ ...s, [i]: !s[i] }))} />
                      ))}
                    </TableBody>
                  </Table>
                </Box>
                <Typography variant="caption" color="text.secondary" sx={{ mt: 0.75, display: 'block' }}>
                  Generate a spec per flow on demand — a covered flow tags its existing test case; a gap creates a new ADO Test Case in the suite, then the spec. Specs are written to the /automation root set below.
                </Typography>
              </Box>
            )}
          </Stack>
        </Section>

        {/* 1 — Extract KB */}
        <Section title="1 · Extract knowledgebase">
          <Stack spacing={1.5}>
            <TextField size="small" fullWidth label="Flows (comma-separated)" value={flows} onChange={e => setFlows(e.target.value)}
              helperText="One KB doc per flow, plus a module rules.md." />
            <Box>
              <Button variant="contained" startIcon={<MenuBookIcon />} onClick={onExtract}
                disabled={extracting || !module.trim() || !flows.trim()}>
                {extracting ? <CircularProgress size={18} sx={{ color: 'white' }} /> : 'Extract KB'}
              </Button>
            </Box>
            {kbError && <Alert severity="error" onClose={() => setKbError('')}>{kbError}</Alert>}
            {kbResult && (
              <Box sx={{ p: 1.5, borderRadius: 2, border: '1px solid', borderColor: 'divider' }}>
                <Typography variant="body2" color="text.secondary" gutterBottom>
                  Read {kbResult.filesRead} file(s) · {kbResult.bundleChars.toLocaleString()} chars
                </Typography>
                <Stack direction="row" spacing={1} sx={{ flexWrap: 'wrap', gap: 1 }}>
                  {kbResult.docs.map(d => (
                    <Chip key={d.documentId} size="small" variant="outlined" icon={<ArticleIcon sx={{ fontSize: 14 }} />}
                      label={`${d.fileName} (${(d.charCount / 1024).toFixed(1)}KB)`}
                      color={d.kind === 'rules' ? 'secondary' : 'default'} />
                  ))}
                </Stack>
              </Box>
            )}
          </Stack>
        </Section>

        {/* 2 — Generate spec */}
        <Section title="2 · Generate Playwright spec">
          <Stack spacing={1.5}>
            <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1.5}>
              <TextField size="small" fullWidth label="Scenario title" value={scenarioTitle} onChange={e => setScenarioTitle(e.target.value)} />
              <TextField size="small" label="KB doc" value={kbFileName} onChange={e => setKbFileName(e.target.value)} sx={{ width: { sm: 220 } }} />
            </Stack>
            <Stack direction="row" spacing={2} sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
              <FormControlLabel control={<Switch checked={createAdo} onChange={e => setCreateAdo(e.target.checked)} />}
                label="Create ADO Test Case" />
              <TextField size="small" label="Link to story id (optional)" value={genStoryId} onChange={e => setGenStoryId(e.target.value)} sx={{ width: 200 }} />
            </Stack>
            <TextField size="small" fullWidth label="Output /automation root (local write target)" value={outputRoot}
              onChange={e => setOutputRoot(e.target.value)}
              helperText={`Spec is written to …\\e2e\\${module}\\`} />
            <Box>
              <Button variant="contained" startIcon={<CodeIcon />} onClick={onGenerate}
                disabled={generating || !module.trim() || !scenarioTitle.trim() || !kbFileName.trim()}>
                {generating ? <CircularProgress size={18} sx={{ color: 'white' }} /> : 'Generate spec'}
              </Button>
            </Box>
            {genError && <Alert severity="error" onClose={() => setGenError('')}>{genError}</Alert>}
            {genResult && (
              <Box sx={{ p: 1.5, borderRadius: 2, border: '1px solid', borderColor: 'divider' }}>
                <Stack direction="row" spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap', gap: 1, mb: 1 }}>
                  {genResult.adoTestCaseId != null && (
                    <Chip size="small" color="primary" icon={<OpenInNewIcon sx={{ fontSize: 14 }} />}
                      component={Link} clickable href={adoWorkItemUrl(genResult.adoTestCaseId)} target="_blank" rel="noreferrer"
                      label={`Test Case #${genResult.adoTestCaseId}${genResult.adoTestCaseCreated ? ' (created)' : ''}`} />
                  )}
                  <Chip size="small" variant="outlined" label={genResult.fileName} />
                  {genResult.written && <Chip size="small" variant="outlined" color="success" label="written" />}
                </Stack>
                <Typography variant="caption" color="text.secondary" sx={{ fontFamily: 'monospace', wordBreak: 'break-all' }}>
                  {genResult.testTitle}
                </Typography>
                <Box sx={{ mt: 1 }}>
                  <Button size="small" onClick={() => setShowCode(v => !v)}>{showCode ? 'Hide' : 'Show'} code ({genResult.charCount} chars)</Button>
                  <Collapse in={showCode}>
                    <Box component="pre" sx={{
                      mt: 1, p: 1.5, borderRadius: 2, fontSize: 12, overflow: 'auto', maxHeight: 360,
                      bgcolor: theme => alpha(theme.palette.text.primary, 0.04), m: 0,
                    }}>{genResult.code}</Box>
                  </Collapse>
                </Box>
              </Box>
            )}
          </Stack>
        </Section>

        {/* 3 — Ingest results */}
        <Section title="3 · Ingest run results → triage → file bugs">
          <Stack spacing={1.5}>
            <TextField size="small" fullWidth label="JUnit results path" value={junitPath} onChange={e => setJunitPath(e.target.value)}
              placeholder="…\tests-e2e\test-results\junit.xml" />
            <Stack direction="row" spacing={2} sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
              <TextField size="small" label="KB doc (grounds triage)" value={ingestKbFile} onChange={e => setIngestKbFile(e.target.value)} sx={{ width: 240 }} />
              <TextField size="small" label="Link bugs to story id (optional)" value={ingestStoryId} onChange={e => setIngestStoryId(e.target.value)} sx={{ width: 220 }} />
              <FormControlLabel control={<Switch checked={fileBugs} onChange={e => setFileBugs(e.target.checked)} color="error" />}
                label={fileBugs ? 'File bugs (writes to ADO)' : 'Dry run (triage only)'} />
            </Stack>
            <Box>
              <Button variant="contained" color={fileBugs ? 'error' : 'primary'} startIcon={<BugReportIcon />} onClick={onIngest}
                disabled={ingesting || !junitPath.trim()}>
                {ingesting ? <CircularProgress size={18} sx={{ color: 'white' }} /> : fileBugs ? 'Ingest + file bugs' : 'Ingest (dry run)'}
              </Button>
            </Box>
            {ingestError && <Alert severity="error" onClose={() => setIngestError('')}>{ingestError}</Alert>}
            {ingestResult && (
              <Box>
                <Typography variant="body2" color="text.secondary" gutterBottom>
                  {ingestResult.totalTests} test(s) · {ingestResult.failures} failure(s) · {ingestResult.bugsFiled} bug(s) filed
                </Typography>
                <Stack spacing={1.25}>
                  {ingestResult.outcomes.map((o, i) => <OutcomeCard key={i} o={o} />)}
                </Stack>
              </Box>
            )}
          </Stack>
        </Section>
      </Stack>

      <Snackbar open={!!toast} autoHideDuration={3500} onClose={() => setToast('')}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'right' }}>
        <Alert severity="success" onClose={() => setToast('')} variant="filled">{toast}</Alert>
      </Snackbar>
    </Box>
  )
}

function FlowRow({ flow, state, codeOpen, onGenerate, onToggleCode }: {
  flow: ModuleFlow
  state?: { loading: boolean; result?: ModuleFlowSpecResult; error?: string }
  codeOpen: boolean
  onGenerate: () => void
  onToggleCode: () => void
}) {
  const result = state?.result
  const spec = result?.spec
  return (
    <>
      <TableRow sx={{ '& td': { borderBottom: codeOpen ? 'none' : undefined, verticalAlign: 'top' } }}>
        <TableCell>
          <Typography variant="body2" sx={{ fontWeight: 600 }}>{flow.name}</Typography>
          {flow.summary && <Typography variant="caption" color="text.secondary">{flow.summary}</Typography>}
        </TableCell>
        <TableCell>
          {flow.covered ? (
            <Stack direction="row" spacing={0.5} sx={{ flexWrap: 'wrap', gap: 0.5 }}>
              <Chip size="small" color="success" label="Covered" />
              {flow.coveringTestCaseIds.map(id => (
                <Chip key={id} size="small" variant="outlined" clickable component={Link}
                  href={adoWorkItemUrl(id)} target="_blank" rel="noreferrer" label={`#${id}`} />
              ))}
            </Stack>
          ) : (
            <Chip size="small" color="warning" label="Gap" />
          )}
        </TableCell>
        <TableCell>
          {state?.loading ? (
            <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
              <CircularProgress size={16} /><Typography variant="caption">Generating…</Typography>
            </Stack>
          ) : result && spec ? (
            <Stack spacing={0.5}>
              <Stack direction="row" spacing={0.5} sx={{ flexWrap: 'wrap', gap: 0.5 }}>
                {spec.adoTestCaseId != null && (
                  <Chip size="small" color="primary" icon={<OpenInNewIcon sx={{ fontSize: 14 }} />}
                    component={Link} clickable href={adoWorkItemUrl(spec.adoTestCaseId)} target="_blank" rel="noreferrer"
                    label={`#${spec.adoTestCaseId}${spec.adoTestCaseCreated ? ' (created)' : ''}`} />
                )}
                {spec.written && <Chip size="small" variant="outlined" color="success" label="written" />}
              </Stack>
              <Button size="small" startIcon={<CodeIcon sx={{ fontSize: 16 }} />} onClick={onToggleCode} sx={{ alignSelf: 'flex-start' }}>
                {codeOpen ? 'Hide' : 'Show'} code ({spec.charCount})
              </Button>
            </Stack>
          ) : (
            <Stack spacing={0.5}>
              <Button size="small" variant="outlined" startIcon={<CodeIcon sx={{ fontSize: 16 }} />} onClick={onGenerate}>
                {state?.error ? 'Retry' : 'Generate spec'}
              </Button>
              {state?.error && <Typography variant="caption" color="error">{state.error}</Typography>}
            </Stack>
          )}
        </TableCell>
      </TableRow>
      {result && spec && (
        <TableRow>
          <TableCell colSpan={3} sx={{ py: 0 }}>
            <Collapse in={codeOpen} timeout="auto" unmountOnExit>
              <Box component="pre" sx={{
                my: 1, p: 1.5, borderRadius: 2, fontSize: 12, overflow: 'auto', maxHeight: 320,
                bgcolor: theme => alpha(theme.palette.text.primary, 0.04),
              }}>{spec.code}</Box>
            </Collapse>
          </TableCell>
        </TableRow>
      )}
    </>
  )
}

function OutcomeCard({ o }: { o: FailureOutcome }) {
  return (
    <Box sx={{ p: 1.5, borderRadius: 2, border: '1px solid', borderColor: 'divider' }}>
      <Stack direction="row" spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap', gap: 1, mb: 0.75 }}>
        <Chip size="small" color={classColor(o.classification)} label={`${classLabel(o.classification)} · ${o.confidence}`} />
        {o.adoTestCaseId != null && <Chip size="small" variant="outlined" label={`TC #${o.adoTestCaseId}`} />}
        {o.filed && o.bugId != null && (
          <Chip size="small" color="error" icon={<OpenInNewIcon sx={{ fontSize: 14 }} />}
            component={Link} clickable href={adoWorkItemUrl(o.bugId)} target="_blank" rel="noreferrer"
            label={`Bug #${o.bugId} [${o.severity}]`} />
        )}
        <Box sx={{ flex: 1 }} />
        <Typography variant="caption" color="text.secondary" sx={{ fontFamily: 'monospace', maxWidth: '50%' }} noWrap>
          {o.testTitle}
        </Typography>
      </Stack>
      <Typography variant="body2" sx={{ fontWeight: 600 }}>{o.bugTitle || '(no bug title)'}</Typography>
      <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>{o.reason}</Typography>
      {(o.reproSteps.length > 0 || o.observations.length > 0) && (
        <>
          <Divider sx={{ my: 1 }} />
          {o.expected && <Typography variant="caption" sx={{ display: 'block' }}><b>Expected:</b> {o.expected}</Typography>}
          {o.observations.length > 0 && (
            <Typography variant="caption" sx={{ display: 'block' }}><b>Actual:</b> {o.observations.join(' ')}</Typography>
          )}
          {o.artifacts.length > 0 && (
            <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 0.5 }}>
              {o.artifacts.length} artifact(s) attached
            </Typography>
          )}
        </>
      )}
    </Box>
  )
}
