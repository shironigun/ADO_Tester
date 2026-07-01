/**
 * Settings — connections, providers, and defaults.
 *
 * Vertical tab nav on the left, content on the right (tabs flip to a top bar
 * under 900px). Save logic + API calls are unchanged — the UI was simplified:
 * no raw setting-key labels, no per-field paragraphs, leaner headers.
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
import CloudOutlinedIcon from '@mui/icons-material/CloudOutlined'
import SmartToyOutlinedIcon from '@mui/icons-material/SmartToyOutlined'
import RuleOutlinedIcon from '@mui/icons-material/RuleOutlined'
import AssessmentOutlinedIcon from '@mui/icons-material/AssessmentOutlined'
import BugReportOutlinedIcon from '@mui/icons-material/BugReportOutlined'
import HealthAndSafetyOutlinedIcon from '@mui/icons-material/HealthAndSafetyOutlined'
import RadioButtonCheckedIcon from '@mui/icons-material/RadioButtonChecked'
import ScienceOutlinedIcon from '@mui/icons-material/ScienceOutlined'
import PageHeader from '../components/PageHeader'
import Section from '../components/Section'
import { FadeIn, Stagger, StaggerItem } from '../motion/primitives'
import { MONO_FONT } from '../theme/tokens'
import { apiUrl } from '../config/env'
import { axiosMessage } from '../common/errors'
import {
  getConfig, saveConfig, getClaudeCliModels, getCopilotCliModels, getAdoOrgs, getAdoProjects, getAdoAreaPaths, getAdoBranches, getAdoRepositories,
  type Config, type AdoProject,
} from '../api/settings'
import { getEnvStatus, getWorkItem, pingLlm, type EnvStatus, type WorkItem } from '../api/diagnostics'
import { getPlans, getSuites, type TestPlan, type TestSuite } from '../api/authorTests'

// ── Setting keys ───────────────────────────────────────────────────────────
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
  COPILOT_CLI_PATH: 'copilot_cli.path',
  COPILOT_CLI_MODEL: 'copilot_cli.model',
  COPILOT_CLI_CONTEXT: 'copilot_cli.context',
  COPILOT_CLI_TIMEOUT: 'copilot_cli.timeout_seconds',
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
  TEAMS_WEBHOOK: 'teams.webhook_url',
  TEAMS_TEMPLATE: 'teams.template',
  QALOOP_POLL: 'qaloop.poll_interval_minutes',
  QALOOP_REPORT: 'qaloop.report_provider',
  QALOOP_ACTIVITY: 'qaloop.activity_enabled',
  QALOOP_DEFAULT_REPO: 'qaloop.default_repository',
  QALOOP_DEFAULT_BRANCH: 'qaloop.default_branch',
  QALOOP_DEFAULT_PLAN: 'qaloop.default_plan_id',
  QALOOP_DEFAULT_SUITE: 'qaloop.default_suite_id',
}

const REPORT_TYPE_OPTIONS = ['User Story', 'Bug', 'Defect', 'Task', 'Issue', 'Feature']

// ── Reusable parts ─────────────────────────────────────────────────────────

/** Compact tab heading — a title and one optional line. */
function TabTitle({ title, hint }: { title: string; hint?: string }) {
  return (
    <Box sx={{ mb: 2.5 }}>
      <Typography variant="h5" sx={{ mb: hint ? 0.5 : 0 }}>{title}</Typography>
      {hint && <Typography variant="body2" color="text.secondary">{hint}</Typography>}
    </Box>
  )
}

/** Subsection card — thin wrapper over the shared Section. */
function Panel({ title, children, action }: { title?: string; children: ReactNode; action?: ReactNode }) {
  return <Section title={title} action={action} padding={3}>{children}</Section>
}

/** Save button — anchored to the right. */
function SaveBar({ onClick, label = 'Save changes' }: { onClick: () => void; label?: string }) {
  return (
    <Stack direction="row" sx={{ justifyContent: 'flex-end', mt: 3 }}>
      <Button variant="contained" startIcon={<SaveIcon />} onClick={onClick}>{label}</Button>
    </Stack>
  )
}

/** Status item — used by the header strip + diagnostics. */
function StatusPill({ ok, label, value }: { ok: boolean; label: string; value?: string }) {
  return (
    <Stack direction="row" spacing={0.75} sx={{ alignItems: 'center' }}>
      {ok
        ? <CheckCircleIcon fontSize="small" sx={{ color: 'success.main' }} />
        : <ErrorIcon fontSize="small" sx={{ color: 'error.main' }} />}
      <Typography variant="body2">
        <Box component="span" sx={{ color: 'text.secondary' }}>{label}: </Box>
        <Box component="span" sx={{ fontWeight: 600 }}>{value ?? (ok ? 'OK' : 'Missing')}</Box>
      </Typography>
    </Stack>
  )
}

/** Secret-aware text input. Backend never echoes the value — only a `.set` flag. */
function SecretField({ label, value, set, isSet }: {
  label: string; value: string; set: (v: string) => void; isSet: boolean
}) {
  const [shown, setShown] = useState(false)
  return (
    <TextField
      label={label}
      size="small" fullWidth
      type={shown ? 'text' : 'password'}
      value={value}
      onChange={e => set(e.target.value)}
      placeholder={isSet ? 'Saved — leave blank to keep' : ''}
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
  )
}

