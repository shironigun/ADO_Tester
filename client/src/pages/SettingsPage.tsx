/**
 * Settings — modern admin console.
 *
 * Vertical tab nav (6 tabs) on the left, animated content on the right. On
 * narrow screens (<900px) the tabs flip to a scrollable bar across the top.
 * All save logic + API calls are preserved verbatim — only the render tree and
 * styling changed (now driven by the shared theme + components, no local serif).
 */
import { useCallback, useEffect, useState, type ReactNode } from 'react'
import {
  Alert, Autocomplete, Box, Button, Card, CardContent, Chip, CircularProgress,
  FormControl, FormControlLabel, IconButton, InputAdornment, InputLabel,
  MenuItem, Select, Snackbar, Stack, Switch, Tab, Tabs, TextField, Tooltip, Typography,
  useMediaQuery, useTheme,
} from '@mui/material'
import { AnimatePresence } from 'framer-motion'
import CheckCircleIcon from '@mui/icons-material/CheckCircle'
import ErrorIcon from '@mui/icons-material/Error'
import VisibilityIcon from '@mui/icons-material/Visibility'
import VisibilityOffIcon from '@mui/icons-material/VisibilityOff'
import SaveIcon from '@mui/icons-material/Save'
import RefreshIcon from '@mui/icons-material/Refresh'
import DashboardOutlinedIcon from '@mui/icons-material/DashboardOutlined'
import CloudOutlinedIcon from '@mui/icons-material/CloudOutlined'
import SmartToyOutlinedIcon from '@mui/icons-material/SmartToyOutlined'
import RuleOutlinedIcon from '@mui/icons-material/RuleOutlined'
import AssessmentOutlinedIcon from '@mui/icons-material/AssessmentOutlined'
import BugReportOutlinedIcon from '@mui/icons-material/BugReportOutlined'
import HealthAndSafetyOutlinedIcon from '@mui/icons-material/HealthAndSafetyOutlined'
import RadioButtonCheckedIcon from '@mui/icons-material/RadioButtonChecked'
import axios from 'axios'
import PageHeader from '../components/PageHeader'
import Section from '../components/Section'
import { FadeIn, Stagger, StaggerItem } from '../motion/primitives'
import { MONO_FONT } from '../theme/tokens'

// ── Type system + setting keys ─────────────────────────────────────────────
const K = {
  ADO_ORG: 'ado.organization',
  ADO_PROJECT: 'ado.project',
  ADO_PAT: 'ado.pat',
  ADO_REPO: 'ado.repository',
  ADO_BRANCH: 'ado.default_branch',
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
  REPORT_TEAM_NAME: 'reports.team_name',
  REPORT_TYPES: 'reports.ticket_types',
  VERIFY_DETAIL: 'find_bugs.verify_detail',
  VERIFY_STEP_SCOPE: 'find_bugs.verify_step_scope',
  BUG_FROM_CASE_MODE: 'find_bugs.bug_from_case_mode',
}

const REPORT_TYPE_OPTIONS = ['User Story', 'Bug', 'Defect', 'Task', 'Issue', 'Feature']

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
  id: number; title: string; workItemType: string; state: string; assignedTo?: string
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

// ── Reusable parts ─────────────────────────────────────────────────────────

/** Section heading with a small mono "eyebrow" label — console feel. */
function SectionHeader({
  eyebrow, title, subtitle,
}: { eyebrow: string; title: string; subtitle?: string }) {
  return (
    <Box sx={{ mb: 3 }}>
      <Typography variant="overline" sx={{ color: 'text.secondary', display: 'block' }}>{eyebrow}</Typography>
      <Typography variant="h4" sx={{ lineHeight: 1.1, mb: subtitle ? 1 : 0 }}>{title}</Typography>
      {subtitle && (
        <Typography variant="body2" color="text.secondary" sx={{ maxWidth: 760 }}>
          {subtitle}
        </Typography>
      )}
    </Box>
  )
}

/** Subsection card — thin wrapper over the shared Section. */
function Panel({
  title, hint, children, action,
}: { title?: string; hint?: string; children: ReactNode; action?: ReactNode }) {
  return (
    <Section title={title} subtitle={hint} action={action} dense={!!hint} padding={3}>
      {children}
    </Section>
  )
}

/** mono "key" label rendered above a control — like a code identifier. */
function FieldKey({ k }: { k: string }) {
  return (
    <Typography sx={{
      fontFamily: MONO_FONT, fontSize: 10.5, letterSpacing: '0.08em',
      color: 'text.secondary', textTransform: 'lowercase', mb: 0.6,
    }}>{k}</Typography>
  )
}

/** Save button — anchored to the right. */
function SaveBar({ onClick, label = 'Save changes' }: { onClick: () => void; label?: string }) {
  return (
    <Stack direction="row" sx={{ justifyContent: 'flex-end', mt: 3 }}>
      <Button variant="contained" startIcon={<SaveIcon />} onClick={onClick}>
        {label}
      </Button>
    </Stack>
  )
}

