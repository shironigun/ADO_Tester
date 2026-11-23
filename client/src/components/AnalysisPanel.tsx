import { useEffect, useMemo, useState } from 'react'
import type { ReactNode } from 'react'
import {
  Box, Button, Card, CardContent, Chip, Collapse, IconButton, Stack, Tab, Tabs, TextField, Tooltip, Typography,
} from '@mui/material'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import ExpandLessIcon from '@mui/icons-material/ExpandLess'
import PsychologyIcon from '@mui/icons-material/Psychology'
import EditIcon from '@mui/icons-material/Edit'
import SaveIcon from '@mui/icons-material/Save'
import CloseIcon from '@mui/icons-material/Close'

// The Analyst output shape (camelCase, as stored in analysisJson).
export interface Analysis {
  module?: string
  feature?: string
  summary?: string
  happyPath?: string
  acceptanceCriteria?: string[]
  businessRules?: string[]
  edgeCases?: string[]
  negativeCases?: string[]
  permissions?: string[]
  crossFlow?: string[]
  openQuestions?: string[]
  /** Answers the QA supplies to the open questions, aligned by index to openQuestions. */
  questionAnswers?: string[]
}

type ListField = 'acceptanceCriteria' | 'businessRules' | 'edgeCases' | 'negativeCases' | 'permissions' | 'crossFlow' | 'openQuestions'

const LIST_DEFS: { field: ListField; label: string }[] = [
  { field: 'acceptanceCriteria', label: 'Acceptance' },
  { field: 'businessRules', label: 'Rules' },
  { field: 'edgeCases', label: 'Edge' },
  { field: 'negativeCases', label: 'Negative' },
  { field: 'permissions', label: 'Permissions' },
  { field: 'crossFlow', label: 'Cross-flow' },
]

export function parseAnalysis(json: string | null | undefined): Analysis | null {
  if (!json) return null
  try {
    const o = JSON.parse(json)
    return typeof o === 'object' && o ? (o as Analysis) : null
  } catch {
    return null
  }
}

type Tag = { kind: 'src' | 'ticket' | 'assumed' | 'unclear'; label: string }

function splitTag(item: string): { text: string; tag: Tag | null } {
  const m = item.match(/\s*\[(src:\s*[^\]]+|ticket|assumed|unclear)\]\s*$/i)
  if (!m) return { text: item.trim(), tag: null }
  const raw = m[1].trim()
  const text = item.slice(0, m.index).trim()
  const low = raw.toLowerCase()
  if (low.startsWith('src:')) return { text, tag: { kind: 'src', label: raw.slice(4).trim() } }
  if (low === 'ticket') return { text, tag: { kind: 'ticket', label: 'ticket' } }
  if (low === 'assumed') return { text, tag: { kind: 'assumed', label: 'assumed' } }
  return { text, tag: { kind: 'unclear', label: 'unclear' } }
}

function TagChip({ tag }: { tag: Tag }) {
  const map = {
    src: { color: 'info' as const, tip: 'Grounded in a knowledgebase document' },
    ticket: { color: 'success' as const, tip: 'Stated directly in the ticket' },
    assumed: { color: 'warning' as const, tip: 'Inferred by the Analyst — verify before trusting' },
    unclear: { color: 'error' as const, tip: 'Ambiguous in the ticket/docs — needs clarification' },
  }
  const cfg = map[tag.kind]
  return (
    <Tooltip title={cfg.tip}>
      <Chip size="small" variant="outlined" color={cfg.color} label={tag.label}
        sx={{ height: 18, ml: 0.5, '& .MuiChip-label': { px: 0.75, fontSize: 11 } }} />
    </Tooltip>
  )
}

function ReadList({ items, warn }: { items: string[]; warn?: boolean }) {
  return (
    <Stack component="ul" spacing={0.75} sx={{ m: 0, pl: 2.5 }}>
      {items.map((raw, i) => {
        const { text, tag } = splitTag(raw)
        return (
          <Box component="li" key={i} sx={{ '&::marker': { color: 'text.disabled' } }}>
            <Typography variant="body2" component="span" color={warn ? 'warning.main' : undefined}>{text}</Typography>
            {tag && <TagChip tag={tag} />}
          </Box>
        )
      })}
    </Stack>
  )
}

