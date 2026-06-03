import { useState } from 'react'
import {
  Box, Button, Card, CardContent, Chip, CircularProgress,
  Stack, TextField, Typography,
} from '@mui/material'
import CheckCircleIcon from '@mui/icons-material/CheckCircle'
import ErrorIcon from '@mui/icons-material/Error'
import axios from 'axios'

interface EnvStatus {
  activeProvider: string
  adoPatPresent: boolean
  anthropicKeyPresent: boolean
  azureOpenAiConfigured: boolean
}
interface WorkItem {
  id: number; title: string; workItemType: string; state: string
  assignedTo?: string; description?: string; acceptanceCriteria?: string
}

export default function DiagnosticsPage() {
  const [env, setEnv] = useState<EnvStatus | null>(null)
  const [envLoading, setEnvLoading] = useState(false)

  const [wiId, setWiId] = useState('')
  const [workItem, setWorkItem] = useState<WorkItem | null>(null)
  const [wiLoading, setWiLoading] = useState(false)
  const [wiError, setWiError] = useState('')

  const [llmReply, setLlmReply] = useState('')
  const [llmLoading, setLlmLoading] = useState(false)
  const [llmError, setLlmError] = useState('')

  async function checkEnv() {
    setEnvLoading(true)
    const { data } = await axios.get<EnvStatus>('/api/diagnostics')
    setEnv(data); setEnvLoading(false)
  }

  async function readWorkItem() {
    setWiError(''); setWorkItem(null); setWiLoading(true)
    try {
      const { data } = await axios.get<WorkItem>(`/api/diagnostics/workitem/${wiId}`)
      setWorkItem(data)
    } catch (e: unknown) {
      setWiError(axios.isAxiosError(e) ? String(e.response?.data ?? e.message) : String(e))
    } finally { setWiLoading(false) }
  }

  async function pingLlm() {
    setLlmError(''); setLlmReply(''); setLlmLoading(true)
    try {
      const { data } = await axios.get<{ provider: string; reply: string }>('/api/diagnostics/llm')
      setLlmReply(`[${data.provider}] ${data.reply}`)
    } catch (e: unknown) {
      setLlmError(axios.isAxiosError(e) ? String(e.response?.data ?? e.message) : String(e))
    } finally { setLlmLoading(false) }
  }

  const StatusChip = ({ ok }: { ok: boolean }) =>
    ok ? <Chip icon={<CheckCircleIcon />} label="Present" color="success" size="small" />
       : <Chip icon={<ErrorIcon />}       label="MISSING"  color="error"   size="small" />

  return (
    <Box>
      <Typography variant="h5" gutterBottom>S0 Diagnostics</Typography>
      <Stack spacing={3} sx={{ maxWidth: 700 }}>

        {/* Env */}
        <Card variant="outlined">
          <CardContent>
            <Typography variant="h6" gutterBottom>Environment Variables</Typography>
            <Button variant="outlined" onClick={checkEnv} disabled={envLoading}>
              {envLoading ? <CircularProgress size={18} /> : 'Check'}
            </Button>
            {env && (
              <Stack direction="row" spacing={2} sx={{ mt: 2, flexWrap: 'wrap', rowGap: 1 }}>
                <Box><Typography variant="body2">Active provider</Typography><Chip label={env.activeProvider} color="primary" size="small" /></Box>
                <Box><Typography variant="body2">ADOACCESS_PAT</Typography><StatusChip ok={env.adoPatPresent} /></Box>
                <Box><Typography variant="body2">ANTHROPIC_API_KEY</Typography><StatusChip ok={env.anthropicKeyPresent} /></Box>
                <Box><Typography variant="body2">Azure OpenAI</Typography><StatusChip ok={env.azureOpenAiConfigured} /></Box>
              </Stack>
            )}
          </CardContent>
        </Card>

        {/* ADO */}
        <Card variant="outlined">
          <CardContent>
            <Typography variant="h6" gutterBottom>Azure DevOps — Read Work Item</Typography>
            <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
              <TextField label="Work item ID" size="small" value={wiId}
                onChange={e => setWiId(e.target.value)} type="number" />
              <Button variant="outlined" onClick={readWorkItem} disabled={wiLoading || !wiId}>
                {wiLoading ? <CircularProgress size={18} /> : 'Read'}
              </Button>
            </Stack>
            {wiError && <Typography color="error" sx={{ mt: 1 }}>{wiError}</Typography>}
            {workItem && (
              <Box component="dl" sx={{ display: 'grid', gridTemplateColumns: '160px 1fr', gap: 0.5, mt: 2 }}>
                {([
                  ['ID', workItem.id],
                  ['Type', workItem.workItemType],
                  ['Title', workItem.title],
                  ['State', workItem.state],
                  ['Assigned to', workItem.assignedTo ?? '—'],
                ] as [string, unknown][]).map(([k, v]) => (
                  <>
                    <Typography key={`k-${k}`} component="dt" variant="body2" sx={{ fontWeight: 600 }}>{k}</Typography>
                    <Typography key={`v-${k}`} component="dd" variant="body2">{String(v ?? '')}</Typography>
                  </>
                ))}
              </Box>
            )}
          </CardContent>
        </Card>

        {/* LLM */}
        <Card variant="outlined">
          <CardContent>
            <Typography variant="h6" gutterBottom>LLM — Ping (active provider)</Typography>
            <Button variant="outlined" onClick={pingLlm} disabled={llmLoading}>
              {llmLoading ? <CircularProgress size={18} /> : 'Ping'}
            </Button>
            {llmError && <Typography color="error" sx={{ mt: 1 }}>{llmError}</Typography>}
            {llmReply && <Typography sx={{ mt: 1 }}>Reply: <strong>{llmReply}</strong></Typography>}
          </CardContent>
        </Card>

      </Stack>
    </Box>
  )
}