/** Status pill — used by the top status strip + diagnostics. */
function StatusPill({ ok, label, value }: { ok: boolean; label: string; value?: string }) {
  return (
    <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
      {ok
        ? <CheckCircleIcon fontSize="small" sx={{ color: 'success.main' }} />
        : <ErrorIcon fontSize="small" sx={{ color: 'error.main' }} />}
      <Box>
        <Typography sx={{
          fontFamily: MONO_FONT, fontSize: 10, letterSpacing: '0.12em',
          color: 'text.secondary', textTransform: 'uppercase',
        }}>{label}</Typography>
        <Typography variant="body2" sx={{
          fontWeight: 600, color: ok ? 'success.main' : 'error.main',
        }}>{value ?? (ok ? 'Configured' : 'Missing')}</Typography>
      </Box>
    </Stack>
  )
}

/** Secret-aware text input. Backend never echoes the value — only a `.set` flag. */
function SecretField({ label, value, set, isSet, keyName }: {
  label: string; value: string; set: (v: string) => void; isSet: boolean; keyName?: string
}) {
  const [shown, setShown] = useState(false)
  return (
    <Box>
      {keyName && <FieldKey k={keyName} />}
      <TextField
        label={isSet ? `${label} — Configured` : label}
        size="small" fullWidth
        type={shown ? 'text' : 'password'}
        value={value}
        onChange={e => set(e.target.value)}
        placeholder={isSet ? 'Configured — leave blank to keep current value' : 'Not set'}
        helperText={isSet
          ? 'A value is stored. Type a new one to replace, or leave blank to keep it.'
          : 'Not configured yet.'}
        slotProps={{
          input: {
            startAdornment: isSet ? (
              <InputAdornment position="start">
                <Chip size="small" label="set" color="success" sx={{ height: 20, fontSize: 11 }} />
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
    </Box>
  )
}

/** LLM provider card for the LLM Providers tab. */
function ProviderCard({
  name, blurb, active, ready, children,
}: { name: string; blurb: string; active: boolean; ready: boolean; children: ReactNode }) {
  return (
    <Card sx={{
      height: '100%',
      borderColor: active ? 'primary.main' : 'divider',
      borderWidth: active ? 2 : 1,
      position: 'relative',
      transition: 'all 200ms ease',
      ...(active && {
        boxShadow: theme => `0 0 0 4px ${theme.palette.primary.main}14`,
      }),
    }}>
      <CardContent sx={{ p: 2.5 }}>
        <Stack direction="row" sx={{ alignItems: 'flex-start', mb: 1.5 }}>
          <Box sx={{ flex: 1 }}>
            <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 0.3 }}>
              <Typography variant="h6">{name}</Typography>
              {active && (
                <Chip size="small" label="active" color="primary"
                  icon={<RadioButtonCheckedIcon sx={{ fontSize: 12 }} />}
                  sx={{ height: 20, fontSize: 10.5, fontFamily: MONO_FONT, letterSpacing: '0.08em' }} />
              )}
              {!active && ready && (
                <Chip size="small" label="ready" variant="outlined" color="success"
                  sx={{ height: 20, fontSize: 10.5, fontFamily: MONO_FONT, letterSpacing: '0.08em' }} />
              )}
            </Stack>
            <Typography variant="caption" color="text.secondary" sx={{ lineHeight: 1.45 }}>
              {blurb}
            </Typography>
          </Box>
        </Stack>
        <Stack spacing={1.5}>{children}</Stack>
      </CardContent>
    </Card>
  )
}

// ── Main component ─────────────────────────────────────────────────────────

const TABS = [
  { key: 'overview',   label: 'Overview',     icon: <DashboardOutlinedIcon fontSize="small" /> },
  { key: 'ado',        label: 'Azure DevOps', icon: <CloudOutlinedIcon fontSize="small" /> },
  { key: 'llm',        label: 'LLM Providers', icon: <SmartToyOutlinedIcon fontSize="small" /> },
  { key: 'tests',      label: 'Author Tests', icon: <RuleOutlinedIcon fontSize="small" /> },
  { key: 'findbugs',   label: 'Find Bugs',    icon: <BugReportOutlinedIcon fontSize="small" /> },
  { key: 'reports',    label: 'Reports',      icon: <AssessmentOutlinedIcon fontSize="small" /> },
  { key: 'diag',       label: 'Diagnostics',  icon: <HealthAndSafetyOutlinedIcon fontSize="small" /> },
] as const
type TabKey = typeof TABS[number]['key']

export default function SettingsPage() {
  const theme = useTheme()
  const isNarrow = useMediaQuery(theme.breakpoints.down('md'))

  const [tab, setTab] = useState<TabKey>('overview')

  const [config, setConfig] = useState<Config>({})
  const [loading, setLoading] = useState(true)
  const [toast, setToast] = useState('')
  const [error, setError] = useState('')

  // Per-section staged edits (start empty for secrets, current for non-secrets)
  const [adoOrg, setAdoOrg] = useState('')
  const [adoProject, setAdoProject] = useState('')
  const [adoRepo, setAdoRepo] = useState('')
  const [adoBranch, setAdoBranch] = useState('')
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
  const [schedEnabled, setSchedEnabled] = useState(true)
  const [defaultRange, setDefaultRange] = useState('7')
  const [teamName, setTeamName] = useState('')
  const [typesList, setTypesList] = useState<string[]>([])
  const [previewTheme, setPreviewTheme] = useState('Executive')
  const [verifyDetail, setVerifyDetail] = useState('step')
  const [verifyScope, setVerifyScope] = useState('fails_only')
  const [bugFromMode, setBugFromMode] = useState('instant')

  const [plans, setPlans] = useState<TestPlan[]>([])
  const [suites, setSuites] = useState<TestSuite[]>([])

  const [orgs, setOrgs] = useState<string[]>([])
  const [projects, setProjects] = useState<AdoProject[]>([])
  const [projectsLoading, setProjectsLoading] = useState(false)
  const [areaPaths, setAreaPaths] = useState<string[]>([])
  const [areasLoading, setAreasLoading] = useState(false)
  const [boardsList, setBoardsList] = useState<string[]>([])

  // Diagnostics
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
      setAdoOrg(String(data[K.ADO_ORG] ?? ''))
      setAdoProject(String(data[K.ADO_PROJECT] ?? ''))
      setAdoRepo(String(data[K.ADO_REPO] ?? ''))
      setAdoBranch(String(data[K.ADO_BRANCH] ?? ''))
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
      setTeamName(String(data[K.REPORT_TEAM_NAME] ?? ''))
      setTypesList(String(data[K.REPORT_TYPES] ?? '').split(';').map(s => s.trim()).filter(Boolean))
      setVerifyDetail(String(data[K.VERIFY_DETAIL] ?? 'step'))
      setVerifyScope(String(data[K.VERIFY_STEP_SCOPE] ?? 'fails_only'))
      setBugFromMode(String(data[K.BUG_FROM_CASE_MODE] ?? 'instant'))
    } catch (e) { setError(axiosMessage(e)) }
    finally { setLoading(false) }
  }, [])

  useEffect(() => { loadConfig() }, [loadConfig])

  useEffect(() => {
    axios.get<{ suggestions: string[] }>('/api/settings/claude-cli/models')
      .then(r => setClaudeCliModels(r.data.suggestions ?? []))
      .catch(() => setClaudeCliModels([]))
  }, [])

  // Auto-fetch env so the status strip + overview cards render without clicking "Check".
  useEffect(() => {
    axios.get<EnvStatus>('/api/diagnostics').then(r => setEnv(r.data)).catch(() => {})
  }, [config[`${K.ADO_PAT}.set`], config[`${K.ANTHROPIC_KEY}.set`]])

  useEffect(() => {
    axios.get<{ organizations: string[] }>('/api/settings/ado/orgs')
      .then(r => setOrgs(r.data.organizations ?? []))
      .catch(() => setOrgs([]))
  }, [config[`${K.ADO_PAT}.set`]])

  useEffect(() => {
    if (!adoOrg) { setProjects([]); return }
    setProjectsLoading(true)
    axios.get<{ projects: AdoProject[] }>('/api/settings/ado/projects',
      { params: { organization: adoOrg } })
      .then(r => setProjects(r.data.projects ?? []))
      .catch(() => setProjects([]))
      .finally(() => setProjectsLoading(false))
  }, [adoOrg, config[`${K.ADO_PAT}.set`]])

  useEffect(() => {
    if (!adoOrg || !adoProject) { setAreaPaths([]); return }
    setAreasLoading(true)
    axios.get<{ areaPaths: string[] }>('/api/settings/ado/area-paths',
      { params: { organization: adoOrg, project: adoProject } })
      .then(r => setAreaPaths(r.data.areaPaths ?? []))
      .catch(() => setAreaPaths([]))
      .finally(() => setAreasLoading(false))
  }, [adoOrg, adoProject, config[`${K.ADO_PAT}.set`]])

  useEffect(() => {
    axios.get<TestPlan[]>('/api/author-tests/plans')
      .then(r => setPlans(r.data))
      .catch(() => setPlans([]))
  }, [config[`${K.ADO_PAT}.set`]])

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
      ;([
        [K.ADO_PAT, setAdoPat],
        [K.ANTHROPIC_KEY, setAnthropicKey],
        [K.AZURE_KEY, setAzureKey],
        [K.OPENAI_KEY, setOpenaiKey],
      ] as const).forEach(([k, setter]) => { if (values[k]) setter('') })
    } catch (e) { setError(axiosMessage(e)) }
  }

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

  if (loading) {
    return (
      <Box sx={{ display: 'flex', justifyContent: 'center', py: 8 }}>
        <CircularProgress />
      </Box>
    )
  }

  const activeProvider = env?.activeProvider ?? ''

  // ── Page header with status pills ────────────────────────────────────────
  const Header = (
    <PageHeader
      title="Settings"
      subtitle="Connections, providers, defaults, and live checks for the QA Automation workspace."
      actions={
        <Stack direction="row" spacing={3} sx={{ alignItems: 'center' }}>
          <StatusPill ok={!!activeProvider} label="Active LLM" value={activeProvider || 'unset'} />
          <StatusPill ok={!!env?.adoPatPresent} label="ADO PAT" />
          <StatusPill ok={!!env?.claudeCliAvailable}
            label="Claude CLI" value={env?.claudeCliVersion ?? (env?.claudeCliAvailable ? 'ok' : 'missing')} />
          <Tooltip title="Refresh diagnostics">
            <IconButton onClick={checkEnv} disabled={envLoading} size="small">
              {envLoading ? <CircularProgress size={16} /> : <RefreshIcon fontSize="small" />}
            </IconButton>
          </Tooltip>
        </Stack>
      }
    />
  )

  // ── Tab navigation ───────────────────────────────────────────────────────
  const TabBar = (
    <Tabs
      orientation={isNarrow ? 'horizontal' : 'vertical'}
      variant={isNarrow ? 'scrollable' : 'standard'}
      value={tab}
      onChange={(_, v: TabKey) => setTab(v)}
      sx={{
        minWidth: isNarrow ? '100%' : 224,
        borderRight: isNarrow ? 0 : 1,
        borderBottom: isNarrow ? 1 : 0,
        borderColor: 'divider',
        '& .MuiTab-root': {
          fontSize: 13.5, fontWeight: 600,
          textTransform: 'none', alignItems: isNarrow ? 'center' : 'flex-start',
          textAlign: 'left', minHeight: 46, gap: 1, pl: isNarrow ? 2 : 2.5, pr: 2,
          borderRadius: 2,
        },
        '& .MuiTab-iconWrapper': { mr: 1, mb: '0 !important' },
      }}
    >
      {TABS.map(t => (
        <Tab key={t.key} value={t.key} icon={t.icon} iconPosition="start" label={t.label} />
      ))}
    </Tabs>
  )

  // ── Tab panels ───────────────────────────────────────────────────────────
  return (
    <Box sx={{ pb: 6 }}>
      {Header}

      <Stack direction={isNarrow ? 'column' : 'row'} spacing={isNarrow ? 0 : 4}
        sx={{ alignItems: 'stretch' }}>
        {TabBar}

        <Box sx={{ flex: 1, minWidth: 0, pt: isNarrow ? 3 : 0, pl: isNarrow ? 0 : 1 }}>
          <AnimatePresence mode="wait">
            <FadeIn key={tab} y={10}>

              {/* ── OVERVIEW ────────────────────────────────────────────────── */}
              {tab === 'overview' && (
                <Box>
                  <SectionHeader eyebrow="01 · Overview" title="System at a glance"
                    subtitle="A snapshot of what's configured. Use the tabs to drill in." />
                  <Stagger>
                    <Box sx={{
                      display: 'grid',
                      gridTemplateColumns: { xs: '1fr', md: 'repeat(2, 1fr)' },
                      gap: 2,
                    }}>
                      <StaggerItem>
                        <Panel title="Azure DevOps"
                          hint={config[`${K.ADO_PAT}.set`]
                            ? `Connected · ${adoOrg || '—'} / ${adoProject || '—'}`
                            : 'PAT not configured.'}>
                          <Stack direction="row" spacing={2} sx={{ alignItems: 'center' }}>
                            <StatusPill ok={!!env?.adoPatPresent} label="PAT" />
                            <Button size="small" onClick={() => setTab('ado')}>Configure</Button>
                          </Stack>
                        </Panel>
                      </StaggerItem>

                      <StaggerItem>
                        <Panel title="LLM Providers"
                          hint={activeProvider ? `Active: ${activeProvider}` : 'No provider selected.'}>
                          <Stack direction="row" spacing={2} sx={{ alignItems: 'center', flexWrap: 'wrap', gap: 1 }}>
                            <Chip size="small" variant={env?.anthropicKeyPresent ? 'filled' : 'outlined'}
                              label="Anthropic" color={env?.anthropicKeyPresent ? 'primary' : 'default'} />
                            <Chip size="small" variant={env?.azureOpenAiConfigured ? 'filled' : 'outlined'}
                              label="Azure OpenAI" color={env?.azureOpenAiConfigured ? 'primary' : 'default'} />
                            <Chip size="small" variant={config[`${K.OPENAI_KEY}.set`] ? 'filled' : 'outlined'}
                              label="OpenAI-compat" color={config[`${K.OPENAI_KEY}.set`] ? 'primary' : 'default'} />
                            <Chip size="small" variant={env?.claudeCliAvailable ? 'filled' : 'outlined'}
                              label="Claude CLI" color={env?.claudeCliAvailable ? 'primary' : 'default'} />
                          </Stack>
                          <Button size="small" sx={{ mt: 1.5 }} onClick={() => setTab('llm')}>Manage providers</Button>
                        </Panel>
                      </StaggerItem>

                      <StaggerItem>
                        <Panel title="Author Tests" hint="Default Test Plan + Suite used on push.">
                          <Typography variant="body2" sx={{ fontFamily: MONO_FONT, fontSize: 12 }}>
                            plan: {defaultPlan || '—'}<br />suite: {defaultSuite || '—'}
                          </Typography>
                          <Button size="small" sx={{ mt: 1 }} onClick={() => setTab('tests')}>Set defaults</Button>
                        </Panel>
                      </StaggerItem>

                      <StaggerItem>
                        <Panel title="Reports" hint="Schedule + default range + boards filter.">
                          <Typography variant="body2" sx={{ fontFamily: MONO_FONT, fontSize: 12 }}>
                            schedule: {schedEnabled ? 'on (Thu 11:00)' : 'off'}<br />
                            range: {defaultRange} days · boards: {boardsList.length || 'all'}
                          </Typography>
                          <Button size="small" sx={{ mt: 1 }} onClick={() => setTab('reports')}>Configure</Button>
                        </Panel>
                      </StaggerItem>
                    </Box>
                  </Stagger>
                </Box>
              )}

              {/* ── AZURE DEVOPS ────────────────────────────────────────────── */}
              {tab === 'ado' && (
                <Box>
                  <SectionHeader eyebrow="02 · Azure DevOps" title="Azure DevOps connection"
                    subtitle="Paste the PAT first — once it's saved, organization and project drop-downs populate from ADO." />
                  <Panel>
                    <Stack spacing={2.5}>
                      <SecretField label="Personal Access Token" value={adoPat} set={setAdoPat}
                        isSet={Boolean(config[`${K.ADO_PAT}.set`])}
                        keyName={K.ADO_PAT} />
                      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
                        <Box sx={{ flex: 1 }}>
                          <FieldKey k={K.ADO_ORG} />
                          <Autocomplete
                            freeSolo options={orgs} value={adoOrg}
                            onChange={(_, v) => { setAdoOrg(v || ''); setAdoProject('') }}
                            onInputChange={(_, v) => setAdoOrg(v)}
                            renderInput={p => (
                              <TextField {...p} label="Organization" size="small"
                                helperText={orgs.length > 0
                                  ? `${orgs.length} discoverable from PAT — or type any`
                                  : 'PAT cannot list orgs via vssps — type the org name manually'} />
                            )}
                          />
                        </Box>
                        <Box sx={{ flex: 1 }}>
                          <FieldKey k={K.ADO_PROJECT} />
                          <Autocomplete
                            options={projects.map(p => p.name)} value={adoProject}
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
                        </Box>
                      </Stack>
                      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
                        <Box sx={{ flex: 1 }}>
                          <FieldKey k={K.ADO_REPO} />
                          <TextField label="Git repository" size="small" fullWidth value={adoRepo}
                            onChange={e => setAdoRepo(e.target.value)} placeholder="Your Repo"
                            helperText="Repo the code-analysis (verify-against-code) phase reads." />
                        </Box>
                        <Box sx={{ flex: 1 }}>
                          <FieldKey k={K.ADO_BRANCH} />
                          <TextField label="Default branch" size="small" fullWidth value={adoBranch}
                            onChange={e => setAdoBranch(e.target.value)} placeholder="main" />
                        </Box>
                      </Stack>
                    </Stack>
                    <SaveBar onClick={() => save({
                      [K.ADO_ORG]: adoOrg,
                      [K.ADO_PROJECT]: adoProject,
                      [K.ADO_PAT]: adoPat,
                      [K.ADO_REPO]: adoRepo,
                      [K.ADO_BRANCH]: adoBranch,
                    })} />
                  </Panel>
                </Box>
              )}

              {/* ── LLM PROVIDERS ───────────────────────────────────────────── */}
              {tab === 'llm' && (
                <Box>
                  <SectionHeader eyebrow="03 · LLM Providers"
                    title="Language model providers"
                    subtitle="Each agent run uses the active provider — switch any time from the AppBar dropdown. Keys are write-only; the backend never sends them back to the UI." />

                  <Stagger>
                    <Box sx={{
                      display: 'grid',
                      gridTemplateColumns: { xs: '1fr', md: '1fr 1fr' },
                      gap: 2,
                    }}>
                      <StaggerItem>
                        <ProviderCard
                          name="Anthropic"
                          blurb="claude.ai API. Fastest direct path."
                          active={activeProvider === 'Anthropic'}
                          ready={!!env?.anthropicKeyPresent}>
                          <SecretField label="API key" value={anthropicKey} set={setAnthropicKey}
                            isSet={Boolean(config[`${K.ANTHROPIC_KEY}.set`])} keyName={K.ANTHROPIC_KEY} />
                          <Box>
                            <FieldKey k={K.ANTHROPIC_MODEL} />
                            <TextField label="Model" size="small" fullWidth value={anthropicModel}
                              onChange={e => setAnthropicModel(e.target.value)}
                              placeholder="claude-sonnet-4-6" />
                          </Box>
                          <Button size="small" variant="text" sx={{ alignSelf: 'flex-end' }}
                            onClick={() => save({
                              [K.ANTHROPIC_KEY]: anthropicKey,
                              [K.ANTHROPIC_MODEL]: anthropicModel,
                            })}>Save Anthropic</Button>
                        </ProviderCard>
                      </StaggerItem>

                      <StaggerItem>
                        <ProviderCard
                          name="Azure OpenAI"
                          blurb="Azure-native AI for procurement-restricted shops."
                          active={activeProvider === 'AzureOpenAI'}
                          ready={!!env?.azureOpenAiConfigured}>
                          <Box>
                            <FieldKey k={K.AZURE_ENDPOINT} />
                            <TextField label="Endpoint" size="small" fullWidth value={azureEndpoint}
                              onChange={e => setAzureEndpoint(e.target.value)}
                              placeholder="https://your-resource.openai.azure.com" />
                          </Box>
                          <SecretField label="API key" value={azureKey} set={setAzureKey}
                            isSet={Boolean(config[`${K.AZURE_KEY}.set`])} keyName={K.AZURE_KEY} />
                          <Box>
                            <FieldKey k={K.AZURE_DEPLOY} />
                            <TextField label="Deployment" size="small" fullWidth value={azureDeploy}
                              onChange={e => setAzureDeploy(e.target.value)} />
                          </Box>
                          <Button size="small" variant="text" sx={{ alignSelf: 'flex-end' }}
                            onClick={() => save({
                              [K.AZURE_ENDPOINT]: azureEndpoint,
                              [K.AZURE_KEY]: azureKey,
                              [K.AZURE_DEPLOY]: azureDeploy,
                            })}>Save Azure</Button>
                        </ProviderCard>
                      </StaggerItem>

                      <StaggerItem>
                        <ProviderCard
                          name="OpenAI-compatible"
                          blurb="GitHub Models (free) · Groq · OpenAI · Ollama."
                          active={activeProvider === 'OpenAICompatible'}
                          ready={!!config[`${K.OPENAI_KEY}.set`]}>
                          <Box>
                            <FieldKey k={K.OPENAI_BASE_URL} />
                            <TextField label="Base URL" size="small" fullWidth value={openaiBase}
                              onChange={e => setOpenaiBase(e.target.value)}
                              placeholder="https://models.inference.ai.azure.com" />
                          </Box>
                          <SecretField label="API key" value={openaiKey} set={setOpenaiKey}
                            isSet={Boolean(config[`${K.OPENAI_KEY}.set`])} keyName={K.OPENAI_KEY} />
                          <Box>
                            <FieldKey k={K.OPENAI_MODEL} />
                            <TextField label="Model" size="small" fullWidth value={openaiModel}
                              onChange={e => setOpenaiModel(e.target.value)}
                              placeholder="gpt-4o-mini" />
                          </Box>
                          <Button size="small" variant="text" sx={{ alignSelf: 'flex-end' }}
                            onClick={() => save({
                              [K.OPENAI_BASE_URL]: openaiBase,
                              [K.OPENAI_KEY]: openaiKey,
                              [K.OPENAI_MODEL]: openaiModel,
                            })}>Save OpenAI-compat</Button>
                        </ProviderCard>
                      </StaggerItem>

                      <StaggerItem>
                        <ProviderCard
                          name="Claude CLI"
                          blurb={env?.claudeCliAvailable
                            ? `Local CLI · ${env.claudeCliVersion}. Uses your Pro/Max login.`
                            : 'Local `claude` CLI not detected — install Claude Code and log in.'}
                          active={activeProvider === 'ClaudeCli'}
                          ready={!!env?.claudeCliAvailable}>
                          <Box>
                            <FieldKey k={K.CLAUDE_CLI_PATH} />
                            <TextField label="CLI path" size="small" fullWidth value={claudeCliPath}
                              onChange={e => setClaudeCliPath(e.target.value)}
                              placeholder="claude" />
                          </Box>
                          <Box>
                            <FieldKey k={K.CLAUDE_CLI_MODEL} />
                            <Autocomplete
                              freeSolo options={claudeCliModels} value={claudeCliModel}
                              onChange={(_, v) => setClaudeCliModel(v || '')}
                              onInputChange={(_, v) => setClaudeCliModel(v)}
                              renderInput={p => (
                                <TextField {...p} label="Model" size="small"
                                  placeholder="(blank = CLI default)" />
                              )}
                            />
                          </Box>
                          <Box>
                            <FieldKey k={K.CLAUDE_CLI_TIMEOUT} />
                            <TextField label="Timeout (seconds)" size="small" type="number"
                              fullWidth value={claudeCliTimeout}
                              onChange={e => setClaudeCliTimeout(e.target.value)} />
                          </Box>
                          <Button size="small" variant="text" sx={{ alignSelf: 'flex-end' }}
                            onClick={() => save({
                              [K.CLAUDE_CLI_PATH]: claudeCliPath,
                              [K.CLAUDE_CLI_MODEL]: claudeCliModel,
                              [K.CLAUDE_CLI_TIMEOUT]: claudeCliTimeout,
                            })}>Save Claude CLI</Button>
                        </ProviderCard>
                      </StaggerItem>
                    </Box>
                  </Stagger>
                </Box>
              )}

              {/* ── AUTHOR TESTS ────────────────────────────────────────────── */}
              {tab === 'tests' && (
                <Box>
                  <SectionHeader eyebrow="04 · Author Tests"
                    title="Author-tests defaults"
                    subtitle="The Test Plan + Suite picked here are pre-selected on the Author Tests push step." />
                  <Panel>
                    <Stack direction={{ xs: 'column', md: 'row' }} spacing={2}>
                      <Box sx={{ flex: 1 }}>
                        <FieldKey k={K.DEFAULT_PLAN_ID} />
                        <FormControl size="small" fullWidth>
                          <InputLabel>Default Test Plan</InputLabel>
                          <Select label="Default Test Plan" value={defaultPlan}
                            onChange={e => { setDefaultPlan(e.target.value as number); setDefaultSuite('') }}>
                            <MenuItem value="">(none)</MenuItem>
                            {plans.map(p => (
                              <MenuItem key={p.id} value={p.id}>
                                {p.name}
                                <Box component="span" sx={{
                                  ml: 1, fontFamily: MONO_FONT, fontSize: 11, color: 'text.secondary',
                                }}>#{p.id}</Box>
                              </MenuItem>
                            ))}
                          </Select>
                        </FormControl>
                      </Box>
                      <Box sx={{ flex: 1 }}>
                        <FieldKey k={K.DEFAULT_SUITE_ID} />
                        <FormControl size="small" fullWidth disabled={!defaultPlan}>
                          <InputLabel>Default Test Suite</InputLabel>
                          <Select label="Default Test Suite" value={defaultSuite}
                            onChange={e => setDefaultSuite(e.target.value as number)}>
                            <MenuItem value="">(none)</MenuItem>
                            {suites.map(s => (
                              <MenuItem key={s.id} value={s.id}>
                                {s.name}
                                <Box component="span" sx={{
                                  ml: 1, fontFamily: MONO_FONT, fontSize: 11, color: 'text.secondary',
                                }}>#{s.id}</Box>
                              </MenuItem>
                            ))}
                          </Select>
                        </FormControl>
                      </Box>
                    </Stack>
                    <SaveBar onClick={() => save({
                      [K.DEFAULT_PLAN_ID]: defaultPlan === '' ? '' : String(defaultPlan),
                      [K.DEFAULT_SUITE_ID]: defaultSuite === '' ? '' : String(defaultSuite),
                    })} />
                  </Panel>
                </Box>
              )}

              {/* ── FIND BUGS ───────────────────────────────────────────────── */}
              {tab === 'findbugs' && (
                <Box>
                  <SectionHeader eyebrow="05 · Find Bugs"
                    title="Code verification report"
                    subtitle="How deeply the Find Bugs code-verification analyzes each test case, and how the “Create bug” action on a failed case builds its draft." />
                  <Panel>
                    <Stack spacing={3}>
                      <Box>
                        <FieldKey k={K.VERIFY_DETAIL} />
                        <FormControl size="small" sx={{ minWidth: 340 }}>
                          <InputLabel>Step detail</InputLabel>
                          <Select label="Step detail" value={verifyDetail}
                            onChange={e => setVerifyDetail(e.target.value)}>
                            <MenuItem value="case">Case-level only — one verdict per test case (fastest)</MenuItem>
                            <MenuItem value="step">Per-step pass / fail (recommended)</MenuItem>
                            <MenuItem value="step_full">Per-step + what the code does at each step (most detail)</MenuItem>
                          </Select>
                        </FormControl>
                        <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 0.5 }}>
                          Step detail shows a green tick / red ✗ on each step and pinpoints the failing step — at the cost of more LLM tokens.
                        </Typography>
                      </Box>
                      <Box>
                        <FieldKey k={K.VERIFY_STEP_SCOPE} />
                        <FormControl size="small" sx={{ minWidth: 340 }}>
                          <InputLabel>Per-step coverage</InputLabel>
                          <Select label="Per-step coverage" value={verifyScope}
                            onChange={e => setVerifyScope(e.target.value)}>
                            <MenuItem value="fails_only">Failing &amp; inconclusive cases only (recommended)</MenuItem>
                            <MenuItem value="all">Every test case</MenuItem>
                          </Select>
                        </FormControl>
                        <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 0.5 }}>
                          Which cases get the per-step drill-down. Applies only when step detail is on.
                        </Typography>
                      </Box>
                      <Box>
                        <FieldKey k={K.BUG_FROM_CASE_MODE} />
                        <FormControl size="small" sx={{ minWidth: 340 }}>
                          <InputLabel>Create bug from a failed case</InputLabel>
                          <Select label="Create bug from a failed case" value={bugFromMode}
                            onChange={e => setBugFromMode(e.target.value)}>
                            <MenuItem value="instant">Instant pre-fill — no LLM (recommended)</MenuItem>
                            <MenuItem value="agent">Run Bug Writer on that case — polished, slower</MenuItem>
                          </Select>
                        </FormControl>
                        <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 0.5 }}>
                          How the “Create bug” button on a failed test case builds the bug draft.
                        </Typography>
                      </Box>
                    </Stack>
                    <SaveBar onClick={() => save({
                      [K.VERIFY_DETAIL]: verifyDetail,
                      [K.VERIFY_STEP_SCOPE]: verifyScope,
                      [K.BUG_FROM_CASE_MODE]: bugFromMode,
                    })} />
                  </Panel>
                </Box>
              )}

              {/* ── REPORTS ─────────────────────────────────────────────────── */}
              {tab === 'reports' && (
                <Box>
                  <SectionHeader eyebrow="06 · Reports"
                    title="Weekly report configuration"
                    subtitle="Schedule, default date range, and which boards / area paths to include by default. The Thursday auto-run only fires while the app is running." />
                  <Panel>
                    <Stack spacing={3}>
                      <Box sx={{ display: 'flex', gap: 1.5, alignItems: 'center', flexWrap: 'wrap' }}>
                        <FormControl size="small" sx={{ minWidth: 170 }}>
                          <InputLabel>Preview theme</InputLabel>
                          <Select label="Preview theme" value={previewTheme}
                            onChange={e => setPreviewTheme(e.target.value)}>
                            <MenuItem value="Executive">Executive</MenuItem>
                            <MenuItem value="Standup">Standup</MenuItem>
                            <MenuItem value="Branded">Branded</MenuItem>
                          </Select>
                        </FormControl>
                        <Button variant="outlined" startIcon={<VisibilityIcon />}
                          onClick={() => window.open(
                            `/api/reports/preview?theme=${encodeURIComponent(previewTheme)}`
                            + `&team=${encodeURIComponent(teamName)}`, '_blank')}>
                          Preview report format
                        </Button>
                        <Typography variant="caption" color="text.secondary">
                          Opens a sample report (mock data) in a new tab — no generation needed.
                        </Typography>
                      </Box>

                      <Box>
                        <FieldKey k={K.REPORT_TEAM_NAME} />
                        <TextField label="Team name" size="small"
                          sx={{ width: 320 }} value={teamName}
                          onChange={e => setTeamName(e.target.value)}
                          helperText="Shown on the report cover, e.g. 'Engineering'." />
                      </Box>

                      <FormControlLabel
                        control={<Switch checked={schedEnabled}
                          onChange={(_, v) => setSchedEnabled(v)} />}
                        label="Schedule weekly report (Thursday 11:00, local TZ)" />

                      <Box>
                        <FieldKey k={K.DEFAULT_RANGE} />
                        <TextField label="Default range (days)" size="small" type="number"
                          sx={{ width: 220 }} value={defaultRange}
                          onChange={e => setDefaultRange(e.target.value)}
                          helperText="Used by both the auto-run and the Reports page default." />
                      </Box>

                      <Box>
                        <FieldKey k={K.REPORT_TYPES} />
                        <Autocomplete<string, true, false, true>
                          multiple freeSolo
                          options={REPORT_TYPE_OPTIONS} value={typesList}
                          onChange={(_, v) => setTypesList(v as string[])}
                          renderInput={p => (
                            <TextField {...p} label="Ticket types" size="small"
                              helperText="Work-item types included across the whole report (themes, boards, spotlights). Empty = default set." />
                          )}
                        />
                      </Box>

                      <Box>
                        <FieldKey k={K.DEFAULT_BOARDS} />
                        <Autocomplete<string, true, false, true>
                          multiple freeSolo
                          options={areaPaths} value={boardsList}
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
                      </Box>
                    </Stack>
                    <SaveBar onClick={() => save({
                      [K.REPORT_TEAM_NAME]: teamName,
                      [K.SCHED_ENABLED]: schedEnabled ? 'true' : 'false',
                      [K.DEFAULT_RANGE]: defaultRange,
                      [K.DEFAULT_BOARDS]: boardsList.join('; '),
                      [K.REPORT_TYPES]: typesList.join('; '),
                    })} />
                  </Panel>
                </Box>
              )}

              {/* ── DIAGNOSTICS ─────────────────────────────────────────────── */}
              {tab === 'diag' && (
                <Box>
                  <SectionHeader eyebrow="07 · Diagnostics"
                    title="Live checks"
                    subtitle="Verify ADO + the active LLM are working end-to-end. Useful right after changing credentials or switching providers." />

                  <Stagger>
                    <Box sx={{
                      display: 'grid',
                      gridTemplateColumns: { xs: '1fr', lg: '1fr 1fr' },
                      gap: 2,
                    }}>
                      <StaggerItem>
                        <Panel title="Environment" action={
                          <Button variant="outlined" size="small" startIcon={envLoading ? <CircularProgress size={14} /> : <RefreshIcon />}
                            onClick={checkEnv} disabled={envLoading}>
                            Re-check
                          </Button>
                        }>
                          {env ? (
                            <Stack direction="row" spacing={3} sx={{ flexWrap: 'wrap', gap: 2 }}>
                              <StatusPill ok={!!activeProvider} label="Active LLM"
                                value={activeProvider || 'unset'} />
                              <StatusPill ok={env.adoPatPresent} label="ADO PAT" />
                              <StatusPill ok={env.anthropicKeyPresent} label="Anthropic key" />
                              <StatusPill ok={env.azureOpenAiConfigured} label="Azure OpenAI" />
                              <StatusPill ok={env.claudeCliAvailable} label="Claude CLI"
                                value={env.claudeCliVersion ?? (env.claudeCliAvailable ? 'ok' : 'missing')} />
                            </Stack>
                          ) : <CircularProgress size={20} />}
                        </Panel>
                      </StaggerItem>

                      <StaggerItem>
                        <Panel title="Azure DevOps — read work item">
                          <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
                            <TextField label="Work item ID" size="small" type="number"
                              value={wiId} onChange={e => setWiId(e.target.value)}
                              sx={{ width: 180 }} />
                            <Button variant="outlined"
                              onClick={readWorkItem} disabled={wiLoading || !wiId}>
                              {wiLoading ? <CircularProgress size={18} /> : 'Read'}
                            </Button>
                          </Stack>
                          {wiError && <Alert severity="error" sx={{ mt: 1.5 }}>{wiError}</Alert>}
                          {workItem && (
                            <Box sx={{ mt: 2, p: 2, borderRadius: 1.5, bgcolor: 'background.default', border: 1, borderColor: 'divider' }}>
                              <Typography variant="body2" sx={{ fontFamily: MONO_FONT, fontSize: 11, color: 'text.secondary' }}>
                                #{workItem.id} · {workItem.workItemType} · {workItem.state}
                              </Typography>
                              <Typography variant="body1" sx={{ fontWeight: 500, mt: 0.5 }}>{workItem.title}</Typography>
                              {workItem.assignedTo && (
                                <Typography variant="caption" color="text.secondary">{workItem.assignedTo}</Typography>
                              )}
                            </Box>
                          )}
                        </Panel>
                      </StaggerItem>

                      <StaggerItem>
                        <Panel title="LLM ping" hint="Pings whichever provider is currently active.">
                          <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
                            <Button variant="outlined" onClick={pingLlm} disabled={llmLoading}>
                              {llmLoading ? <CircularProgress size={18} /> : 'Ping'}
                            </Button>
                            {llmReply && (
                              <Typography sx={{
                                fontFamily: MONO_FONT, fontSize: 13,
                                color: 'success.main', flex: 1,
                              }}>{llmReply}</Typography>
                            )}
                          </Stack>
                          {llmError && <Alert severity="error" sx={{ mt: 1.5 }}>{llmError}</Alert>}
                        </Panel>
                      </StaggerItem>
                    </Box>
                  </Stagger>
                </Box>
              )}
            </FadeIn>
          </AnimatePresence>
        </Box>
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
