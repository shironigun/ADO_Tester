import { useEffect, useMemo, useState } from 'react'
import type { ReactNode } from 'react'
import {
  Alert, Box, Button, Card, CardContent, CircularProgress, Collapse, IconButton,
  Stack, Tab, Tabs, TextField, Tooltip, Typography,
} from '@mui/material'
import CloseIcon from '@mui/icons-material/Close'
import EditIcon from '@mui/icons-material/Edit'
import ExpandLessIcon from '@mui/icons-material/ExpandLess'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import PsychologyIcon from '@mui/icons-material/Psychology'
import SaveIcon from '@mui/icons-material/Save'

export interface UiAnalystFeature { id?: string; name?: string; description?: string }
export interface UiAnalystFlow { name?: string; trigger?: string; steps?: string[]; breaks?: string[] }
export interface UiAnalystScreen { name?: string; route?: string; components?: string[] }

export interface UiAnalystOutput {
  features?: UiAnalystFeature[]
  flows?: UiAnalystFlow[]
  screens?: UiAnalystScreen[]
  openQuestions?: string[]
  mustTest?: string[]
  mustNotTest?: string[]
  authPermissionsSummary?: string
  tenantVariantsSummary?: string
}

export function parseUiAnalysis(json: string | null | undefined): UiAnalystOutput | null {
  if (!json) return null
  try {
    const raw = JSON.parse(json) as Record<string, unknown>
    // Backend used to persist PascalCase; accept both.
    const pick = <T,>(camel: string, pascal: string): T | undefined =>
      (raw[camel] ?? raw[pascal]) as T | undefined
    const mapObj = <T,>(v: unknown, map: (o: Record<string, unknown>) => T): T[] | undefined => {
      if (!Array.isArray(v)) return undefined
      return v.map(item => map((item ?? {}) as Record<string, unknown>))
    }
    return {
      features: mapObj(pick('features', 'Features'), o => ({
        id: (o.id ?? o.Id) as string | undefined,
        name: (o.name ?? o.Name) as string | undefined,
        description: (o.description ?? o.Description) as string | undefined,
      })),
      flows: mapObj(pick('flows', 'Flows'), o => ({
        name: (o.name ?? o.Name) as string | undefined,
        trigger: (o.trigger ?? o.Trigger) as string | undefined,
        steps: (o.steps ?? o.Steps) as string[] | undefined,
        breaks: (o.breaks ?? o.Breaks) as string[] | undefined,
      })),
      screens: mapObj(pick('screens', 'Screens'), o => ({
        name: (o.name ?? o.Name) as string | undefined,
        route: (o.route ?? o.Route) as string | undefined,
        components: (o.components ?? o.Components) as string[] | undefined,
      })),
      openQuestions: pick<string[]>('openQuestions', 'OpenQuestions'),
      mustTest: pick<string[]>('mustTest', 'MustTest'),
      mustNotTest: pick<string[]>('mustNotTest', 'MustNotTest'),
      authPermissionsSummary: pick<string>('authPermissionsSummary', 'AuthPermissionsSummary'),
      tenantVariantsSummary: pick<string>('tenantVariantsSummary', 'TenantVariantsSummary'),
    }
  } catch { return null }
}

type TabKey = 'overview' | 'features' | 'flows' | 'screens' | 'mustTest' | 'mustNotTest' | 'questions'

const TAB_META: { key: TabKey; label: string }[] = [
  { key: 'overview', label: 'Overview' },
  { key: 'features', label: 'Features' },
  { key: 'flows', label: 'Journeys' },
  { key: 'screens', label: 'Pages' },
  { key: 'mustTest', label: 'Cover' },
  { key: 'mustNotTest', label: 'Out of scope' },
  { key: 'questions', label: 'Questions' },
]

function StringListEdit({ items, onChange, placeholder }: {
  items: string[]; onChange: (next: string[]) => void; placeholder?: string
}) {
  const set = (i: number, v: string) => onChange(items.map((x, j) => (j === i ? v : x)))
  const remove = (i: number) => onChange(items.filter((_, j) => j !== i))
  return (
    <Stack spacing={1}>
      {items.map((it, i) => (
        <Stack key={i} direction="row" spacing={0.5} sx={{ alignItems: 'flex-start' }}>
          <TextField size="small" fullWidth multiline value={it} placeholder={placeholder}
            onChange={e => set(i, e.target.value)} />
          <IconButton size="small" onClick={() => remove(i)} sx={{ mt: 0.5 }} aria-label="remove">
            <CloseIcon fontSize="small" />
          </IconButton>
        </Stack>
      ))}
      <Button size="small" onClick={() => onChange([...items, ''])}
        sx={{ alignSelf: 'flex-start', textTransform: 'none' }}>+ Add</Button>
    </Stack>
  )
}

