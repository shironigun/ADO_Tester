import { useCallback, useEffect, useRef, useState } from 'react'
import {
  Alert, Box, Button, Chip, CircularProgress, IconButton, LinearProgress,
  Snackbar, Stack, TextField, Tooltip, Typography,
} from '@mui/material'
import { alpha } from '@mui/material/styles'
import DeleteIcon from '@mui/icons-material/Delete'
import UploadFileIcon from '@mui/icons-material/UploadFile'
import DriveFolderUploadIcon from '@mui/icons-material/DriveFolderUpload'
import MenuBookIcon from '@mui/icons-material/MenuBookOutlined'
import ArticleIcon from '@mui/icons-material/ArticleOutlined'
import DataObjectIcon from '@mui/icons-material/DataObjectOutlined'
import SearchOffIcon from '@mui/icons-material/SearchOffOutlined'
import PageHeader from '../components/PageHeader'
import Section from '../components/Section'
import StatTile from '../components/StatTile'
import EmptyState from '../components/EmptyState'
import { Stagger, StaggerItem } from '../motion/primitives'
import { PROJECT_ID } from '../config/env'
import { axiosMessage } from '../common/errors'
import { listDocs, uploadDocs, deleteDoc, searchDocs, type Doc, type Hit } from '../api/knowledgebase'