/** LLM provider card. */
function ProviderCard({
  name, blurb, active, ready, children,
}: { name: string; blurb: string; active: boolean; ready: boolean; children: ReactNode }) {
  return (
    <Card sx={{
      height: '100%',
      borderColor: active ? 'primary.main' : 'divider',
      borderWidth: active ? 2 : 1,
      transition: 'all 200ms ease',
      ...(active && { boxShadow: theme => `0 0 0 4px ${theme.palette.primary.main}14` }),
    }}>
      <CardContent sx={{ p: 2.5 }}>
        <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 0.3 }}>
          <Typography variant="h6">{name}</Typography>
          {active && (
            <Chip size="small" label="active" color="primary"
              icon={<RadioButtonCheckedIcon sx={{ fontSize: 12 }} />}
              sx={{ height: 20, fontSize: 10.5 }} />
          )}
          {!active && ready && (
            <Chip size="small" label="ready" variant="outlined" color="success" sx={{ height: 20, fontSize: 10.5 }} />
          )}
        </Stack>
        <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 1.5, lineHeight: 1.45 }}>
          {blurb}
        </Typography>
        <Stack spacing={1.5}>{children}</Stack>
      </CardContent>
    </Card>
  )
}

// ── Main component ─────────────────────────────────────────────────────────

const TABS = [
  { key: 'ado',      label: 'Azure DevOps',  icon: <CloudOutlinedIcon fontSize="small" /> },
  { key: 'llm',      label: 'LLM Providers', icon: <SmartToyOutlinedIcon fontSize="small" /> },
  { key: 'tests',    label: 'Author Tests',  icon: <RuleOutlinedIcon fontSize="small" /> },
  { key: 'findbugs', label: 'Find Bugs',     icon: <BugReportOutlinedIcon fontSize="small" /> },
  { key: 'qaloop',   label: 'QA Loop',       icon: <ScienceOutlinedIcon fontSize="small" /> },
  { key: 'reports',  label: 'Reports',       icon: <AssessmentOutlinedIcon fontSize="small" /> },
  { key: 'diag',     label: 'Diagnostics',   icon: <HealthAndSafetyOutlinedIcon fontSize="small" /> },
] as const
type TabKey = typeof TABS[number]['key']

