import {
  Box, Chip, Divider, List, ListItemButton, Stack, Typography,
} from '@mui/material'
import KeyIcon from '@mui/icons-material/KeyOutlined'

export interface ModuleItem {
  id: number
  label: string
  count: number
  color: string
}

interface Props {
  modules: ModuleItem[]
  selectedId: number | null
  onSelect: (id: number) => void
}

// Left rail: module list (one selected at a time) + a compact ERD legend.
export default function ModuleSidebar({ modules, selectedId, onSelect }: Props) {
  return (
    <Box sx={{ width: 232, flexShrink: 0 }}>
      <List dense disablePadding>
        {modules.map(m => (
          <ListItemButton
            key={m.id}
            selected={m.id === selectedId}
            onClick={() => onSelect(m.id)}
            sx={{ borderRadius: 1.5, mb: 0.25, gap: 1 }}
          >
            <Box sx={{ width: 10, height: 10, borderRadius: '3px', bgcolor: m.color, flexShrink: 0 }} />
            <Typography variant="body2" sx={{ flex: 1, minWidth: 0, fontWeight: m.id === selectedId ? 700 : 500 }} noWrap>
              {m.label}
            </Typography>
            <Chip size="small" variant="outlined" label={m.count} sx={{ height: 20, '& .MuiChip-label': { px: 0.75, fontSize: 11 } }} />
          </ListItemButton>
        ))}
      </List>

      <Divider sx={{ my: 1.5 }} />

      <Typography variant="overline" color="text.secondary" sx={{ px: 1 }}>Legend</Typography>
      <Stack spacing={1} sx={{ px: 1, mt: 0.75 }}>
        <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
          <Chip size="small" color="primary" icon={<KeyIcon />} label="PK" sx={{ height: 22 }} />
          <Typography variant="caption" color="text.secondary">primary key</Typography>
        </Stack>
        <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
          <Chip size="small" color="info" variant="outlined" label="FK" sx={{ height: 22 }} />
          <Typography variant="caption" color="text.secondary">foreign key</Typography>
        </Stack>
        <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
          <LegendSwatch dashed />
          <Typography variant="caption" color="text.secondary">keyless (view / SP result)</Typography>
        </Stack>
        <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
          <LegendSwatch dashed />
          <Typography variant="caption" color="text.secondary">owned by another module</Typography>
        </Stack>
        <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
          <Box sx={{ width: 24, borderTop: '2px dashed', borderColor: 'text.disabled' }} />
          <Typography variant="caption" color="text.secondary">inferred link</Typography>
        </Stack>
      </Stack>
    </Box>
  )
}

function LegendSwatch({ dashed }: { dashed?: boolean }) {
  return (
    <Box sx={{
      width: 26, height: 16, borderRadius: '4px', flexShrink: 0,
      border: '1.5px', borderStyle: dashed ? 'dashed' : 'solid', borderColor: 'text.disabled',
    }} />
  )
}
