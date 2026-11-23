import { MenuItem, TextField } from '@mui/material'
import type { SxProps, Theme } from '@mui/material/styles'

/** Select that shows the literal label "All" when value is empty (never a blank closed field). */
export default function AllSelect({
  label, value, onChange, options, sx, minWidth = 120,
}: {
  label: string
  value: string
  onChange: (next: string) => void
  options: { value: string; label: string }[]
  sx?: SxProps<Theme>
  minWidth?: number
}) {
  return (
    <TextField
      select
      size="small"
      label={label}
      value={value}
      onChange={e => onChange(e.target.value)}
      slotProps={{
        inputLabel: { shrink: true },
        select: {
          displayEmpty: true,
          renderValue: (selected: unknown) => {
            const v = String(selected ?? '')
            if (!v) return 'All'
            return options.find(o => o.value === v)?.label ?? v
          },
        },
      }}
      sx={{ minWidth, ...sx }}
    >
      <MenuItem value="">All</MenuItem>
      {options.map(o => (
        <MenuItem key={o.value} value={o.value}>{o.label}</MenuItem>
      ))}
    </TextField>
  )
}