export default function SettingsPage() {
  const theme = useTheme()
  const isNarrow = useMediaQuery(theme.breakpoints.down('md'))

  const [tab, setTab] = useState<TabKey>('ado')

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
  const [copilotCliPath, setCopilotCliPath] = useState('')
  const [copilotCliModel, setCopilotCliModel] = useState('')
  const [copilotCliContext, setCopilotCliContext] = useState('')
  const [copilotCliTimeout, setCopilotCliTimeout] = useState('180')
  const [copilotCliModels, setCopilotCliModels] = useState<string[]>([])
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
  const [teamsWebhook, setTeamsWebhook] = useState('')
  const [teamsTemplate, setTeamsTemplate] = useState('developer')
  const [pollInterval, setPollInterval] = useState('5')
  const [reportProvider, setReportProvider] = useState('allure')
  const [qaLoopActivity, setQaLoopActivity] = useState(true)
  const [qaLoopDefaultRepo, setQaLoopDefaultRepo] = useState('')
  const [qaLoopDefaultBranch, setQaLoopDefaultBranch] = useState('')
  const [qaLoopDefaultPlanId, setQaLoopDefaultPlanId] = useState<number | ''>('')
  const [qaLoopDefaultSuiteId, setQaLoopDefaultSuiteId] = useState<number | ''>('')
  const [qaLoopDefaultRepos, setQaLoopDefaultRepos] = useState<string[]>([])
  const [qaLoopDefaultBranches, setQaLoopDefaultBranches] = useState<string[]>([])
  const [qaLoopDefaultBranchesLoading, setQaLoopDefaultBranchesLoading] = useState(false)
  const [qaLoopDefaultPlans, setQaLoopDefaultPlans] = useState<TestPlan[]>([])
  const [qaLoopDefaultSuites, setQaLoopDefaultSuites] = useState<TestSuite[]>([])

  const [plans, setPlans] = useState<TestPlan[]>([])
  const [suites, setSuites] = useState<TestSuite[]>([])

  const [orgs, setOrgs] = useState<string[]>([])
  const [projects, setProjects] = useState<AdoProject[]>([])
  const [projectsLoading, setProjectsLoading] = useState(false)
  const [areaPaths, setAreaPaths] = useState<string[]>([])
  const [areasLoading, setAreasLoading] = useState(false)
  const [boardsList, setBoardsList] = useState<string[]>([])
  const [branches, setBranches] = useState<string[]>([])
  const [branchesLoading, setBranchesLoading] = useState(false)

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
      const data = await getConfig()
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
      setCopilotCliPath(String(data[K.COPILOT_CLI_PATH] ?? ''))
      setCopilotCliModel(String(data[K.COPILOT_CLI_MODEL] ?? ''))
      setCopilotCliContext(String(data[K.COPILOT_CLI_CONTEXT] ?? ''))
      setCopilotCliTimeout(String(data[K.COPILOT_CLI_TIMEOUT] ?? '180'))
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
      setTeamsTemplate(String(data[K.TEAMS_TEMPLATE] ?? 'developer'))
      setPollInterval(String(data[K.QALOOP_POLL] ?? '5'))
      setReportProvider(String(data[K.QALOOP_REPORT] ?? 'allure'))
      setQaLoopActivity(String(data[K.QALOOP_ACTIVITY] ?? 'true').toLowerCase() === 'true')
      setQaLoopDefaultRepo(String(data[K.QALOOP_DEFAULT_REPO] ?? ''))
      setQaLoopDefaultBranch(String(data[K.QALOOP_DEFAULT_BRANCH] ?? ''))
      setQaLoopDefaultPlanId(data[K.QALOOP_DEFAULT_PLAN] ? Number(data[K.QALOOP_DEFAULT_PLAN]) : '')
      setQaLoopDefaultSuiteId(data[K.QALOOP_DEFAULT_SUITE] ? Number(data[K.QALOOP_DEFAULT_SUITE]) : '')

      // Eagerly pre-load option lists so saved values render correctly without opening dropdowns.
      const savedRepo = String(data[K.QALOOP_DEFAULT_REPO] ?? '')
      const savedPlanId = data[K.QALOOP_DEFAULT_PLAN] ? Number(data[K.QALOOP_DEFAULT_PLAN]) : null
      const savedSuiteId = data[K.QALOOP_DEFAULT_SUITE] ? Number(data[K.QALOOP_DEFAULT_SUITE]) : null
      if (savedRepo) {
        getAdoBranches(savedRepo).then(setQaLoopDefaultBranches).catch(() => {})
      }
      if (savedPlanId) {
        getPlans().then(ps => {
          setQaLoopDefaultPlans(ps)
          if (savedSuiteId) getSuites(savedPlanId).then(setQaLoopDefaultSuites).catch(() => {})
        }).catch(() => {})
      }
    } catch (e) { setError(axiosMessage(e)) }
    finally { setLoading(false) }
  }, [])

  useEffect(() => { loadConfig() }, [loadConfig])

  useEffect(() => {
    getClaudeCliModels().then(setClaudeCliModels).catch(() => setClaudeCliModels([]))
  }, [])

  useEffect(() => {
    getCopilotCliModels().then(setCopilotCliModels).catch(() => setCopilotCliModels([]))
  }, [])

  // Auto-fetch env so the status strip renders without clicking "Re-check".
  useEffect(() => {
    getEnvStatus().then(setEnv).catch(() => {})
  }, [config[`${K.ADO_PAT}.set`], config[`${K.ANTHROPIC_KEY}.set`]])

  useEffect(() => {
    getAdoOrgs().then(setOrgs).catch(() => setOrgs([]))
  }, [config[`${K.ADO_PAT}.set`]])

  useEffect(() => {
    if (!adoOrg) { setProjects([]); return }
    setProjectsLoading(true)
    getAdoProjects(adoOrg)
      .then(setProjects)
      .catch(() => setProjects([]))
      .finally(() => setProjectsLoading(false))
  }, [adoOrg, config[`${K.ADO_PAT}.set`]])

  useEffect(() => {
    if (!adoOrg || !adoProject) { setAreaPaths([]); return }
    setAreasLoading(true)
    getAdoAreaPaths(adoOrg, adoProject)
      .then(setAreaPaths)
      .catch(() => setAreaPaths([]))
      .finally(() => setAreasLoading(false))
  }, [adoOrg, adoProject, config[`${K.ADO_PAT}.set`]])

  // Branches for the configured repo (drives the Default branch dropdown). Reloads when the saved
  // repository or PAT changes.
  useEffect(() => {
    setBranchesLoading(true)
    getAdoBranches()
      .then(setBranches)
      .catch(() => setBranches([]))
      .finally(() => setBranchesLoading(false))
  }, [config[K.ADO_REPO], config[`${K.ADO_PAT}.set`]])

  useEffect(() => {
    getPlans().then(setPlans).catch(() => setPlans([]))
  }, [config[`${K.ADO_PAT}.set`]])

  useEffect(() => {
    if (!defaultPlan) { setSuites([]); return }
    getSuites(defaultPlan).then(setSuites).catch(() => setSuites([]))
  }, [defaultPlan])

  async function save(values: Record<string, string>) {
    try {
      const data = await saveConfig(values)
      setConfig(data)
      setToast('Saved')
      ;([
        [K.ADO_PAT, setAdoPat],
        [K.ANTHROPIC_KEY, setAnthropicKey],
        [K.AZURE_KEY, setAzureKey],
        [K.OPENAI_KEY, setOpenaiKey],
        [K.TEAMS_WEBHOOK, setTeamsWebhook],
      ] as const).forEach(([k, setter]) => { if (values[k]) setter('') })
    } catch (e) { setError(axiosMessage(e)) }
  }

  async function checkEnv() {
    setEnvLoading(true)
    try {
      setEnv(await getEnvStatus())
    } catch (e) { setError(axiosMessage(e)) }
    finally { setEnvLoading(false) }
  }
  async function readWorkItem() {
    setWiError(''); setWorkItem(null); setWiLoading(true)
    try {
      setWorkItem(await getWorkItem(wiId))
    } catch (e) { setWiError(axiosMessage(e)) }
    finally { setWiLoading(false) }
  }
  async function runLlmPing() {
    setLlmError(''); setLlmReply(''); setLlmLoading(true)
    try {
      const data = await pingLlm()
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

  // ── Page header with status strip ────────────────────────────────────────
  const Header = (
    <PageHeader
      title="Settings"
      subtitle="Connections, providers, and defaults."
      actions={
        <Stack direction="row" spacing={3} sx={{ alignItems: 'center' }}>
          <StatusPill ok={!!activeProvider} label="LLM" value={activeProvider || 'unset'} />
          <StatusPill ok={!!env?.adoPatPresent} label="ADO" value={env?.adoPatPresent ? 'OK' : 'Missing'} />
          <Tooltip title="Refresh status">
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
        minWidth: isNarrow ? '100%' : 200,
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

      <Stack direction={isNarrow ? 'column' : 'row'} spacing={isNarrow ? 0 : 4} sx={{ alignItems: 'stretch' }}>
        {TabBar}

        <Box sx={{ flex: 1, minWidth: 0, pt: isNarrow ? 3 : 0, pl: isNarrow ? 0 : 1 }}>
          <AnimatePresence mode="wait">
            <FadeIn key={tab} y={10}>

              {/* ── AZURE DEVOPS ──────────────────────────────────────────── */}
              {tab === 'ado' && (
                <Box>
                  <TabTitle title="Azure DevOps" hint="Save the PAT first — the organization and project lists load once it's stored." />
                  <Panel>
                    <Stack spacing={2.5}>
                      <SecretField label="Personal Access Token" value={adoPat} set={setAdoPat}
                        isSet={Boolean(config[`${K.ADO_PAT}.set`])} />
                      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
                        <Autocomplete
                          sx={{ flex: 1 }}
                          freeSolo options={orgs} value={adoOrg}
                          onChange={(_, v) => { setAdoOrg(v || ''); setAdoProject('') }}
                          onInputChange={(_, v) => setAdoOrg(v)}
                          renderInput={p => <TextField {...p} label="Organization" size="small" />}
                        />
                        <Autocomplete
                          sx={{ flex: 1 }}
                          options={projects.map(p => p.name)} value={adoProject}
                          onChange={(_, v) => setAdoProject(v || '')}
                          loading={projectsLoading}
                          disabled={!adoOrg}
                          renderInput={p => (
                            <TextField {...p} label="Project" size="small"
                              helperText={!adoOrg ? 'Pick an organization first.' : undefined} />
                          )}
                        />
                      </Stack>
                      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
                        <TextField label="Git repository" size="small" sx={{ flex: 1 }} value={adoRepo}
                          onChange={e => setAdoRepo(e.target.value)} placeholder="Your Repo" />
                        <Autocomplete
                          sx={{ flex: 1 }}
                          freeSolo options={branches} value={adoBranch}
                          onChange={(_, v) => setAdoBranch(v || '')}
                          onInputChange={(_, v) => setAdoBranch(v)}
                          loading={branchesLoading}
                          renderInput={p => (
                            <TextField {...p} label="Default branch" size="small" placeholder="main"
                              helperText={branchesLoading ? 'Loading branches…'
                                : branches.length ? `${branches.length} branch(es) — or type any` : undefined} />
                          )}
                        />
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

              {/* ── LLM PROVIDERS ─────────────────────────────────────────── */}
              {tab === 'llm' && (
                <Box>
                  <TabTitle title="LLM providers" hint="Switch the active provider from the top bar. Keys are saved write-only." />
                  <Stagger>
                    <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', md: '1fr 1fr' }, gap: 2 }}>
                      <StaggerItem>
                        <ProviderCard name="Anthropic" blurb="Claude API — fastest direct path."
                          active={activeProvider === 'Anthropic'} ready={!!env?.anthropicKeyPresent}>
                          <SecretField label="API key" value={anthropicKey} set={setAnthropicKey}
                            isSet={Boolean(config[`${K.ANTHROPIC_KEY}.set`])} />
                          <TextField label="Model" size="small" fullWidth value={anthropicModel}
                            onChange={e => setAnthropicModel(e.target.value)} placeholder="claude-sonnet-4-6" />
                          <Button size="small" variant="text" sx={{ alignSelf: 'flex-end' }}
                            onClick={() => save({ [K.ANTHROPIC_KEY]: anthropicKey, [K.ANTHROPIC_MODEL]: anthropicModel })}>
                            Save
                          </Button>
                        </ProviderCard>
                      </StaggerItem>

                      <StaggerItem>
                        <ProviderCard name="Azure OpenAI" blurb="Azure-native AI for restricted shops."
                          active={activeProvider === 'AzureOpenAI'} ready={!!env?.azureOpenAiConfigured}>
                          <TextField label="Endpoint" size="small" fullWidth value={azureEndpoint}
                            onChange={e => setAzureEndpoint(e.target.value)}
                            placeholder="https://your-resource.openai.azure.com" />
                          <SecretField label="API key" value={azureKey} set={setAzureKey}
                            isSet={Boolean(config[`${K.AZURE_KEY}.set`])} />
                          <TextField label="Deployment" size="small" fullWidth value={azureDeploy}
                            onChange={e => setAzureDeploy(e.target.value)} placeholder="gpt-4o-mini" />
                          <Button size="small" variant="text" sx={{ alignSelf: 'flex-end' }}
                            onClick={() => save({
                              [K.AZURE_ENDPOINT]: azureEndpoint, [K.AZURE_KEY]: azureKey, [K.AZURE_DEPLOY]: azureDeploy,
                            })}>Save</Button>
                        </ProviderCard>
                      </StaggerItem>

                      <StaggerItem>
                        <ProviderCard name="OpenAI-compatible" blurb="GitHub Models (free) · Groq · OpenAI · Ollama."
                          active={activeProvider === 'OpenAICompatible'} ready={!!config[`${K.OPENAI_KEY}.set`]}>
                          <TextField label="Base URL" size="small" fullWidth value={openaiBase}
                            onChange={e => setOpenaiBase(e.target.value)}
                            placeholder="https://models.inference.ai.azure.com" />
                          <SecretField label="API key" value={openaiKey} set={setOpenaiKey}
                            isSet={Boolean(config[`${K.OPENAI_KEY}.set`])} />
                          <TextField label="Model" size="small" fullWidth value={openaiModel}
                            onChange={e => setOpenaiModel(e.target.value)} placeholder="gpt-4o-mini" />
                          <Button size="small" variant="text" sx={{ alignSelf: 'flex-end' }}
                            onClick={() => save({
                              [K.OPENAI_BASE_URL]: openaiBase, [K.OPENAI_KEY]: openaiKey, [K.OPENAI_MODEL]: openaiModel,
                            })}>Save</Button>
                        </ProviderCard>
                      </StaggerItem>

                      <StaggerItem>
                        <ProviderCard name="Claude CLI"
                          blurb={env?.claudeCliAvailable
                            ? `Local CLI ${env.claudeCliVersion} — uses your Pro/Max login, no key.`
                            : 'Local `claude` CLI not detected — install Claude Code and log in.'}
                          active={activeProvider === 'ClaudeCli'} ready={!!env?.claudeCliAvailable}>
                          <TextField label="CLI path" size="small" fullWidth value={claudeCliPath}
                            onChange={e => setClaudeCliPath(e.target.value)} placeholder="claude" />
                          <Autocomplete
                            freeSolo options={claudeCliModels} value={claudeCliModel}
                            onChange={(_, v) => setClaudeCliModel(v || '')}
                            onInputChange={(_, v) => setClaudeCliModel(v)}
                            renderInput={p => <TextField {...p} label="Model" size="small" placeholder="(blank = CLI default)" />}
                          />
                          <TextField label="Timeout (seconds)" size="small" type="number" fullWidth
                            value={claudeCliTimeout} onChange={e => setClaudeCliTimeout(e.target.value)} />
                          <Button size="small" variant="text" sx={{ alignSelf: 'flex-end' }}
                            onClick={() => save({
                              [K.CLAUDE_CLI_PATH]: claudeCliPath, [K.CLAUDE_CLI_MODEL]: claudeCliModel,
                              [K.CLAUDE_CLI_TIMEOUT]: claudeCliTimeout,
                            })}>Save</Button>
                        </ProviderCard>
                      </StaggerItem>

                      <StaggerItem>
                        <ProviderCard name="Copilot CLI"
                          blurb={env?.copilotCliAvailable
                            ? `Local CLI ${env.copilotCliVersion} — uses your GitHub Copilot login, no key.`
                            : 'Local `copilot` CLI not detected — install GitHub Copilot CLI and run `copilot auth login`.'}
                          active={activeProvider === 'CopilotCli'} ready={!!env?.copilotCliAvailable}>
                          <TextField label="CLI path (blank = auto-detect)" size="small" fullWidth value={copilotCliPath}
                            onChange={e => setCopilotCliPath(e.target.value)} placeholder="copilot"
                            helperText="Leave blank to auto-find copilot / github-copilot-cli / gh-copilot on PATH, in %APPDATA%\npm, pnpm, Volta, or npm's global prefix." />
                          <Autocomplete
                            freeSolo options={copilotCliModels} value={copilotCliModel}
                            onChange={(_, v) => setCopilotCliModel(v || '')}
                            onInputChange={(_, v) => setCopilotCliModel(v)}
                            renderInput={p => <TextField {...p} label="Model" size="small" placeholder="(blank = CLI default)" />}
                          />
                          <FormControl size="small" fullWidth>
                            <InputLabel>Context window</InputLabel>
                            <Select label="Context window" value={copilotCliContext}
                              onChange={e => setCopilotCliContext(e.target.value)}>
                              <MenuItem value="">Default (CLI decides)</MenuItem>
                              <MenuItem value="long_context">long_context — maximum context window</MenuItem>
                            </Select>
                          </FormControl>
                          <TextField label="Timeout (seconds)" size="small" type="number" fullWidth
                            value={copilotCliTimeout} onChange={e => setCopilotCliTimeout(e.target.value)} />
                          <Button size="small" variant="text" sx={{ alignSelf: 'flex-end' }}
                            onClick={() => save({
                              [K.COPILOT_CLI_PATH]: copilotCliPath, [K.COPILOT_CLI_MODEL]: copilotCliModel,
                              [K.COPILOT_CLI_CONTEXT]: copilotCliContext, [K.COPILOT_CLI_TIMEOUT]: copilotCliTimeout,
                            })}>Save</Button>
                        </ProviderCard>
                      </StaggerItem>
                    </Box>
                  </Stagger>
                </Box>
              )}

              {/* ── AUTHOR TESTS ──────────────────────────────────────────── */}
              {tab === 'tests' && (
                <Box>
                  <TabTitle title="Author Tests defaults" hint="Pre-selected on the Author Tests push step." />
                  <Panel>
                    <Stack direction={{ xs: 'column', md: 'row' }} spacing={2}>
                      <FormControl size="small" sx={{ flex: 1 }}>
                        <InputLabel>Default Test Plan</InputLabel>
                        <Select label="Default Test Plan" value={defaultPlan}
                          onChange={e => { setDefaultPlan(e.target.value as number); setDefaultSuite('') }}>
                          <MenuItem value="">(none)</MenuItem>
                          {plans.map(p => (
                            <MenuItem key={p.id} value={p.id}>
                              {p.name}
                              <Box component="span" sx={{ ml: 1, fontFamily: MONO_FONT, fontSize: 11, color: 'text.secondary' }}>#{p.id}</Box>
                            </MenuItem>
                          ))}
                        </Select>
                      </FormControl>
                      <FormControl size="small" sx={{ flex: 1 }} disabled={!defaultPlan}>
                        <InputLabel>Default Test Suite</InputLabel>
                        <Select label="Default Test Suite" value={defaultSuite}
                          onChange={e => setDefaultSuite(e.target.value as number)}>
                          <MenuItem value="">(none)</MenuItem>
                          {suites.map(s => (
                            <MenuItem key={s.id} value={s.id}>
                              {s.name}
                              <Box component="span" sx={{ ml: 1, fontFamily: MONO_FONT, fontSize: 11, color: 'text.secondary' }}>#{s.id}</Box>
                            </MenuItem>
                          ))}
                        </Select>
                      </FormControl>
                    </Stack>
                    <SaveBar onClick={() => save({
                      [K.DEFAULT_PLAN_ID]: defaultPlan === '' ? '' : String(defaultPlan),
                      [K.DEFAULT_SUITE_ID]: defaultSuite === '' ? '' : String(defaultSuite),
                    })} />
                  </Panel>
                </Box>
              )}

              {/* ── FIND BUGS ─────────────────────────────────────────────── */}
              {tab === 'findbugs' && (
                <Box>
                  <TabTitle title="Find Bugs" hint="How deeply code-verification checks each case, and how a failed case becomes a bug." />
                  <Panel>
                    <Stack spacing={2.5}>
                      <FormControl size="small" sx={{ maxWidth: 420 }}>
                        <InputLabel>Verification detail</InputLabel>
                        <Select label="Verification detail" value={verifyDetail}
                          onChange={e => setVerifyDetail(e.target.value)}>
                          <MenuItem value="case">Case only — one verdict per case</MenuItem>
                          <MenuItem value="step">Per step (recommended)</MenuItem>
                          <MenuItem value="step_full">Per step + code detail</MenuItem>
                        </Select>
                      </FormControl>
                      <FormControl size="small" sx={{ maxWidth: 420 }}>
                        <InputLabel>Per-step coverage</InputLabel>
                        <Select label="Per-step coverage" value={verifyScope}
                          onChange={e => setVerifyScope(e.target.value)}>
                          <MenuItem value="fails_only">Failing &amp; inconclusive only (recommended)</MenuItem>
                          <MenuItem value="all">Every case</MenuItem>
                        </Select>
                      </FormControl>
                      <FormControl size="small" sx={{ maxWidth: 420 }}>
                        <InputLabel>Create bug from a failed case</InputLabel>
                        <Select label="Create bug from a failed case" value={bugFromMode}
                          onChange={e => setBugFromMode(e.target.value)}>
                          <MenuItem value="instant">Instant pre-fill (recommended)</MenuItem>
                          <MenuItem value="agent">Run Bug Writer — slower</MenuItem>
                        </Select>
                      </FormControl>
                    </Stack>
                    <SaveBar onClick={() => save({
                      [K.VERIFY_DETAIL]: verifyDetail,
                      [K.VERIFY_STEP_SCOPE]: verifyScope,
                      [K.BUG_FROM_CASE_MODE]: bugFromMode,
                    })} />
                  </Panel>
                </Box>
              )}

              {/* ── QA LOOP ───────────────────────────────────────────────── */}
              {tab === 'qaloop' && (
                <Box>
                  <TabTitle title="QA Loop" hint="Pipeline watcher, Teams notifications, report format, and activity toggle." />

                  <Panel title="New Scan defaults">
                    <Stack spacing={2}>
                      <Typography variant="body2" color="text.secondary">
                        Pre-fill the New Scan dialog — users can still override each field.
                      </Typography>
                      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
                        <Autocomplete
                          sx={{ flex: 1 }}
                          freeSolo options={qaLoopDefaultRepos} value={qaLoopDefaultRepo}
                          onOpen={() => { if (!qaLoopDefaultRepos.length) getAdoRepositories().then(setQaLoopDefaultRepos).catch(() => setQaLoopDefaultRepos([])) }}
                          onChange={(_, v) => {
                            setQaLoopDefaultRepo(v || '')
                            setQaLoopDefaultBranch('')
                            setQaLoopDefaultBranches([])
                            if (v) {
                              setQaLoopDefaultBranchesLoading(true)
                              getAdoBranches(v).then(bs => { setQaLoopDefaultBranches(bs) }).catch(() => {}).finally(() => setQaLoopDefaultBranchesLoading(false))
                            }
                          }}
                          onInputChange={(_, v) => setQaLoopDefaultRepo(v)}
                          renderInput={p => <TextField {...p} label="Default repository" size="small" placeholder="(none)" />}
                        />
                        <Autocomplete
                          sx={{ flex: 1 }}
                          freeSolo options={qaLoopDefaultBranches} value={qaLoopDefaultBranch}
                          disabled={!qaLoopDefaultRepo}
                          loading={qaLoopDefaultBranchesLoading}
                          onOpen={() => {
                            if (qaLoopDefaultRepo && !qaLoopDefaultBranches.length) {
                              setQaLoopDefaultBranchesLoading(true)
                              getAdoBranches(qaLoopDefaultRepo).then(setQaLoopDefaultBranches).catch(() => {}).finally(() => setQaLoopDefaultBranchesLoading(false))
                            }
                          }}
                          onChange={(_, v) => setQaLoopDefaultBranch(v || '')}
                          onInputChange={(_, v) => setQaLoopDefaultBranch(v)}
                          renderInput={p => <TextField {...p} label="Default branch" size="small" placeholder="(none)"
                            helperText={!qaLoopDefaultRepo ? 'Pick a repository first' : undefined} />}
                        />
                      </Stack>
                      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
                        <FormControl size="small" sx={{ flex: 1 }}>
                          <InputLabel>Default Test Plan</InputLabel>
                          <Select label="Default Test Plan" value={qaLoopDefaultPlanId}
                            onOpen={() => { getPlans().then(setQaLoopDefaultPlans).catch(() => {}) }}
                            onChange={e => {
                              const pid = e.target.value as number
                              setQaLoopDefaultPlanId(pid)
                              setQaLoopDefaultSuiteId('')
                              setQaLoopDefaultSuites([])
                              if (pid) getSuites(pid).then(setQaLoopDefaultSuites).catch(() => {})
                            }}>
                            <MenuItem value="">(none)</MenuItem>
                            {qaLoopDefaultPlans.map(p => (
                              <MenuItem key={p.id} value={p.id}>
                                {p.name}
                                <Box component="span" sx={{ ml: 1, fontFamily: MONO_FONT, fontSize: 11, color: 'text.secondary' }}>#{p.id}</Box>
                              </MenuItem>
                            ))}
                          </Select>
                        </FormControl>
                        <FormControl size="small" sx={{ flex: 1 }} disabled={!qaLoopDefaultPlanId}>
                          <InputLabel>Default Test Suite</InputLabel>
                          <Select label="Default Test Suite" value={qaLoopDefaultSuiteId}
                            onOpen={() => {
                              if (qaLoopDefaultPlanId && !qaLoopDefaultSuites.length)
                                getSuites(qaLoopDefaultPlanId as number).then(setQaLoopDefaultSuites).catch(() => {})
                            }}
                            onChange={e => setQaLoopDefaultSuiteId(e.target.value as number)}>
                            <MenuItem value="">(none)</MenuItem>
                            {qaLoopDefaultSuites.map(s => (
                              <MenuItem key={s.id} value={s.id}>
                                {s.name}
                                <Box component="span" sx={{ ml: 1, fontFamily: MONO_FONT, fontSize: 11, color: 'text.secondary' }}>#{s.id}</Box>
                              </MenuItem>
                            ))}
                          </Select>
                        </FormControl>
                      </Stack>
                    </Stack>
                    <SaveBar label="Save scan defaults" onClick={() => save({
                      [K.QALOOP_DEFAULT_REPO]: qaLoopDefaultRepo,
                      [K.QALOOP_DEFAULT_BRANCH]: qaLoopDefaultBranch,
                      [K.QALOOP_DEFAULT_PLAN]: qaLoopDefaultPlanId === '' ? '' : String(qaLoopDefaultPlanId),
                      [K.QALOOP_DEFAULT_SUITE]: qaLoopDefaultSuiteId === '' ? '' : String(qaLoopDefaultSuiteId),
                    })} />
                  </Panel>

                  <Panel>
                    <Stack spacing={2.5}>
                      <SecretField label="Teams webhook URL" value={teamsWebhook} set={setTeamsWebhook}
                        isSet={Boolean(config[`${K.TEAMS_WEBHOOK}.set`])} />
                      <FormControl size="small" sx={{ maxWidth: 320 }}>
                        <InputLabel>Teams card template</InputLabel>
                        <Select label="Teams card template" value={teamsTemplate}
                          onChange={e => setTeamsTemplate(e.target.value)}>
                          <MenuItem value="developer">Developer</MenuItem>
                          <MenuItem value="compact">Compact</MenuItem>
                          <MenuItem value="scheduled">Scheduled</MenuItem>
                        </Select>
                      </FormControl>
                      <TextField label="Pipeline poll interval (minutes)" size="small" type="number"
                        sx={{ width: 260 }} value={pollInterval}
                        onChange={e => setPollInterval(e.target.value)} />
                      <FormControl size="small" sx={{ maxWidth: 320 }}>
                        <InputLabel>Report format</InputLabel>
                        <Select label="Report format" value={reportProvider}
                          onChange={e => setReportProvider(e.target.value)}>
                          <MenuItem value="allure">Allure (default)</MenuItem>
                          <MenuItem value="playwright">Playwright HTML</MenuItem>
                          <MenuItem value="both">Both</MenuItem>
                        </Select>
                      </FormControl>
                      <FormControlLabel
                        control={<Switch checked={qaLoopActivity} onChange={(_, v) => setQaLoopActivity(v)} />}
                        label="Stream agent activity (disabling saves bandwidth and storage)" />
                    </Stack>
                    <SaveBar onClick={() => save({
                      [K.TEAMS_WEBHOOK]: teamsWebhook,
                      [K.TEAMS_TEMPLATE]: teamsTemplate,
                      [K.QALOOP_POLL]: pollInterval,
                      [K.QALOOP_REPORT]: reportProvider,
                      [K.QALOOP_ACTIVITY]: qaLoopActivity ? 'true' : 'false',
                    })} />
                  </Panel>
                </Box>
              )}

              {/* ── REPORTS ───────────────────────────────────────────────── */}
              {tab === 'reports' && (
                <Box>
                  <TabTitle title="Reports" hint="The weekly auto-run fires Thursday 11:00, only while the app is running." />
                  <Panel>
                    <Stack spacing={2.5}>
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
                          onClick={() => window.open(apiUrl(
                            `/api/reports/preview?theme=${encodeURIComponent(previewTheme)}`
                            + `&team=${encodeURIComponent(teamName)}`), '_blank')}>
                          Preview format
                        </Button>
                      </Box>

                      <TextField label="Team name" size="small" sx={{ width: 320 }} value={teamName}
                        onChange={e => setTeamName(e.target.value)} placeholder="Shown on the report cover" />

                      <FormControlLabel
                        control={<Switch checked={schedEnabled} onChange={(_, v) => setSchedEnabled(v)} />}
                        label="Schedule weekly report (Thursday 11:00)" />

                      <TextField label="Default range (days)" size="small" type="number" sx={{ width: 220 }}
                        value={defaultRange} onChange={e => setDefaultRange(e.target.value)} />

                      <Autocomplete<string, true, false, true>
                        multiple freeSolo options={REPORT_TYPE_OPTIONS} value={typesList}
                        onChange={(_, v) => setTypesList(v as string[])}
                        renderInput={p => (
                          <TextField {...p} label="Ticket types" size="small"
                            helperText="Empty = default set." />
                        )}
                      />

                      <Autocomplete<string, true, false, true>
                        multiple freeSolo options={areaPaths} value={boardsList}
                        onChange={(_, v) => setBoardsList(v as string[])}
                        loading={areasLoading}
                        disabled={!adoOrg || !adoProject}
                        renderInput={p => (
                          <TextField {...p} label="Boards / area paths" size="small"
                            helperText={!adoOrg || !adoProject ? 'Set organization + project first.' : 'Empty = whole project.'} />
                        )}
                      />
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

              {/* ── DIAGNOSTICS ───────────────────────────────────────────── */}
              {tab === 'diag' && (
                <Box>
                  <TabTitle title="Diagnostics" hint="Quick end-to-end checks for ADO and the active LLM." />
                  <Stagger>
                    <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', lg: '1fr 1fr' }, gap: 2 }}>
                      <StaggerItem>
                        <Panel title="Environment" action={
                          <Button variant="outlined" size="small"
                            startIcon={envLoading ? <CircularProgress size={14} /> : <RefreshIcon />}
                            onClick={checkEnv} disabled={envLoading}>Re-check</Button>
                        }>
                          {env ? (
                            <Stack direction="row" spacing={3} sx={{ flexWrap: 'wrap', gap: 2 }}>
                              <StatusPill ok={!!activeProvider} label="Active LLM" value={activeProvider || 'unset'} />
                              <StatusPill ok={env.adoPatPresent} label="ADO PAT" />
                              <StatusPill ok={env.anthropicKeyPresent} label="Anthropic key" />
                              <StatusPill ok={env.azureOpenAiConfigured} label="Azure OpenAI" />
                              <StatusPill ok={env.claudeCliAvailable} label="Claude CLI"
                                value={env.claudeCliVersion ?? (env.claudeCliAvailable ? 'ok' : 'missing')} />
                              <StatusPill ok={env.copilotCliAvailable} label="Copilot CLI"
                                value={env.copilotCliVersion ?? (env.copilotCliAvailable ? 'ok' : 'missing')} />
                            </Stack>
                          ) : <CircularProgress size={20} />}
                        </Panel>
                      </StaggerItem>

                      <StaggerItem>
                        <Panel title="Read a work item">
                          <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
                            <TextField label="Work item ID" size="small" type="number"
                              value={wiId} onChange={e => setWiId(e.target.value)} sx={{ width: 180 }} />
                            <Button variant="outlined" onClick={readWorkItem} disabled={wiLoading || !wiId}>
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
                        <Panel title="LLM ping">
                          <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
                            <Button variant="outlined" onClick={runLlmPing} disabled={llmLoading}>
                              {llmLoading ? <CircularProgress size={18} /> : 'Ping'}
                            </Button>
                            {llmReply && (
                              <Typography sx={{ fontFamily: MONO_FONT, fontSize: 13, color: 'success.main', flex: 1 }}>
                                {llmReply}
                              </Typography>
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
