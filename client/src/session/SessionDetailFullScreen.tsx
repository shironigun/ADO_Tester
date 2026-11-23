import { useEffect, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import {
  Alert, AppBar, Box, Button, Container, Snackbar, Stack, Tab, Tabs,
  Toolbar, Typography,
} from '@mui/material'
import ArrowBackIcon from '@mui/icons-material/ArrowBack'
import StatusChip from '../components/StatusChip'
import { ThinkingShimmer } from '../components/LoadingSkeleton'
import { axiosMessage, sessionIdentity, SessionTimeline, useSessionDetail } from './shared'
import type { SessionModuleConfig } from './types'

export default function SessionDetailFullScreen({ config }: { config: SessionModuleConfig }) {
  const { sessionId } = useParams()
  const navigate = useNavigate()
  const id = sessionId ? Number(sessionId) : null
  const { session, polling, refetch } = useSessionDetail(config, id)
  const [tab, setTab] = useState<'detail' | 'timeline'>('detail')
  const [actionError, setActionError] = useState('')

  useEffect(() => { setTab('detail') }, [id])

  const onError = (e: unknown, fallback: string) => setActionError(`${fallback}: ${axiosMessage(e)}`)
  const goBack = () => {
    if (window.history.length > 1) navigate(-1)
    else navigate(config.route)
  }
  const live = polling || (session != null && config.inFlightStatuses.includes(session.status))
  const Body = config.DetailBody

  return (
    <Box sx={{ minHeight: '100vh', bgcolor: 'background.default' }}>
      <AppBar position="sticky" color="default" elevation={0} sx={{ borderBottom: 1, borderColor: 'divider' }}>
        <Toolbar>
          <Button startIcon={<ArrowBackIcon />} onClick={goBack} sx={{ textTransform: 'none', mr: 2 }}>Back</Button>
          {session && (
            <>
              <Typography variant="subtitle1" sx={{ fontWeight: 600, flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                {sessionIdentity(config, session)}
              </Typography>
              <StatusChip value={session.status} />
            </>
          )}
        </Toolbar>
        <Tabs value={tab} onChange={(_, v) => setTab(v)} sx={{ px: 2 }}>
          <Tab value="detail" label="Detail" sx={{ textTransform: 'none' }} />
          <Tab value="timeline" label="Timeline" sx={{ textTransform: 'none' }} />
        </Tabs>
      </AppBar>

      <Container maxWidth={false} sx={{ py: 3, px: { xs: 2, sm: 3, md: 4 }, maxWidth: '100%' }}>
        {!session ? (
          <ThinkingShimmer label="Loading session…" />
        ) : tab === 'detail' ? (
          <Body key={session.id} session={session} refetch={refetch} onError={onError} fullScreen />
        ) : (
          <SessionTimeline config={config} session={session} live={!!live} />
        )}
      </Container>

      <Snackbar open={!!actionError} autoHideDuration={6000} onClose={() => setActionError('')} anchorOrigin={{ vertical: 'bottom', horizontal: 'right' }}>
        <Alert severity="error" onClose={() => setActionError('')} variant="filled">{actionError}</Alert>
      </Snackbar>

      {!session && (
        <Box sx={{ position: 'fixed', bottom: 16, left: 16 }}>
          <Stack direction="row" spacing={1}><Typography variant="caption" color="text.secondary">Session #{id}</Typography></Stack>
        </Box>
      )}
    </Box>
  )
}