export default function KnowledgebasePage() {
  const [docs, setDocs] = useState<Doc[]>([])
  const [uploading, setUploading] = useState(false)
  const [error, setError] = useState('')
  const fileRef = useRef<HTMLInputElement>(null)
  const folderRef = useRef<HTMLInputElement>(null)

  const [query, setQuery] = useState('')
  const [hits, setHits] = useState<Hit[] | null>(null)
  const [searching, setSearching] = useState(false)
  const [toast, setToast] = useState('')

  const loadDocs = useCallback(async () => {
    setDocs(await listDocs(PROJECT_ID))
  }, [])

  useEffect(() => { loadDocs().catch(() => {}) }, [loadDocs])

  async function onUpload(e: React.ChangeEvent<HTMLInputElement>) {
    const picked = e.target.files
    if (!picked || picked.length === 0) return
    // Folder picker hands us every file in the tree — filter to .md (case-insensitive).
    const mdFiles = Array.from(picked).filter(f => f.name.toLowerCase().endsWith('.md'))
    if (mdFiles.length === 0) {
      setError('No .md files found in the selection.')
      if (fileRef.current) fileRef.current.value = ''
      if (folderRef.current) folderRef.current.value = ''
      return
    }
    setError(''); setUploading(true)
    try {
      const form = new FormData()
      mdFiles.forEach(f => form.append('files', f))
      await uploadDocs(PROJECT_ID, form)
      await loadDocs()
      const skipped = picked.length - mdFiles.length
      setToast(skipped > 0
        ? `Uploaded ${mdFiles.length} .md file(s) · skipped ${skipped} non-md`
        : `Uploaded ${mdFiles.length} file(s)`)
    } catch (err: unknown) {
      setError(axiosMessage(err))
    } finally {
      setUploading(false)
      if (fileRef.current) fileRef.current.value = ''
      if (folderRef.current) folderRef.current.value = ''
    }
  }

  async function remove(id: number) {
    try {
      await deleteDoc(id)
      await loadDocs()
      setToast('Document deleted')
    } catch (e) { setError(axiosMessage(e)) }
  }

  async function search() {
    if (!query.trim() || query.trim().length < 2) {
      setError('Search query must be at least 2 characters.'); return
    }
    setError('')
    setSearching(true); setHits(null)
    try {
      setHits(await searchDocs(PROJECT_ID, query, 8))
    } catch (e) { setError(axiosMessage(e)) }
    finally { setSearching(false) }
  }

  const totalChunks = docs.reduce((s, d) => s + d.chunkCount, 0)

  return (
    <Box sx={{ pb: 4 }}>
      <PageHeader
        title="Knowledgebase"
        subtitle="The domain base agents retrieve from. Upload Markdown context, then test lexical retrieval (FTS5 / BM25)."
      />

      {/* Stat row */}
      <Stagger>
        <Box sx={{ display: 'grid', gap: 2, mb: 3, gridTemplateColumns: { xs: '1fr 1fr', sm: 'repeat(2, 240px)' } }}>
          <StaggerItem>
            <StatTile icon={<ArticleIcon />} label="Documents" value={docs.length} color="primary.main" sub=".md domain files" />
          </StaggerItem>
          <StaggerItem>
            <StatTile icon={<DataObjectIcon />} label="Indexed chunks" value={totalChunks} color="secondary.main" sub="Searchable via BM25" />
          </StaggerItem>
        </Box>
      </Stagger>

      <Stack spacing={3} sx={{ maxWidth: 960 }}>

        <Section title="Upload context">
          {/* Dropzone-styled call to action (still button-driven) */}
          <Box sx={{
            border: '1.5px dashed', borderColor: 'divider', borderRadius: 3,
            p: 3, textAlign: 'center',
            transition: 'border-color 200ms, background-color 200ms',
            '&:hover': {
              borderColor: 'primary.main',
              bgcolor: theme => alpha(theme.palette.primary.main, 0.04),
            },
          }}>
            <Box sx={{ color: 'primary.main', mb: 1, '& svg': { fontSize: 40 } }}>
              <UploadFileIcon />
            </Box>
            <Typography variant="subtitle1" sx={{ fontWeight: 600 }}>Add Markdown to the domain base</Typography>
            <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
              Folder picker walks the whole tree — non-<code>.md</code> files are skipped automatically.
            </Typography>
            <Stack direction="row" spacing={1} sx={{ justifyContent: 'center', flexWrap: 'wrap', gap: 1 }}>
              <Button component="label" variant="contained" startIcon={<UploadFileIcon />} disabled={uploading}>
                {uploading ? <CircularProgress size={18} sx={{ color: 'white' }} /> : 'Choose files'}
                <input ref={fileRef} type="file" hidden multiple accept=".md" onChange={onUpload} />
              </Button>
              <Button component="label" variant="outlined" startIcon={<DriveFolderUploadIcon />} disabled={uploading}>
                {uploading ? <CircularProgress size={18} /> : 'Choose folder'}
                {/* webkitdirectory + directory aren't in React's HTMLAttributes — cast around it. */}
                <input
                  ref={folderRef}
                  type="file"
                  hidden
                  onChange={onUpload}
                  {...({ webkitdirectory: '', directory: '' } as React.InputHTMLAttributes<HTMLInputElement>)}
                />
              </Button>
            </Stack>
          </Box>
          {error && <Alert severity="error" sx={{ mt: 2 }} onClose={() => setError('')}>{error}</Alert>}
        </Section>

        <Section title={`Documents (${docs.length})`}>
          {docs.length === 0 ? (
            <EmptyState compact icon={<MenuBookIcon />} title="No documents yet"
              hint="Upload Markdown files to give the agents domain context." />
          ) : (
            <Stagger>
              <Stack spacing={1}>
                {docs.map(d => (
                  <StaggerItem key={d.id}>
                    <Stack direction="row" spacing={1.5} sx={{
                      alignItems: 'center', p: 1.25, borderRadius: 2,
                      border: '1px solid', borderColor: 'divider',
                      transition: 'border-color 180ms, background-color 180ms',
                      '&:hover': { borderColor: 'primary.main', bgcolor: 'action.hover' },
                    }}>
                      <Box sx={{
                        width: 36, height: 36, borderRadius: 1.5, flexShrink: 0,
                        display: 'flex', alignItems: 'center', justifyContent: 'center',
                        color: 'primary.main', bgcolor: theme => alpha(theme.palette.primary.main, 0.12),
                      }}>
                        <ArticleIcon fontSize="small" />
                      </Box>
                      <Box sx={{ flex: 1, minWidth: 0 }}>
                        <Typography variant="body2" noWrap sx={{ fontWeight: 600 }}>{d.fileName}</Typography>
                        <Typography variant="caption" color="text.secondary">
                          {(d.sizeBytes / 1024).toFixed(1)} KB · {new Date(d.uploadedAt).toLocaleString()}
                        </Typography>
                      </Box>
                      <Chip size="small" variant="outlined" label={`${d.chunkCount} chunks`} />
                      <Tooltip title="Delete">
                        <IconButton size="small" onClick={() => remove(d.id)}><DeleteIcon fontSize="small" /></IconButton>
                      </Tooltip>
                    </Stack>
                  </StaggerItem>
                ))}
              </Stack>
            </Stagger>
          )}
        </Section>

        <Section title="Test retrieval (FTS5 / BM25)">
          <Stack direction="row" spacing={1}>
            <TextField fullWidth size="small" label="Query" value={query}
              onChange={e => setQuery(e.target.value)}
              onKeyDown={e => e.key === 'Enter' && search()} />
            <Button variant="outlined" onClick={search} disabled={searching || !query.trim()}>
              {searching ? <CircularProgress size={18} /> : 'Search'}
            </Button>
          </Stack>

          {hits && (
            <Box sx={{ mt: 2 }}>
              <Typography variant="body2" color="text.secondary" gutterBottom>{hits.length} hit(s)</Typography>
              {hits.length === 0 ? (
                <EmptyState compact icon={<SearchOffIcon />} title="No matches"
                  hint="Try different keywords or upload more context." />
              ) : (
                <Stagger>
                  <Stack spacing={1.25}>
                    {hits.map((h, i) => (
                      <StaggerItem key={i}>
                        <Box sx={{
                          p: 1.5, borderRadius: 2, border: '1px solid', borderColor: 'divider',
                        }}>
                          <Stack direction="row" spacing={1} sx={{ mb: 0.75, alignItems: 'center' }}>
                            <Chip size="small" label={`#${i + 1}`} color="primary" />
                            <Chip size="small" variant="outlined" icon={<ArticleIcon sx={{ fontSize: 14 }} />} label={h.fileName} />
                            <Box sx={{ flex: 1 }} />
                            <Typography variant="caption" color="text.secondary" sx={{ fontFamily: 'monospace' }}>
                              bm25 {h.score.toFixed(2)}
                            </Typography>
                          </Stack>
                          {/* rank-based relevance bar (top hit = full) */}
                          <LinearProgress variant="determinate"
                            value={Math.round(((hits.length - i) / hits.length) * 100)}
                            sx={{ mb: 1, height: 4, borderRadius: 2 }} />
                          <Typography variant="body2" sx={{ whiteSpace: 'pre-wrap' }}>
                            {h.content.length > 600 ? h.content.slice(0, 600) + '…' : h.content}
                          </Typography>
                        </Box>
                      </StaggerItem>
                    ))}
                  </Stack>
                </Stagger>
              )}
            </Box>
          )}
        </Section>

      </Stack>

      <Snackbar
        open={!!toast}
        autoHideDuration={3000}
        onClose={() => setToast('')}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'right' }}
      >
        <Alert severity="success" onClose={() => setToast('')} variant="filled">{toast}</Alert>
      </Snackbar>
    </Box>
  )
}
