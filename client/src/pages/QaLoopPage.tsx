import { useState, useEffect } from 'react'
import {
  Alert, Box, Button, Chip, CircularProgress, Collapse, Divider, FormControlLabel,
  Link, Snackbar, Stack, Switch, TextField, Typography,
} from '@mui/material'
import { alpha } from '@mui/material/styles'
import MenuBookIcon from '@mui/icons-material/MenuBookOutlined'
import CodeIcon from '@mui/icons-material/CodeOutlined'
import BugReportIcon from '@mui/icons-material/BugReportOutlined'
import ArticleIcon from '@mui/icons-material/ArticleOutlined'
import OpenInNewIcon from '@mui/icons-material/OpenInNewOutlined'
import PageHeader from '../components/PageHeader'
import Section from '../components/Section'
import { PROJECT_ID } from '../config/env'
import { axiosMessage } from '../common/errors'
import {
  extractKb, generateSpec, ingestResults, adoWorkItemUrl,
  type KbExtractionResult, type GenerateSpecResult, type IngestResultsResult, type FailureOutcome,
} from '../api/qaLoop'
import { getConfig } from '../api/settings'

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
  const [settingsHint, setSettingsHint] = useState('')
  const [toast, setToast] = useState('')

  // Prefill repo + branch from Settings (ado.repository / ado.default_branch).
  useEffect(() => {
    getConfig()
      .then(cfg => {
        const repo = String(cfg['ado.repository'] ?? '')
        const br = String(cfg['ado.default_branch'] ?? '')
        setRepository(repo)
        setBranch(br)
        setSettingsHint(repo ? `Defaulted from Settings: ${repo}${br ? ` @ ${br}` : ''}` : 'Set a default repository in Settings, or enter one here.')
      })
      .catch(() => setSettingsHint('Could not load Settings — enter a repository + branch.'))
  }, [])

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
            <TextField size="small" fullWidth label="Source repository (Azure Repos)" value={repository} onChange={e => setRepository(e.target.value)} />
            <TextField size="small" label="Branch" value={branch} onChange={e => setBranch(e.target.value)} sx={{ width: { sm: 200 } }} placeholder="main" />
          </Stack>
          <Typography variant="caption" color="text.secondary" sx={{ mt: 0.75, display: 'block' }}>
            {settingsHint} · Source code is read from this repo + branch (override per run here).
          </Typography>
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
