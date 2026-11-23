import { Chip, FormControl, MenuItem, Select, Stack, Tooltip, type SelectChangeEvent } from '@mui/material'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { getProvider, setProvider } from '../api/settings'

// The active LLM provider is server state, so it lives in React Query (F3) rather than Redux:
// one ['provider'] cache entry, shared with anything else that reads it, and a mutation that
// invalidates it on switch so the UI reflects the backend without manual state juggling.
// Model / effort / context chips are display-only — edit them in Settings → LLM Providers.
export default function ProviderSwitcher() {
  const qc = useQueryClient()
  const { data: info } = useQuery({ queryKey: ['provider'], queryFn: getProvider })
  const change = useMutation({
    mutationFn: setProvider,
    onSuccess: () => qc.invalidateQueries({ queryKey: ['provider'] }),
  })

  if (!info) return null

  const chips = [
    info.active?.model ? { key: 'model', label: info.active.model } : null,
    info.active?.effort ? { key: 'effort', label: info.active.effort } : null,
    info.active?.context ? { key: 'context', label: info.active.context } : null,
  ].filter(Boolean) as { key: string; label: string }[]

  return (
    <Stack direction="row" spacing={1} sx={{ minWidth: 0, alignItems: 'center' }}>
      <FormControl size="small" sx={{ minWidth: 160 }}>
        <Select
          value={info.current}
          onChange={(e: SelectChangeEvent) => change.mutate(e.target.value)}
          disabled={change.isPending}
          sx={{
            color: 'inherit',
            '.MuiOutlinedInput-notchedOutline': { borderColor: 'divider' },
            '&:hover .MuiOutlinedInput-notchedOutline': { borderColor: 'text.secondary' },
            '.MuiSvgIcon-root': { color: 'inherit' },
          }}
        >
          {info.available.map(p => <MenuItem key={p} value={p}>{p}</MenuItem>)}
        </Select>
      </FormControl>
      {chips.length > 0 && (
        <Tooltip title="Change model / effort / context in Settings → LLM Providers">
          <Stack direction="row" spacing={0.5} sx={{ maxWidth: 360, overflow: 'hidden' }}>
            {chips.map(c => (
              <Chip
                key={c.key}
                size="small"
                label={c.label}
                variant="outlined"
                sx={{
                  color: 'inherit',
                  borderColor: 'divider',
                  height: 24,
                  '& .MuiChip-label': { px: 0.75, fontSize: 12 },
                }}
              />
            ))}
          </Stack>
        </Tooltip>
      )}
    </Stack>
  )
}
