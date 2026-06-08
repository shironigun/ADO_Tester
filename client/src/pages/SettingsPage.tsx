import { useCallback, useEffect, useState } from 'react'
import {
  Alert, Autocomplete, Box, Button, Card, CardContent, Chip, CircularProgress, Divider,
  FormControl, FormControlLabel, IconButton, InputAdornment, InputLabel,
  MenuItem, Select, Snackbar, Stack, Switch, TextField, Typography,
} from '@mui/material'
import CheckCircleIcon from '@mui/icons-material/CheckCircle'
import ErrorIcon from '@mui/icons-material/Error'
import VisibilityIcon from '@mui/icons-material/Visibility'
import VisibilityOffIcon from '@mui/icons-material/VisibilityOff'
import SaveIcon from '@mui/icons-material/Save'
import axios from 'axios'

// ─── setting keys (must match backend constants) ─────────────────────────────
const K = {
  ADO_ORG: 'ado.organization',
  ADO_PROJECT: 'ado.project',
  ADO_PAT: 'ado.pat',
  ANTHROPIC_KEY: 'anthropic.api_key',
  ANTHROPIC_MODEL: 'anthropic.model',
  AZURE_ENDPOINT: 'azure_openai.endpoint',
  AZURE_KEY: 'azure_openai.api_key',
  AZURE_DEPLOY: 'azure_openai.deployment',
  OPENAI_BASE_URL: 'openai.base_url',
  OPENAI_KEY: 'openai.api_key',
  OPENAI_MODEL: 'openai.model',
  CLAUDE_CLI_PATH: 'claude_cli.path',
  CLAUDE_CLI_MODEL: 'claude_cli.model',
  CLAUDE_CLI_TIMEOUT: 'claude_cli.timeout_seconds',
  DEFAULT_PLAN_ID: 'defaults.test_plan_id',
  DEFAULT_SUITE_ID: 'defaults.test_suite_id',
  DEFAULT_BOARDS: 'reports.default_boards',
  SCHED_ENABLED: 'reports.schedule_enabled',
  DEFAULT_RANGE: 'reports.default_range_days',
}

type Config = Record<string, string | boolean>

interface EnvStatus {
  activeProvider: string
  adoPatPresent: boolean
  anthropicKeyPresent: boolean
  azureOpenAiConfigured: boolean
  claudeCliAvailable: boolean
  claudeCliVersion: string | null
}
interface WorkItem {
  id: number; title: string; workItemType: string; state: string
  assignedTo?: string
}
interface TestPlan { id: number; name: string }
interface TestSuite { id: number; name: string }
interface AdoProject { id: string; name: string }

function axiosMessage(e: unknown): string {
  if (!axios.isAxiosError(e)) return String(e)
  const d = e.response?.data as unknown
  if (typeof d === 'string') return d
  if (d && typeof d === 'object' && 'detail' in d) return String((d as { detail: string }).detail)
  return e.message
}

// Secret-aware TextField. Backend never echoes secret characters — only a `.set` flag.
// When set, label + helper-text + a green "Configured" chip make the existing-value state obvious.
function SecretField({ label, value, set, isSet }: {
  label: string
  value: string
  set: (v: string) => void
  isSet: boolean
}) {
  const [shown, setShown] = useState(false)
  return (
    <TextField
      label={isSet ? `${label} — Configured` : label}
      size="small"
      fullWidth
      type={shown ? 'text' : 'password'}
      value={value}
      onChange={e => set(e.target.value)}
      placeholder={isSet ? 'Configured — leave blank to keep current value' : 'Not set'}
      helperText={isSet
        ? 'Currently configured. Type a new value to replace it, or leave blank to keep the saved value.'
        : 'Not set yet.'}
      slotProps={{
        input: {
          startAdornment: isSet ? (
            <InputAdornment position="start">
              <Chip size="small" label="Configured" color="success" sx={{ height: 22 }} />
            </InputAdornment>
          ) : undefined,
          endAdornment: (
            <InputAdornment position="end">
              <IconButton size="small" onClick={() => setShown(s => !s)} disabled={!value}>
                {shown ? <VisibilityOffIcon fontSize="small" /> : <VisibilityIcon fontSize="small" />}
              </IconButton>
            </InputAdornment>
          ),
        },
      }}
    />
  )
}

