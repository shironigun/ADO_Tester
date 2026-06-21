import { useEffect, useState } from 'react'
import { FormControl, MenuItem, Select, type SelectChangeEvent } from '@mui/material'
import { getProvider, setProvider, type ProviderInfo } from '../api/settings'

export default function ProviderSwitcher() {
  const [info, setInfo] = useState<ProviderInfo | null>(null)
  const [saving, setSaving] = useState(false)

  useEffect(() => {
    getProvider().then(setInfo).catch(() => {})
  }, [])

  async function change(e: SelectChangeEvent) {
    const provider = e.target.value
    setSaving(true)
    try {
      await setProvider(provider)
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
