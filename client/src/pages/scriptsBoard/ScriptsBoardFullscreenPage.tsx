import { Box, Button, Stack, Typography } from '@mui/material'
import CloseFullscreenIcon from '@mui/icons-material/CloseFullscreen'
import { useNavigate, useSearchParams } from 'react-router-dom'
import { paths } from '../../routes/paths'
import ScriptsBoardPage from './ScriptsBoardPage'

/** Isolated Scripts Board popup — no app chrome (sidebar / AppBar). */
export default function ScriptsBoardFullscreenPage() {
  const [params] = useSearchParams()
  const navigate = useNavigate()
  const initialModule = params.get('module') || undefined
  const runRaw = params.get('runId')
  const initialRunId = runRaw && /^\d+$/.test(runRaw) ? Number(runRaw) : undefined

  function exit() {
    // Popup opened via window.open → close; otherwise return to in-app board.
    if (window.opener) {
      window.close()
      return
    }
    navigate(paths.scriptsBoard)
  }

  return (
    <Box sx={{
      minHeight: '100vh', height: '100vh', display: 'flex', flexDirection: 'column',
      bgcolor: 'background.default', p: { xs: 1.5, md: 2 }, boxSizing: 'border-box',
    }}>
      <Stack direction="row" spacing={1.5}
        sx={{ alignItems: 'center', mb: 1.5, flexShrink: 0 }}>
        <Typography variant="h6" sx={{ flex: 1 }}>Scripts Board</Typography>
        <Button size="small" variant="outlined" startIcon={<CloseFullscreenIcon />} onClick={exit}>
          Exit fullscreen
        </Button>
      </Stack>
      <Box sx={{ flex: 1, minHeight: 0 }}>
        <ScriptsBoardPage
          fullscreen
          initialModule={initialModule}
          initialRunId={initialRunId}
        />
      </Box>
    </Box>
  )
}
