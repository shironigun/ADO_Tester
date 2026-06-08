import { useCallback, useEffect, useRef, useState } from 'react'
import {
  Alert, Box, Button, Card, CardContent, Chip, CircularProgress, Divider, IconButton,
  List, ListItem, ListItemText, Snackbar, Stack, TextField, Typography,
} from '@mui/material'
import DeleteIcon from '@mui/icons-material/Delete'
import UploadFileIcon from '@mui/icons-material/UploadFile'
import DriveFolderUploadIcon from '@mui/icons-material/DriveFolderUpload'
import axios from 'axios'

const PROJECT_ID = 1

function axiosMessage(e: unknown): string {
  if (!axios.isAxiosError(e)) return String(e)
  const d = e.response?.data as unknown
  if (typeof d === 'string') return d
  if (d && typeof d === 'object' && 'title' in d) return String((d as { title: string }).title)
  return e.message
}

interface Doc {
  id: number; fileName: string; chunkCount: number; sizeBytes: number; uploadedAt: string
}
interface Hit { documentId: number; fileName: string; content: string; score: number }

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
    const { data } = await axios.get<Doc[]>(`/api/knowledgebase/${PROJECT_ID}`)
    setDocs(data)
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
      await axios.post(`/api/knowledgebase/${PROJECT_ID}`, form)
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
      await axios.delete(`/api/knowledgebase/doc/${id}`)
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
      const { data } = await axios.get<Hit[]>(`/api/knowledgebase/${PROJECT_ID}/search`, { params: { q: query, k: 8 } })
      setHits(data)
    } catch (e) { setError(axiosMessage(e)) }
    finally { setSearching(false) }
  }

  return (
    <Box>
      <Typography variant="h5" gutterBottom>Knowledgebase (domain base)</Typography>
      <Stack spacing={3} sx={{ maxWidth: 900 }}>

        <Card variant="outlined">
          <CardContent>
            <Typography variant="h6" gutterBottom>Upload .md files or a folder</Typography>
            <Stack direction="row" spacing={1}>
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
            <Typography variant="caption" color="text.secondary" sx={{ mt: 1, display: 'block' }}>
              Folder picker walks the whole tree — non-<code>.md</code> files are skipped automatically.
            </Typography>
            {error && <Alert severity="error" sx={{ mt: 1 }} onClose={() => setError('')}>{error}</Alert>}

            <Divider sx={{ my: 2 }} />
            <Typography variant="subtitle2" gutterBottom>Uploaded documents ({docs.length})</Typography>
            <List dense>
              {docs.map(d => (
                <ListItem key={d.id}
                  secondaryAction={
                    <IconButton edge="end" onClick={() => remove(d.id)}><DeleteIcon /></IconButton>
                  }>
                  <ListItemText
                    primary={d.fileName}
                    secondary={`${d.chunkCount} chunks · ${(d.sizeBytes / 1024).toFixed(1)} KB · ${new Date(d.uploadedAt).toLocaleString()}`}
                  />
                </ListItem>
              ))}
              {docs.length === 0 && <Typography variant="body2" color="text.secondary">None yet.</Typography>}
            </List>
          </CardContent>
        </Card>

        <Card variant="outlined">
          <CardContent>
            <Typography variant="h6" gutterBottom>Test retrieval (FTS5 / BM25)</Typography>
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
                <Stack spacing={1}>
                  {hits.map((h, i) => (
                    <Card key={i} variant="outlined">
                      <CardContent sx={{ py: 1.5 }}>
                        <Stack direction="row" spacing={1} sx={{ mb: 0.5, alignItems: 'center' }}>
                          <Chip size="small" label={h.fileName} />
                          <Chip size="small" variant="outlined" label={`bm25 ${h.score.toFixed(2)}`} />
                        </Stack>
                        <Typography variant="body2" sx={{ whiteSpace: 'pre-wrap' }}>
                          {h.content.length > 600 ? h.content.slice(0, 600) + '…' : h.content}
                        </Typography>
                      </CardContent>
                    </Card>
                  ))}
                  {hits.length === 0 && <Typography variant="body2">No matches.</Typography>}
                </Stack>
              </Box>
            )}
          </CardContent>
        </Card>

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