export default function SettingsPage() {
  const [config, setConfig] = useState<Config>({})
  const [loading, setLoading] = useState(true)
  const [toast, setToast] = useState('')
  const [error, setError] = useState('')

  // Per-section staged edits (start empty for secrets, current for non-secrets)
  const [adoOrg, setAdoOrg] = useState('')
  const [adoProject, setAdoProject] = useState('')
  const [adoPat, setAdoPat] = useState('')
  const [anthropicKey, setAnthropicKey] = useState('')
  const [anthropicModel, setAnthropicModel] = useState('')
  const [azureEndpoint, setAzureEndpoint] = useState('')
  const [azureKey, setAzureKey] = useState('')
  const [azureDeploy, setAzureDeploy] = useState('')
  const [openaiBase, setOpenaiBase] = useState('')
  const [openaiKey, setOpenaiKey] = useState('')
  const [openaiModel, setOpenaiModel] = useState('')
  const [claudeCliPath, setClaudeCliPath] = useState('')
  const [claudeCliModel, setClaudeCliModel] = useState('')
  const [claudeCliTimeout, setClaudeCliTimeout] = useState('180')
  const [claudeCliModels, setClaudeCliModels] = useState<string[]>([])
  const [defaultPlan, setDefaultPlan] = useState<number | ''>('')
  const [defaultSuite, setDefaultSuite] = useState<number | ''>('')
  // boardsList is the source of truth for the multi-select; the saved CSV form is derived on save.
  const [schedEnabled, setSchedEnabled] = useState(true)
  const [defaultRange, setDefaultRange] = useState('7')

  // Plans / suites for default-suite dropdowns
  const [plans, setPlans] = useState<TestPlan[]>([])
  const [suites, setSuites] = useState<TestSuite[]>([])

  // PAT-driven discovery
  const [orgs, setOrgs] = useState<string[]>([])
  const [projects, setProjects] = useState<AdoProject[]>([])
  const [projectsLoading, setProjectsLoading] = useState(false)
  const [areaPaths, setAreaPaths] = useState<string[]>([])
  const [areasLoading, setAreasLoading] = useState(false)
  // Boards multi-select state (parsed CSV ↔ list)
  const [boardsList, setBoardsList] = useState<string[]>([])

  // Diagnostics state
  const [env, setEnv] = useState<EnvStatus | null>(null)
  const [envLoading, setEnvLoading] = useState(false)
  const [wiId, setWiId] = useState('')
  const [workItem, setWorkItem] = useState<WorkItem | null>(null)
  const [wiLoading, setWiLoading] = useState(false)
  const [wiError, setWiError] = useState('')
  const [llmReply, setLlmReply] = useState('')
  const [llmLoading, setLlmLoading] = useState(false)
  const [llmError, setLlmError] = useState('')

  const loadConfig = useCallback(async () => {
    setLoading(true)
    try {
      const { data } = await axios.get<Config>('/api/settings/config')
      setConfig(data)
      // Hydrate non-secret editable fields from server.
      setAdoOrg(String(data[K.ADO_ORG] ?? ''))
      setAdoProject(String(data[K.ADO_PROJECT] ?? ''))
      setAnthropicModel(String(data[K.ANTHROPIC_MODEL] ?? ''))
      setAzureEndpoint(String(data[K.AZURE_ENDPOINT] ?? ''))
      setAzureDeploy(String(data[K.AZURE_DEPLOY] ?? ''))
      setOpenaiBase(String(data[K.OPENAI_BASE_URL] ?? ''))
      setOpenaiModel(String(data[K.OPENAI_MODEL] ?? ''))
      setClaudeCliPath(String(data[K.CLAUDE_CLI_PATH] ?? 'claude'))
      setClaudeCliModel(String(data[K.CLAUDE_CLI_MODEL] ?? ''))
      setClaudeCliTimeout(String(data[K.CLAUDE_CLI_TIMEOUT] ?? '180'))
      setDefaultPlan(data[K.DEFAULT_PLAN_ID] ? Number(data[K.DEFAULT_PLAN_ID]) : '')
      setDefaultSuite(data[K.DEFAULT_SUITE_ID] ? Number(data[K.DEFAULT_SUITE_ID]) : '')
      const savedBoards = String(data[K.DEFAULT_BOARDS] ?? '')
      setBoardsList(savedBoards.split(/[;,]/).map(s => s.trim()).filter(Boolean))
      setSchedEnabled(String(data[K.SCHED_ENABLED] ?? 'true').toLowerCase() === 'true')
      setDefaultRange(String(data[K.DEFAULT_RANGE] ?? '7'))
    } catch (e) { setError(axiosMessage(e)) }
    finally { setLoading(false) }
  }, [])

  useEffect(() => { loadConfig() }, [loadConfig])

  // Claude CLI suggested-models list
  useEffect(() => {
    axios.get<{ suggestions: string[] }>('/api/settings/claude-cli/models')
      .then(r => setClaudeCliModels(r.data.suggestions ?? []))
      .catch(() => setClaudeCliModels([]))
  }, [])

  // Auto-fetch env once so cards (e.g. Claude CLI detected version) render without clicking Check.
  useEffect(() => {
    axios.get<EnvStatus>('/api/diagnostics').then(r => setEnv(r.data)).catch(() => {})
  }, [])

  // ADO discovery — orgs are best-effort (PAT often can't list them via vssps).
  useEffect(() => {
    axios.get<{ organizations: string[]; supported: boolean }>('/api/settings/ado/orgs')
      .then(r => setOrgs(r.data.organizations ?? []))
      .catch(() => setOrgs([]))
  }, [config[K.ADO_PAT + '.set']])

  // Projects auto-load when organization is set.
  useEffect(() => {
    if (!adoOrg) { setProjects([]); return }
    setProjectsLoading(true)
    axios.get<{ projects: AdoProject[] }>('/api/settings/ado/projects',
      { params: { organization: adoOrg } })
      .then(r => setProjects(r.data.projects ?? []))
      .catch(() => setProjects([]))
      .finally(() => setProjectsLoading(false))
  }, [adoOrg, config[K.ADO_PAT + '.set']])

  // Area paths auto-load when project is set.
  useEffect(() => {
    if (!adoOrg || !adoProject) { setAreaPaths([]); return }
    setAreasLoading(true)
    axios.get<{ areaPaths: string[] }>('/api/settings/ado/area-paths',
      { params: { organization: adoOrg, project: adoProject } })
      .then(r => setAreaPaths(r.data.areaPaths ?? []))
      .catch(() => setAreaPaths([]))
      .finally(() => setAreasLoading(false))
  }, [adoOrg, adoProject, config[K.ADO_PAT + '.set']])

  // Load plans (depends on a working ADO PAT, won't fail page if not set)
  useEffect(() => {
    axios.get<TestPlan[]>('/api/author-tests/plans')
      .then(r => setPlans(r.data))
      .catch(() => setPlans([]))
  }, [config[K.ADO_PAT + '.set']])

  // Load suites when default plan changes
  useEffect(() => {
    if (!defaultPlan) { setSuites([]); return }
    axios.get<TestSuite[]>(`/api/author-tests/plans/${defaultPlan}/suites`)
      .then(r => setSuites(r.data))
      .catch(() => setSuites([]))
  }, [defaultPlan])

  async function save(values: Record<string, string>) {
    try {
      const { data } = await axios.put<Config>('/api/settings/config', { values })
      setConfig(data)
      setToast('Saved')
      // Clear secret inputs after successful save so the masked placeholder shows again.
      ;([
        [K.ADO_PAT, setAdoPat],
        [K.ANTHROPIC_KEY, setAnthropicKey],
        [K.AZURE_KEY, setAzureKey],
        [K.OPENAI_KEY, setOpenaiKey],
      ] as const).forEach(([k, setter]) => { if (values[k]) setter('') })
    } catch (e) { setError(axiosMessage(e)) }
  }

  // ── diagnostics actions ────────────────────────────────────────────────
  async function checkEnv() {
    setEnvLoading(true)
    try {
      const { data } = await axios.get<EnvStatus>('/api/diagnostics')
      setEnv(data)
    } catch (e) { setError(axiosMessage(e)) }
    finally { setEnvLoading(false) }
  }
  async function readWorkItem() {
    setWiError(''); setWorkItem(null); setWiLoading(true)
    try {
      const { data } = await axios.get<WorkItem>(`/api/diagnostics/workitem/${wiId}`)
      setWorkItem(data)
    } catch (e) { setWiError(axiosMessage(e)) }
    finally { setWiLoading(false) }
  }
  async function pingLlm() {
    setLlmError(''); setLlmReply(''); setLlmLoading(true)
    try {
      const { data } = await axios.get<{ provider: string; reply: string }>('/api/diagnostics/llm')
      setLlmReply(`[${data.provider}] ${data.reply}`)
    } catch (e) { setLlmError(axiosMessage(e)) }
    finally { setLlmLoading(false) }
  }

  const StatusChip = ({ ok }: { ok: boolean }) =>
    ok ? <Chip icon={<CheckCircleIcon />} label="Present" color="success" size="small" />
       : <Chip icon={<ErrorIcon />} label="MISSING" color="error" size="small" />

  if (loading) return <CircularProgress />

  return (
    <Box>
      <Typography variant="h5" gutterBottom>Settings</Typography>
      <Stack spacing={3} sx={{ maxWidth: 820 }}>

        {/* ─── Azure DevOps ─────────────────────────────────────────────── */}
        <Card variant="outlined">
          <CardContent>
            <Typography variant="h6" gutterBottom>Azure DevOps</Typography>
            <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 2 }}>
              Save the PAT first — organization, project, and boards then populate from ADO.
            </Typography>
            <Stack spacing={2}>
              <SecretField label="Personal Access Token" value={adoPat} set={setAdoPat}
                isSet={Boolean(config[`${K.ADO_PAT}.set`])} />

              <Autocomplete
                freeSolo
                options={orgs}
                value={adoOrg}
                onChange={(_, v) => { setAdoOrg(v || ''); setAdoProject('') }}
                onInputChange={(_, v) => setAdoOrg(v)}
                renderInput={p => (
                  <TextField {...p} label="Organization" size="small"
                    helperText={orgs.length > 0
                      ? `${orgs.length} discoverable from PAT — or type any`
                      : 'PAT cannot list orgs via vssps — type your organization name'} />
                )}
              />

              <Autocomplete
                options={projects.map(p => p.name)}
                value={adoProject}
                onChange={(_, v) => setAdoProject(v || '')}
                loading={projectsLoading}
                disabled={!adoOrg}
                renderInput={p => (
                  <TextField {...p} label="Project" size="small"
                    helperText={!adoOrg ? 'Set organization first.'
                      : projectsLoading ? 'Loading projects…'
                      : `${projects.length} project(s) in ${adoOrg}`} />
                )}
              />

              <Stack direction="row" sx={{ justifyContent: 'flex-end' }}>
                <Button variant="contained" startIcon={<SaveIcon />}
                  onClick={() => save({
                    [K.ADO_ORG]: adoOrg,
                    [K.ADO_PROJECT]: adoProject,
                    [K.ADO_PAT]: adoPat,
                  })}>Save Azure DevOps</Button>
              </Stack>
            </Stack>
          </CardContent>
        </Card>

        {/* ─── LLM Providers ────────────────────────────────────────────── */}
        <Card variant="outlined">
          <CardContent>
            <Typography variant="h6" gutterBottom>LLM Providers</Typography>

            <Typography variant="subtitle2" sx={{ mt: 1 }}>Anthropic</Typography>
            <Stack spacing={1.5} sx={{ mt: 1 }}>
              <SecretField label="API key" value={anthropicKey} set={setAnthropicKey}
                isSet={Boolean(config[`${K.ANTHROPIC_KEY}.set`])} />
              <TextField label="Model" size="small" value={anthropicModel}
                onChange={e => setAnthropicModel(e.target.value)}
                placeholder="claude-sonnet-4-6" />
            </Stack>

            <Divider sx={{ my: 2 }} />
            <Typography variant="subtitle2">Azure OpenAI</Typography>
            <Stack spacing={1.5} sx={{ mt: 1 }}>
              <TextField label="Endpoint" size="small" value={azureEndpoint}
                onChange={e => setAzureEndpoint(e.target.value)}
                placeholder="https://your-resource.openai.azure.com" />
              <SecretField label="API key" value={azureKey} set={setAzureKey}
                isSet={Boolean(config[`${K.AZURE_KEY}.set`])} />
              <TextField label="Deployment" size="small" value={azureDeploy}
                onChange={e => setAzureDeploy(e.target.value)} />
            </Stack>

            <Divider sx={{ my: 2 }} />
            <Typography variant="subtitle2">
              OpenAI-compatible (GitHub Models / Groq / OpenAI / Ollama)
            </Typography>
            <Stack spacing={1.5} sx={{ mt: 1 }}>
              <TextField label="Base URL" size="small" value={openaiBase}
                onChange={e => setOpenaiBase(e.target.value)}
                placeholder="https://models.inference.ai.azure.com" />
              <SecretField label="API key" value={openaiKey} set={setOpenaiKey}
                isSet={Boolean(config[`${K.OPENAI_KEY}.set`])} />
              <TextField label="Model" size="small" value={openaiModel}
                onChange={e => setOpenaiModel(e.target.value)}
                placeholder="gpt-4o-mini" />
            </Stack>

            <Divider sx={{ my: 2 }} />
            <Typography variant="subtitle2">
              Claude CLI <Chip size="small" label="No API key" color="success" sx={{ ml: 1 }} />
            </Typography>
            <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 0.5 }}>
              Uses your local <code>claude</code> CLI login (Pro/Max). Single-user, slower than
              direct APIs (~2–5s per call), but no API key required.
              {env?.claudeCliAvailable
                ? <> Detected: <strong>{env.claudeCliVersion}</strong>.</>
                : <> CLI not detected on PATH — install Claude Code or set the path below.</>}
            </Typography>
            <Stack spacing={1.5} sx={{ mt: 1 }}>
              <TextField label="CLI path" size="small" value={claudeCliPath}
                onChange={e => setClaudeCliPath(e.target.value)}
                placeholder="claude"
                helperText="Bare 'claude' uses PATH lookup. Set an absolute path if not on PATH." />
              <Autocomplete<string, false, false, true>
                freeSolo
                options={claudeCliModels}
                value={claudeCliModel}
                onChange={(_, v) => setClaudeCliModel(v ?? '')}
                onInputChange={(_, v) => setClaudeCliModel(v)}
                renderInput={p => (
                  <TextField {...p} label="Model" size="small"
                    placeholder="(blank = CLI default)"
                    helperText="Aliases (sonnet/opus/haiku) auto-resolve to latest. Type a full version ID to pin." />
                )}
              />
              <TextField label="Timeout (seconds)" size="small" type="number" value={claudeCliTimeout}
                onChange={e => setClaudeCliTimeout(e.target.value)}
                helperText="How long to wait per CLI call before aborting. Min 10." />
            </Stack>

            <Stack direction="row" sx={{ mt: 2, justifyContent: 'flex-end' }}>
              <Button variant="contained" startIcon={<SaveIcon />}
                onClick={() => save({
                  [K.ANTHROPIC_KEY]: anthropicKey,
                  [K.ANTHROPIC_MODEL]: anthropicModel,
                  [K.AZURE_ENDPOINT]: azureEndpoint,
                  [K.AZURE_KEY]: azureKey,
                  [K.AZURE_DEPLOY]: azureDeploy,
                  [K.OPENAI_BASE_URL]: openaiBase,
                  [K.OPENAI_KEY]: openaiKey,
                  [K.OPENAI_MODEL]: openaiModel,
                  [K.CLAUDE_CLI_PATH]: claudeCliPath,
                  [K.CLAUDE_CLI_MODEL]: claudeCliModel,
                  [K.CLAUDE_CLI_TIMEOUT]: claudeCliTimeout,
                })}>Save LLM Providers</Button>
            </Stack>
          </CardContent>
        </Card>

        {/* ─── Author Tests defaults ────────────────────────────────────── */}
        <Card variant="outlined">
          <CardContent>
            <Typography variant="h6" gutterBottom>Author Tests — Defaults</Typography>
            <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 2 }}>
              Used as the pre-selected Test Plan + Suite on the Author Tests page push step.
            </Typography>
            <Stack spacing={2}>
              <FormControl size="small">
                <InputLabel>Default Test Plan</InputLabel>
                <Select label="Default Test Plan" value={defaultPlan}
                  onChange={e => { setDefaultPlan(e.target.value as number); setDefaultSuite('') }}>
                  <MenuItem value="">(none)</MenuItem>
                  {plans.map(p => (
                    <MenuItem key={p.id} value={p.id}>{p.name} <small style={{ marginLeft: 6, opacity: .7 }}>#{p.id}</small></MenuItem>
                  ))}
                </Select>
              </FormControl>
              <FormControl size="small" disabled={!defaultPlan}>
                <InputLabel>Default Test Suite</InputLabel>
                <Select label="Default Test Suite" value={defaultSuite}
                  onChange={e => setDefaultSuite(e.target.value as number)}>
                  <MenuItem value="">(none)</MenuItem>
                  {suites.map(s => (
                    <MenuItem key={s.id} value={s.id}>{s.name} <small style={{ marginLeft: 6, opacity: .7 }}>#{s.id}</small></MenuItem>
                  ))}
                </Select>
              </FormControl>
              <Stack direction="row" sx={{ justifyContent: 'flex-end' }}>
                <Button variant="contained" startIcon={<SaveIcon />}
                  onClick={() => save({
                    [K.DEFAULT_PLAN_ID]: defaultPlan === '' ? '' : String(defaultPlan),
                    [K.DEFAULT_SUITE_ID]: defaultSuite === '' ? '' : String(defaultSuite),
                  })}>Save Defaults</Button>
              </Stack>
            </Stack>
          </CardContent>
        </Card>

        {/* ─── Reports configuration ────────────────────────────────────── */}
        <Card variant="outlined">
          <CardContent>
            <Typography variant="h6" gutterBottom>Reports configuration</Typography>
            <Stack spacing={2}>
              <FormControlLabel
                control={<Switch checked={schedEnabled}
                  onChange={(_, v) => setSchedEnabled(v)} />}
                label="Schedule weekly report (Thursday 11:00, local timezone)" />
              <TextField label="Default range (days)" size="small" type="number" value={defaultRange}
                onChange={e => setDefaultRange(e.target.value)}
                helperText="Used by the weekly auto-run + the on-demand Reports page default." />
              <Autocomplete<string, true, false, true>
                multiple
                freeSolo
                options={areaPaths}
                value={boardsList}
                onChange={(_, v) => setBoardsList(v as string[])}
                loading={areasLoading}
                disabled={!adoOrg || !adoProject}
                renderInput={p => (
                  <TextField {...p} label="Boards / area paths" size="small"
                    helperText={
                      !adoOrg || !adoProject ? 'Set organization + project first.'
                      : areasLoading ? 'Loading area paths…'
                      : `Pick from ${areaPaths.length} discovered path(s). Empty = whole project.`
                    }
                  />
                )}
              />
              <Stack direction="row" sx={{ justifyContent: 'flex-end' }}>
                <Button variant="contained" startIcon={<SaveIcon />}
                  onClick={() => save({
                    [K.SCHED_ENABLED]: schedEnabled ? 'true' : 'false',
                    [K.DEFAULT_RANGE]: defaultRange,
                    [K.DEFAULT_BOARDS]: boardsList.join('; '),
                  })}>Save Reports config</Button>
              </Stack>
            </Stack>
          </CardContent>
        </Card>

        {/* ─── System diagnostics (existing) ────────────────────────────── */}
        <Card variant="outlined">
          <CardContent>
            <Typography variant="h6" gutterBottom>System diagnostics</Typography>
            <Stack spacing={2}>
              <Box>
                <Typography variant="subtitle2" gutterBottom>Environment</Typography>
                <Button variant="outlined" onClick={checkEnv} disabled={envLoading}>
                  {envLoading ? <CircularProgress size={18} /> : 'Check'}
                </Button>
                {env && (
                  <Stack direction="row" spacing={2} sx={{ mt: 2, flexWrap: 'wrap', rowGap: 1 }}>
                    <Box><Typography variant="body2">Active provider</Typography>
                      <Chip label={env.activeProvider} color="primary" size="small" /></Box>
                    <Box><Typography variant="body2">ADOACCESS_PAT</Typography>
                      <StatusChip ok={env.adoPatPresent} /></Box>
                    <Box><Typography variant="body2">ANTHROPIC_API_KEY</Typography>
                      <StatusChip ok={env.anthropicKeyPresent} /></Box>
                    <Box><Typography variant="body2">Azure OpenAI</Typography>
                      <StatusChip ok={env.azureOpenAiConfigured} /></Box>
                    <Box><Typography variant="body2">Claude CLI</Typography>
                      <StatusChip ok={env.claudeCliAvailable} /></Box>
                  </Stack>
                )}
              </Box>

              <Divider />

              <Box>
                <Typography variant="subtitle2" gutterBottom>Azure DevOps — Read Work Item</Typography>
                <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
                  <TextField label="Work item ID" size="small" value={wiId}
                    onChange={e => setWiId(e.target.value)} type="number" />
                  <Button variant="outlined" onClick={readWorkItem} disabled={wiLoading || !wiId}>
                    {wiLoading ? <CircularProgress size={18} /> : 'Read'}
                  </Button>
                </Stack>
                {wiError && <Typography color="error" sx={{ mt: 1 }}>{wiError}</Typography>}
                {workItem && (
                  <Box sx={{ mt: 1.5 }}>
                    <Typography variant="body2"><strong>{workItem.workItemType}</strong> · {workItem.state}</Typography>
                    <Typography variant="body2">{workItem.title}</Typography>
                    {workItem.assignedTo && <Typography variant="caption" color="text.secondary">{workItem.assignedTo}</Typography>}
                  </Box>
                )}
              </Box>

              <Divider />

              <Box>
                <Typography variant="subtitle2" gutterBottom>LLM ping (active provider)</Typography>
                <Button variant="outlined" onClick={pingLlm} disabled={llmLoading}>
                  {llmLoading ? <CircularProgress size={18} /> : 'Ping'}
                </Button>
                {llmError && <Typography color="error" sx={{ mt: 1 }}>{llmError}</Typography>}
                {llmReply && <Typography sx={{ mt: 1 }}>Reply: <strong>{llmReply}</strong></Typography>}
              </Box>
            </Stack>
          </CardContent>
        </Card>

      </Stack>

      <Snackbar open={!!toast} autoHideDuration={3000} onClose={() => setToast('')}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'right' }}>
        <Alert severity="success" variant="filled" onClose={() => setToast('')}>{toast}</Alert>
      </Snackbar>
      <Snackbar open={!!error} autoHideDuration={6000} onClose={() => setError('')}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'right' }}>
        <Alert severity="error" variant="filled" onClose={() => setError('')}>{error}</Alert>
      </Snackbar>
    </Box>
  )
}