function EditList({ items, onChange }: { items: string[]; onChange: (next: string[]) => void }) {
  const set = (i: number, v: string) => onChange(items.map((x, j) => (j === i ? v : x)))
  const remove = (i: number) => onChange(items.filter((_, j) => j !== i))
  const add = () => onChange([...items, ''])
  return (
    <Stack spacing={1}>
      {items.map((it, i) => (
        <Stack key={i} direction="row" spacing={0.5} sx={{ alignItems: 'flex-start' }}>
          <TextField size="small" fullWidth multiline value={it} onChange={e => set(i, e.target.value)} />
          <IconButton size="small" onClick={() => remove(i)} sx={{ mt: 0.5 }} aria-label="remove"><CloseIcon fontSize="small" /></IconButton>
        </Stack>
      ))}
      <Button size="small" onClick={add} sx={{ alignSelf: 'flex-start', textTransform: 'none' }}>+ Add item</Button>
    </Stack>
  )
}

/** Open questions are ANSWERED, not edited. Each question is read-only; in edit mode a text field
 * captures the answer (aligned by index), in view mode the answer is shown beneath the question. */
function QuestionList({ questions, answers, editing, onAnswers }: {
  questions: string[]; answers: string[]; editing: boolean; onAnswers: (next: string[]) => void
}) {
  const setAns = (i: number, v: string) => {
    const next = questions.map((_, j) => answers[j] ?? '')
    next[i] = v
    onAnswers(next)
  }
  if (questions.length === 0) {
    return <Typography variant="body2" color="text.secondary">No open questions.</Typography>
  }
  return (
    <Stack spacing={1.5}>
      {questions.map((q, i) => {
        const { text } = splitTag(q)
        const ans = (answers[i] ?? '').trim()
        return (
          <Box key={i}>
            <Stack direction="row" spacing={1} sx={{ alignItems: 'flex-start' }}>
              <Box sx={{ mt: '7px', width: 6, height: 6, borderRadius: '50%', bgcolor: 'warning.main', flexShrink: 0 }} />
              <Typography variant="body2" sx={{ flex: 1, fontWeight: 500 }}>{text}</Typography>
            </Stack>
            {editing ? (
              <TextField size="small" fullWidth multiline placeholder="Answer this question…"
                value={answers[i] ?? ''} onChange={e => setAns(i, e.target.value)} sx={{ mt: 0.5, ml: 2 }} />
            ) : ans ? (
              <Box sx={{ ml: 2, mt: 0.5, pl: 1, borderLeft: 2, borderColor: 'success.main' }}>
                <Typography variant="caption" color="success.main" sx={{ fontWeight: 700, display: 'block', textTransform: 'uppercase' }}>Answer</Typography>
                <Typography variant="body2" sx={{ whiteSpace: 'pre-wrap' }}>{ans}</Typography>
              </Box>
            ) : (
              <Typography variant="caption" color="text.secondary" sx={{ ml: 2, fontStyle: 'italic' }}>Unanswered</Typography>
            )}
          </Box>
        )
      })}
    </Stack>
  )
}

interface Props {
  analysisJson: string | null | undefined
  /** If provided, an Edit affordance appears; saving calls this with the edited analysis. */
  onSave?: (a: Analysis) => Promise<void>
  defaultExpanded?: boolean
}

