import {
  Box, Chip, Dialog, DialogContent, DialogTitle, Divider, IconButton, Stack,
  Table, TableBody, TableCell, TableHead, TableRow, Typography,
} from '@mui/material'
import CloseIcon from '@mui/icons-material/Close'
import KeyIcon from '@mui/icons-material/KeyOutlined'
import { MONO_FONT } from '../../theme/tokens'
import { NEUTRAL_COLOR } from './palette'
import type { SchemaGraph } from '../../api/schema'

interface Props {
  open: boolean
  tableId: number | null
  graph: SchemaGraph
  serviceName: Map<number, string>
  serviceColor: Map<number, string>
  onClose: () => void
  onOpenTable: (id: number) => void
}

// Full schema popup for one table: columns (COLUMN|TYPE|KEY|NOTES) + a clickable
// "Referenced by" list (derived from every relation targeting this table).
export default function TableDetailDialog({
  open, tableId, graph, serviceName, serviceColor, onClose, onOpenTable,
}: Props) {
  const table = tableId != null ? graph.tables.find(t => t.id === tableId) : undefined

  // "Referenced by": relations whose target is this table → the referencing (source) side.
  const referencedBy = table
    ? graph.relations
        .filter(r => r.targetTableId === table.id && r.sourceTableId !== table.id)
        .map(r => ({ src: graph.tables.find(t => t.id === r.sourceTableId), col: r.sourceColumn }))
        .filter((x): x is { src: NonNullable<typeof x.src>; col: string | null } => !!x.src)
    : []

  const color = table?.serviceId != null ? (serviceColor.get(table.serviceId) ?? NEUTRAL_COLOR) : NEUTRAL_COLOR

  return (
    <Dialog open={open} onClose={onClose} maxWidth="md" fullWidth scroll="paper">
      {table && (
        <>
          <DialogTitle component="div" sx={{ display: 'flex', alignItems: 'center', gap: 1, pr: 1 }}>
            <Box sx={{ width: 12, height: 12, borderRadius: '50%', bgcolor: color, flexShrink: 0 }} />
            <Typography sx={{ fontWeight: 700, fontFamily: MONO_FONT, flex: 1, minWidth: 0 }} noWrap>
              {table.name}
            </Typography>
            {table.serviceId != null && (
              <Chip size="small" variant="outlined" label={serviceName.get(table.serviceId) ?? '—'} />
            )}
            {table.sqlSchema && <Chip size="small" variant="outlined" label={`schema ${table.sqlSchema}`} />}
            {table.isKeyless && <Chip size="small" color="warning" variant="outlined" label="keyless" />}
            <Chip size="small" variant="outlined" label={`${table.columns.length} columns`} />
            <IconButton size="small" onClick={onClose} aria-label="close"><CloseIcon /></IconButton>
          </DialogTitle>

          <DialogContent dividers>
            <Stack spacing={2}>
              <Box sx={{ overflowX: 'auto' }}>
                <Table size="small">
                  <TableHead><TableRow>
                    <TableCell>Column</TableCell>
                    <TableCell>Type</TableCell>
                    <TableCell>Key</TableCell>
                    <TableCell>Notes</TableCell>
                  </TableRow></TableHead>
                  <TableBody>
                    {table.columns.map(c => (
                      <TableRow key={c.name}>
                        <TableCell sx={{ fontFamily: MONO_FONT, fontWeight: c.isPrimaryKey ? 700 : 500 }}>
                          {c.name}
                        </TableCell>
                        <TableCell sx={{ fontFamily: MONO_FONT, color: 'text.secondary' }}>
                          {c.dataType ?? '—'}
                        </TableCell>
                        <TableCell>
                          {c.isPrimaryKey && (
                            <Chip size="small" color="primary" icon={<KeyIcon />} label="PK" sx={{ mr: 0.5 }} />
                          )}
                          {c.isForeignKey && (
                            <Chip size="small" color="info" variant="outlined"
                              label={c.referencesTable ? `FK → ${c.referencesTable}` : 'FK'} />
                          )}
                        </TableCell>
                        <TableCell sx={{ color: 'text.secondary', fontSize: 12 }}>
                          {[c.isNullable ? 'nullable' : null, c.referencesColumn ? `→ ${c.referencesColumn}` : null]
                            .filter(Boolean).join(' · ') || ''}
                        </TableCell>
                      </TableRow>
                    ))}
                    {table.columns.length === 0 && (
                      <TableRow>
                        <TableCell colSpan={4} sx={{ color: 'text.disabled', textAlign: 'center', py: 2 }}>
                          No columns parsed.
                        </TableCell>
                      </TableRow>
                    )}
                  </TableBody>
                </Table>
              </Box>

              <Box>
                <Divider textAlign="left" sx={{ mb: 1 }}>
                  <Typography variant="overline">Referenced by</Typography>
                </Divider>
                {referencedBy.length > 0 ? (
                  <Stack direction="row" spacing={0.75} sx={{ flexWrap: 'wrap', gap: 0.75 }}>
                    {referencedBy.map(({ src, col }, i) => (
                      <Chip
                        key={`${src.id}-${col}-${i}`}
                        size="small" clickable variant="outlined"
                        onClick={() => onOpenTable(src.id)}
                        label={`${src.name}${col ? `.${col}` : ''}`}
                        sx={{ fontFamily: MONO_FONT, fontSize: 11.5 }}
                      />
                    ))}
                  </Stack>
                ) : (
                  <Typography variant="caption" color="text.disabled">
                    Nothing references this table.
                  </Typography>
                )}
              </Box>
            </Stack>
          </DialogContent>
        </>
      )}
    </Dialog>
  )
}