function StringListRead({ items }: { items: string[] }) {
  if (items.length === 0)
    return <Typography variant="body2" color="text.secondary">None.</Typography>
  return (
    <Stack component="ul" spacing={0.75} sx={{ m: 0, pl: 2.5 }}>
      {items.map((t, i) => (
        <Box component="li" key={i} sx={{ '&::marker': { color: 'text.disabled' } }}>
          <Typography variant="body2">{t}</Typography>
        </Box>
      ))}
    </Stack>
  )
}

interface Props {
  uiAnalysisJson: string | null | undefined
  onSave?: (a: UiAnalystOutput) => Promise<void>
  /** Re-run UI Analyst with feedback scoped to one section (tab). */
  onRefineSection?: (sectionLabel: string, feedback: string) => Promise<void>
  defaultExpanded?: boolean
}

export default function UiAnalysisPanel({
  uiAnalysisJson, onSave, onRefineSection, defaultExpanded = true,
}: Props) {
  const a = useMemo(() => parseUiAnalysis(uiAnalysisJson), [uiAnalysisJson])
  const [open, setOpen] = useState(defaultExpanded)
  const [tab, setTab] = useState(0)
  const [editing, setEditing] = useState(false)
  const [draft, setDraft] = useState<UiAnalystOutput>({})
  const [saving, setSaving] = useState(false)
  const [tabFeedback, setTabFeedback] = useState<Partial<Record<TabKey, string>>>({})
  const [refining, setRefining] = useState(false)

  useEffect(() => { if (!editing) setDraft(a ?? {}) }, [a, editing])

  if (!a) return null
  const view: UiAnalystOutput = editing ? draft : a

  const count = (key: TabKey): number => {
    switch (key) {
      case 'overview': return (view.authPermissionsSummary || view.tenantVariantsSummary) ? 1 : 0
      case 'features': return view.features?.length ?? 0
      case 'flows': return view.flows?.length ?? 0
      case 'screens': return view.screens?.length ?? 0
      case 'mustTest': return view.mustTest?.length ?? 0
      case 'mustNotTest': return view.mustNotTest?.length ?? 0
      case 'questions': return view.openQuestions?.length ?? 0
    }
  }

  const tabs = TAB_META.filter(t => editing || count(t.key) > 0 || t.key === 'overview')
  const active = Math.min(tab, Math.max(0, tabs.length - 1))
  const cur = tabs[active]

  const startEdit = () => {
    setDraft({
      ...a,
      features: [...(a.features ?? [])],
      flows: (a.flows ?? []).map(f => ({ ...f, steps: [...(f.steps ?? [])], breaks: [...(f.breaks ?? [])] })),
      screens: [...(a.screens ?? [])],
      openQuestions: [...(a.openQuestions ?? [])],
      mustTest: [...(a.mustTest ?? [])],
      mustNotTest: [...(a.mustNotTest ?? [])],
    })
    setEditing(true)
    setOpen(true)
  }

  const save = async () => {
    if (!onSave) return
    const clean = (xs?: string[]) => (xs ?? []).map(s => s.trim()).filter(Boolean)
    const payload: UiAnalystOutput = {
      authPermissionsSummary: (draft.authPermissionsSummary ?? '').trim(),
      tenantVariantsSummary: (draft.tenantVariantsSummary ?? '').trim(),
      features: (draft.features ?? [])
        .map(f => ({
          id: (f.id ?? '').trim(),
          name: (f.name ?? '').trim(),
          description: (f.description ?? '').trim(),
        }))
        .filter(f => f.name || f.id),
      flows: (draft.flows ?? [])
        .map(f => ({
          name: (f.name ?? '').trim(),
          trigger: (f.trigger ?? '').trim(),
          steps: clean(f.steps),
          breaks: clean(f.breaks),
        }))
        .filter(f => f.name),
      screens: (draft.screens ?? [])
        .map(s => ({
          name: (s.name ?? '').trim(),
          route: (s.route ?? '').trim(),
          components: clean(s.components),
        }))
        .filter(s => s.name),
      mustTest: clean(draft.mustTest),
      mustNotTest: clean(draft.mustNotTest),
      openQuestions: clean(draft.openQuestions),
    }
    setSaving(true)
    try { await onSave(payload); setEditing(false) }
    finally { setSaving(false) }
  }

  const refineCurrent = async () => {
    if (!onRefineSection || !cur) return
    const fb = (tabFeedback[cur.key] ?? '').trim()
    if (!fb) return
    setRefining(true)
    try {
      await onRefineSection(cur.label, fb)
      setTabFeedback(m => ({ ...m, [cur.key]: '' }))
    } finally { setRefining(false) }
  }

  const renderBody = (t: typeof TAB_META[number]): ReactNode => {
    if (t.key === 'overview') {
      if (editing) {
        return (
          <Stack spacing={1.5}>
            <TextField size="small" fullWidth multiline label="Who can use this"
              value={draft.authPermissionsSummary ?? ''}
              onChange={e => setDraft(d => ({ ...d, authPermissionsSummary: e.target.value }))} />
            <TextField size="small" fullWidth multiline label="Product variants (if any)"
              value={draft.tenantVariantsSummary ?? ''}
              onChange={e => setDraft(d => ({ ...d, tenantVariantsSummary: e.target.value }))} />
          </Stack>
        )
      }
      return (
        <Stack spacing={1.5}>
          {view.authPermissionsSummary ? (
            <Box>
              <Typography variant="overline" color="text.secondary" sx={{ lineHeight: 1.4 }}>Who can use this</Typography>
              <Typography variant="body2">{view.authPermissionsSummary}</Typography>
            </Box>
          ) : (
            <Typography variant="body2" color="text.secondary">No access notes yet.</Typography>
          )}
          {view.tenantVariantsSummary ? (
            <Box>
              <Typography variant="overline" color="text.secondary" sx={{ lineHeight: 1.4 }}>Product variants</Typography>
              <Typography variant="body2">{view.tenantVariantsSummary}</Typography>
            </Box>
          ) : null}
        </Stack>
      )
    }

    if (t.key === 'features') {
      if (editing) {
        const items = draft.features ?? []
        return (
          <Stack spacing={1.5}>
            {items.map((f, i) => (
              <Stack key={i} spacing={0.75} sx={{ p: 1, border: 1, borderColor: 'divider', borderRadius: 1 }}>
                <Stack direction="row" spacing={1}>
                  <TextField size="small" label="Short name" value={f.id ?? ''} sx={{ width: 140 }}
                    onChange={e => setDraft(d => {
                      const features = [...(d.features ?? [])]
                      features[i] = { ...features[i], id: e.target.value }
                      return { ...d, features }
                    })} />
                  <TextField size="small" fullWidth label="Title" value={f.name ?? ''}
                    onChange={e => setDraft(d => {
                      const features = [...(d.features ?? [])]
                      features[i] = { ...features[i], name: e.target.value }
                      return { ...d, features }
                    })} />
                  <IconButton size="small" onClick={() => setDraft(d => ({
                    ...d, features: (d.features ?? []).filter((_, j) => j !== i),
                  }))}><CloseIcon fontSize="small" /></IconButton>
                </Stack>
                <TextField size="small" fullWidth multiline label="Description" value={f.description ?? ''}
                  onChange={e => setDraft(d => {
                    const features = [...(d.features ?? [])]
                    features[i] = { ...features[i], description: e.target.value }
                    return { ...d, features }
                  })} />
              </Stack>
            ))}
            <Button size="small" sx={{ alignSelf: 'flex-start', textTransform: 'none' }}
              onClick={() => setDraft(d => ({
                ...d, features: [...(d.features ?? []), { id: '', name: '', description: '' }],
              }))}>+ Add feature</Button>
          </Stack>
        )
      }
      if (!view.features?.length)
        return <Typography variant="body2" color="text.secondary">No features yet.</Typography>
      return (
        <Stack spacing={1}>
          {view.features.map((f, i) => (
            <Box key={f.id ?? i}>
              <Typography variant="body2" sx={{ fontWeight: 600 }}>
                {f.name ?? f.id ?? `Feature ${i + 1}`}
              </Typography>
              {f.description && (
                <Typography variant="body2" color="text.secondary" sx={{ mt: 0.25 }}>
                  {f.description}
                </Typography>
              )}
            </Box>
          ))}
        </Stack>
      )
    }

    if (t.key === 'flows') {
      if (editing) {
        const items = draft.flows ?? []
        return (
          <Stack spacing={1.5}>
            {items.map((f, i) => (
              <Stack key={i} spacing={0.75} sx={{ p: 1, border: 1, borderColor: 'divider', borderRadius: 1 }}>
                <Stack direction="row" spacing={1}>
                  <TextField size="small" fullWidth label="Journey name" value={f.name ?? ''}
                    onChange={e => setDraft(d => {
                      const flows = [...(d.flows ?? [])]
                      flows[i] = { ...flows[i], name: e.target.value }
                      return { ...d, flows }
                    })} />
                  <TextField size="small" fullWidth label="Starts when" value={f.trigger ?? ''}
                    onChange={e => setDraft(d => {
                      const flows = [...(d.flows ?? [])]
                      flows[i] = { ...flows[i], trigger: e.target.value }
                      return { ...d, flows }
                    })} />
                  <IconButton size="small" onClick={() => setDraft(d => ({
                    ...d, flows: (d.flows ?? []).filter((_, j) => j !== i),
                  }))}><CloseIcon fontSize="small" /></IconButton>
                </Stack>
                <TextField size="small" fullWidth multiline label="Steps (one per line)"
                  value={(f.steps ?? []).join('\n')}
                  onChange={e => setDraft(d => {
                    const flows = [...(d.flows ?? [])]
                    flows[i] = { ...flows[i], steps: e.target.value.split('\n') }
                    return { ...d, flows }
                  })} />
              </Stack>
            ))}
            <Button size="small" sx={{ alignSelf: 'flex-start', textTransform: 'none' }}
              onClick={() => setDraft(d => ({
                ...d, flows: [...(d.flows ?? []), { name: '', trigger: '', steps: [], breaks: [] }],
              }))}>+ Add journey</Button>
          </Stack>
        )
      }
      if (!view.flows?.length)
        return <Typography variant="body2" color="text.secondary">No user journeys yet.</Typography>
      return (
        <Stack spacing={1.5}>
          {view.flows.map((f, i) => (
            <Box key={i}>
              <Typography variant="body2" sx={{ fontWeight: 600 }}>
                {f.name}{f.trigger ? ` — starts when: ${f.trigger}` : ''}
              </Typography>
              {(f.steps?.length ?? 0) > 0 && (
                <Stack component="ul" spacing={0.25} sx={{ m: 0, pl: 2.5, mt: 0.25 }}>
                  {f.steps!.map((s, j) => (
                    <Box component="li" key={j} sx={{ '&::marker': { color: 'text.disabled' } }}>
                      <Typography variant="body2">{s}</Typography>
                    </Box>
                  ))}
                </Stack>
              )}
            </Box>
          ))}
        </Stack>
      )
    }

    if (t.key === 'screens') {
      if (editing) {
        const items = draft.screens ?? []
        return (
          <Stack spacing={1}>
            {items.map((s, i) => (
              <Stack key={i} direction="row" spacing={1} sx={{ alignItems: 'flex-start' }}>
                <TextField size="small" fullWidth label="Page name" value={s.name ?? ''}
                  onChange={e => setDraft(d => {
                    const screens = [...(d.screens ?? [])]
                    screens[i] = { ...screens[i], name: e.target.value }
                    return { ...d, screens }
                  })} />
                <TextField size="small" fullWidth label="Path (optional)" value={s.route ?? ''}
                  onChange={e => setDraft(d => {
                    const screens = [...(d.screens ?? [])]
                    screens[i] = { ...screens[i], route: e.target.value }
                    return { ...d, screens }
                  })} />
                <IconButton size="small" onClick={() => setDraft(d => ({
                  ...d, screens: (d.screens ?? []).filter((_, j) => j !== i),
                }))}><CloseIcon fontSize="small" /></IconButton>
              </Stack>
            ))}
            <Button size="small" sx={{ alignSelf: 'flex-start', textTransform: 'none' }}
              onClick={() => setDraft(d => ({
                ...d, screens: [...(d.screens ?? []), { name: '', route: '', components: [] }],
              }))}>+ Add page</Button>
          </Stack>
        )
      }
      if (!view.screens?.length)
        return <Typography variant="body2" color="text.secondary">No pages yet.</Typography>
      return (
        <Stack spacing={0.75}>
          {view.screens.map((s, i) => (
            <Box key={i}>
              <Typography variant="body2" sx={{ fontWeight: 500 }}>{s.name}</Typography>
              {s.route && (
                <Typography variant="caption" color="text.secondary">{s.route}</Typography>
              )}
            </Box>
          ))}
        </Stack>
      )
    }

    if (t.key === 'mustTest') {
      return editing
        ? <StringListEdit items={draft.mustTest ?? []} onChange={mustTest => setDraft(d => ({ ...d, mustTest }))} />
        : <StringListRead items={view.mustTest ?? []} />
    }
    if (t.key === 'mustNotTest') {
      return editing
        ? <StringListEdit items={draft.mustNotTest ?? []} onChange={mustNotTest => setDraft(d => ({ ...d, mustNotTest }))} />
        : <StringListRead items={view.mustNotTest ?? []} />
    }
    // questions
    if (editing) {
      return (
        <StringListEdit items={draft.openQuestions ?? []}
          onChange={openQuestions => setDraft(d => ({ ...d, openQuestions }))}
          placeholder="Open question…" />
      )
    }
    if (!view.openQuestions?.length)
      return <Typography variant="body2" color="text.secondary">No open questions.</Typography>
    return (
      <Alert severity="warning">
        {view.openQuestions.map((q, i) => <div key={i}>{q}</div>)}
      </Alert>
    )
  }

  const fb = cur ? (tabFeedback[cur.key] ?? '') : ''

  return (
    <Card sx={{ mb: 1 }}>
      <CardContent>
        <Stack direction="row" sx={{ alignItems: 'center' }}>
          <PsychologyIcon fontSize="small" color="primary" sx={{ mr: 1 }} />
          <Typography variant="subtitle1" sx={{ flex: 1, cursor: 'pointer' }}
            onClick={() => !editing && setOpen(v => !v)}>
            Analyst findings
          </Typography>
          {editing ? (
            <Stack direction="row" spacing={0.5}>
              <Button size="small" variant="contained" startIcon={<SaveIcon />} onClick={save} disabled={saving}>
                {saving ? 'Saving…' : 'Save'}
              </Button>
              <Button size="small" color="inherit" startIcon={<CloseIcon />}
                onClick={() => setEditing(false)} disabled={saving}>Cancel</Button>
            </Stack>
          ) : (
            <>
              {onSave && (
                <Tooltip title="Edit analysis">
                  <IconButton size="small" onClick={startEdit}><EditIcon fontSize="small" /></IconButton>
                </Tooltip>
              )}
              <IconButton size="small" onClick={() => setOpen(v => !v)}>
                {open ? <ExpandLessIcon /> : <ExpandMoreIcon />}
              </IconButton>
            </>
          )}
        </Stack>

        <Collapse in={open || editing}>
          {tabs.length > 0 && (
            <Box sx={{ mt: 1 }}>
              <Tabs value={active} onChange={(_e, v) => setTab(v)} variant="scrollable" scrollButtons="auto"
                sx={{
                  borderBottom: 1, borderColor: 'divider', minHeight: 36,
                  '& .MuiTab-root': { minHeight: 36, py: 0.5, textTransform: 'none' },
                }}>
                {tabs.map(t => {
                  const n = count(t.key)
                  const label = t.key === 'overview' ? t.label : `${t.label}${n ? ` (${n})` : ''}`
                  return <Tab key={t.key} label={label} />
                })}
              </Tabs>
              <Box sx={{ pt: 1.5 }}>{cur && renderBody(cur)}</Box>
            </Box>
          )}

          {!editing && onRefineSection && cur && (
            <Stack spacing={1} sx={{ mt: 2, pt: 1.5, borderTop: 1, borderColor: 'divider' }}>
              <Typography variant="subtitle2">Improve {cur.label.toLowerCase()}</Typography>
              <Typography variant="caption" color="text.secondary">
                Missing something, or something wrong? Describe it — the analyst updates this section.
              </Typography>
              <TextField size="small" fullWidth multiline minRows={2}
                placeholder="e.g. add the cancel path; drop the admin-only case…"
                value={fb}
                onChange={e => setTabFeedback(m => ({ ...m, [cur.key]: e.target.value }))}
                disabled={refining} />
              <Button variant="outlined" size="small" sx={{ alignSelf: 'flex-start' }}
                disabled={refining || !fb.trim()}
                startIcon={refining ? <CircularProgress size={14} /> : <PsychologyIcon />}
                onClick={refineCurrent}>
                {refining ? 'Updating…' : 'Send feedback'}
              </Button>
            </Stack>
          )}

          {editing && (
            <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 1 }}>
              Edits are saved on this session and used the next time cases are authored.
            </Typography>
          )}
        </Collapse>
      </CardContent>
    </Card>
  )
}