export default function AnalysisPanel({ analysisJson, onSave, defaultExpanded = true }: Props) {
  const a = useMemo(() => parseAnalysis(analysisJson), [analysisJson])
  const [open, setOpen] = useState(defaultExpanded)
  const [tab, setTab] = useState(0)
  const [editing, setEditing] = useState(false)
  const [draft, setDraft] = useState<Analysis>({})
  const [saving, setSaving] = useState(false)

  // If the underlying analysis changes (e.g. re-run) while not editing, nothing to sync.
  useEffect(() => { if (!editing) setDraft(a ?? {}) }, [a, editing])

  const sources = useMemo(() => {
    if (!a) return []
    const all = [
      ...(a.acceptanceCriteria ?? []), ...(a.businessRules ?? []), ...(a.edgeCases ?? []),
      ...(a.negativeCases ?? []), ...(a.permissions ?? []), ...(a.crossFlow ?? []),
    ]
    const set = new Set<string>()
    for (const it of all) {
      const { tag } = splitTag(it)
      if (tag?.kind === 'src') set.add(tag.label)
    }
    return [...set]
  }, [a])

  if (!a) return null
  const view: Analysis = editing ? draft : a

  const setField = (k: keyof Analysis, v: string) => setDraft(d => ({ ...d, [k]: v }))
  const setList = (f: ListField, items: string[]) => setDraft(d => ({ ...d, [f]: items }))

  const startEdit = () => {
    setDraft({ ...a, acceptanceCriteria: a.acceptanceCriteria ?? [], businessRules: a.businessRules ?? [], edgeCases: a.edgeCases ?? [], negativeCases: a.negativeCases ?? [], permissions: a.permissions ?? [], crossFlow: a.crossFlow ?? [], openQuestions: a.openQuestions ?? [], questionAnswers: a.questionAnswers ?? [] })
    setEditing(true)
    setOpen(true)
  }
  const cancel = () => setEditing(false)
  const save = async () => {
    if (!onSave) return
    const clean = (xs?: string[]) => (xs ?? []).map(s => s.trim()).filter(Boolean)
    // Questions are read-only (answered, not edited): keep each question paired with its answer so
    // the parallel arrays stay aligned by index even if a question were ever blank.
    const qPairs = (draft.openQuestions ?? [])
      .map((q, i) => [(q ?? '').trim(), (draft.questionAnswers?.[i] ?? '').trim()] as const)
      .filter(([q]) => q)
    const payload: Analysis = {
      module: (draft.module ?? '').trim(), feature: (draft.feature ?? '').trim(),
      summary: (draft.summary ?? '').trim(), happyPath: (draft.happyPath ?? '').trim(),
      acceptanceCriteria: clean(draft.acceptanceCriteria), businessRules: clean(draft.businessRules),
      edgeCases: clean(draft.edgeCases), negativeCases: clean(draft.negativeCases),
      permissions: clean(draft.permissions), crossFlow: clean(draft.crossFlow),
      openQuestions: qPairs.map(p => p[0]),
      questionAnswers: qPairs.map(p => p[1]),
    }
    setSaving(true)
    try { await onSave(payload); setEditing(false) }
    finally { setSaving(false) }
  }

  // Build the tab set. In edit mode show every group (so empty ones can be filled); hide Sources.
  type TabDef = { key: string; label: string; kind: 'overview' | 'list' | 'questions' | 'sources'; field?: ListField }
  const tabs: TabDef[] = []
  if (editing || view.summary || view.happyPath || view.module || view.feature)
    tabs.push({ key: 'overview', label: 'Overview', kind: 'overview' })
  for (const d of LIST_DEFS) {
    const items = view[d.field] as string[] | undefined
    if (editing || (items && items.length))
      tabs.push({ key: d.field, label: `${d.label}${items?.length ? ` (${items.length})` : ''}`, kind: 'list', field: d.field })
  }
  const oq = view.openQuestions
  if (editing || (oq && oq.length))
    tabs.push({ key: 'questions', label: `Questions${oq?.length ? ` (${oq.length})` : ''}`, kind: 'questions' })
  if (!editing && sources.length)
    tabs.push({ key: 'sources', label: `Sources (${sources.length})`, kind: 'sources' })

  const active = Math.min(tab, Math.max(0, tabs.length - 1))
  const cur = tabs[active]

  const renderBody = (t: TabDef): ReactNode => {
    if (t.kind === 'overview') {
      if (editing) return (
        <Stack spacing={1.5}>
          <Stack direction="row" spacing={1.5}>
            <TextField size="small" fullWidth label="Module" value={draft.module ?? ''} onChange={e => setField('module', e.target.value)} />
            <TextField size="small" fullWidth label="Feature" value={draft.feature ?? ''} onChange={e => setField('feature', e.target.value)} />
          </Stack>
          <TextField size="small" fullWidth multiline label="Summary" value={draft.summary ?? ''} onChange={e => setField('summary', e.target.value)} />
          <TextField size="small" fullWidth multiline label="Happy path" value={draft.happyPath ?? ''} onChange={e => setField('happyPath', e.target.value)} />
        </Stack>
      )
      return (
        <Stack spacing={1.5}>
          {a.summary && <Typography variant="body2">{a.summary}</Typography>}
          {a.happyPath && (
            <Box>
              <Typography variant="overline" color="text.secondary" sx={{ lineHeight: 1.4 }}>Happy path</Typography>
              <Typography variant="body2">{a.happyPath}</Typography>
            </Box>
          )}
        </Stack>
      )
    }
    if (t.kind === 'list' && t.field) {
      return editing
        ? <EditList items={(draft[t.field] as string[]) ?? []} onChange={items => setList(t.field!, items)} />
        : <ReadList items={(a[t.field] as string[]) ?? []} />
    }
    if (t.kind === 'questions') {
      return (
        <QuestionList
          questions={(editing ? draft.openQuestions : a.openQuestions) ?? []}
          answers={(editing ? draft.questionAnswers : a.questionAnswers) ?? []}
          editing={editing}
          onAnswers={ans => setDraft(d => ({ ...d, questionAnswers: ans }))}
        />
      )
    }
    // sources
    return (
      <Stack direction="row" spacing={0.5} sx={{ flexWrap: 'wrap', gap: 0.5 }}>
        {sources.map(s => <Chip key={s} size="small" variant="outlined" color="info" label={s} />)}
      </Stack>
    )
  }

  return (
    <Card sx={{ mb: 2 }}>
      <CardContent>
        <Stack direction="row" sx={{ alignItems: 'center' }}>
          <PsychologyIcon fontSize="small" color="primary" sx={{ mr: 1 }} />
          <Typography variant="subtitle1" sx={{ flex: 1, cursor: 'pointer' }} onClick={() => !editing && setOpen(v => !v)}>
            Analyst findings
          </Typography>
          {!editing && a.module && <Chip size="small" label={a.module} sx={{ mr: 0.5 }} />}
          {!editing && a.feature && <Chip size="small" variant="outlined" label={a.feature} sx={{ mr: 1, maxWidth: 320 }} />}
          {editing ? (
            <Stack direction="row" spacing={0.5}>
              <Button size="small" variant="contained" startIcon={<SaveIcon />} onClick={save} disabled={saving}>
                {saving ? 'Saving…' : 'Save'}
              </Button>
              <Button size="small" color="inherit" startIcon={<CloseIcon />} onClick={cancel} disabled={saving}>Cancel</Button>
            </Stack>
          ) : (
            <>
              {onSave && <Tooltip title="Edit analysis"><IconButton size="small" onClick={startEdit}><EditIcon fontSize="small" /></IconButton></Tooltip>}
              <IconButton size="small" onClick={() => setOpen(v => !v)}>{open ? <ExpandLessIcon /> : <ExpandMoreIcon />}</IconButton>
            </>
          )}
        </Stack>

        <Collapse in={open || editing}>
          {tabs.length > 0 && (
            <Box sx={{ mt: 1 }}>
              <Tabs value={active} onChange={(_e, v) => setTab(v)} variant="scrollable" scrollButtons="auto"
                sx={{ borderBottom: 1, borderColor: 'divider', minHeight: 36, '& .MuiTab-root': { minHeight: 36, py: 0.5, textTransform: 'none' } }}>
                {tabs.map(t => <Tab key={t.key} label={t.label} />)}
              </Tabs>
              <Box sx={{ pt: 1.5 }}>{cur && renderBody(cur)}</Box>
            </Box>
          )}
          {editing && (
            <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 1 }}>
              Edits — and answers you give to the open Questions — are saved to this session and used the
              next time the agent runs. Keep the trailing tag (e.g. [src: file.md], [ASSUMED]) on an item
              to preserve its evidence label.
            </Typography>
          )}
        </Collapse>
      </CardContent>
    </Card>
  )
}
