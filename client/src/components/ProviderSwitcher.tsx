import { useEffect, useState } from 'react'
import { FormControl, MenuItem, Select, type SelectChangeEvent } from '@mui/material'
import axios from 'axios'

interface ProviderInfo { current: string; available: string[] }

export default function ProviderSwitcher() {
  const [info, setInfo] = useState<ProviderInfo | null>(null)
  const [saving, setSaving] = useState(false)

  useEffect(() => {
    axios.get<ProviderInfo>('/api/settings/provider').then(r => setInfo(r.data)).catch(() => {})
  }, [])

  async function change(e: SelectChangeEvent) {
    const provider = e.target.value
    setSaving(true)
    try {
      await axios.put('/api/settings/provider', { provider })
      setInfo(prev => prev ? { ...prev, current: provider } : prev)
    } finally { setSaving(false) }
  }

  if (!info) return null

  return (
    <FormControl size="small" sx={{ minWidth: 200 }}>
      <Select
        value={info.current}
        onChange={change}
        disabled={saving}
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
