import { FormControl, MenuItem, Select, type SelectChangeEvent } from '@mui/material'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { getProvider, setProvider } from '../api/settings'

// The active LLM provider is server state, so it lives in React Query (F3) rather than Redux:
// one ['provider'] cache entry, shared with anything else that reads it, and a mutation that
// invalidates it on switch so the UI reflects the backend without manual state juggling.
export default function ProviderSwitcher() {
  const qc = useQueryClient()
  const { data: info } = useQuery({ queryKey: ['provider'], queryFn: getProvider })
  const change = useMutation({
    mutationFn: setProvider,
    onSuccess: () => qc.invalidateQueries({ queryKey: ['provider'] }),
  })

  if (!info) return null

  return (
    <FormControl size="small" sx={{ minWidth: 200 }}>
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
  )
}
