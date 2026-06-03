import { useCallback, useEffect, useRef, useState } from 'react'
import {
  Box, Button, Card, CardContent, Chip, CircularProgress, Divider, IconButton,
  List, ListItem, ListItemText, Stack, TextField, Typography,
} from '@mui/material'
import DeleteIcon from '@mui/icons-material/Delete'
import UploadFileIcon from '@mui/icons-material/UploadFile'
import axios from 'axios'

const PROJECT_ID = 1

interface Doc {
  id: number; fileName: string; chunkCount: number; sizeBytes: number; uploadedAt: string
}
interface Hit { documentId: number; fileName: string; content: string; score: number }

export default function KnowledgebasePage() {
  const [docs, setDocs] = useState<Doc[]>([])
  const [uploading, setUploading] = useState(false)
  const [error, setError] = useState('')
  const fileRef = useRef<HTMLInputElement>(null)

  const [query, setQuery] = useState('')
  const [hits, setHits] = useState<Hit[] | null>(null)
  const [searching, setSearching] = useState(false)

  const loadDocs = useCallback(async () => {
    const { data } = await axios.get<Doc[]>(`/api/knowledgebase/${PROJECT_ID}`)
    setDocs(data)
  }, [])

  useEffect(() => { loadDocs().catch(() => {}) }, [loadDocs])

  async function onUpload(e: React.ChangeEvent<HTMLInputElement>) {
    const files = e.target.files
    if (!files || files.length === 0) return
    setError(''); setUploading(true)
    try {
      const form = new FormData()
      Array.from(files).forEach(f => form.append('files', f))
      await axios.post(`/api/knowledgebase/${PROJECT_ID}`, form)
      await loadDocs()
    } catch (err: unknown) {
      setError(axios.isAxiosError(err) ? String(err.response?.data ?? err.message) : String(err))
    } finally {
      setUploading(false)
      if (fileRef.current) fileRef.current.value = ''
    }
  }

  async function remove(id: number) {
    await axios.delete(`/api/knowledgebase/doc/${id}`)
    await loadDocs()
  }

  async function search() {
    if (!query.trim()) return
    setSearching(true); setHits(null)
    try {
      const { data } = await axios.get<Hit[]>(`/api/knowledgebase/${PROJECT_ID}/search`, { params: { q: query, k: 8 } })
      setHits(data)
    } finally { setSearching(false) }
  }

  return (
    <Box>
      <Typography variant="h5" gutterBottom>Knowledgebase (domain base)</Typography>
      <Stack spacing={3} sx={{ maxWidth: 900 }}>

        <Card variant="outlined">
          <CardContent>
            <Typography variant="h6" gutterBottom>Upload .md files</Typography>
            <Button component="label" variant="contained" startIcon={<UploadFileIcon />} disabled={uploading}>
              {uploading ? <CircularProgress size={18} sx={{ color: 'white' }} /> : 'Choose .md files'}
              <input ref={fileRef} type="file" hidden multiple accept=".md" onChange={onUpload} />
            </Button>
            {error && <Typography color="error" sx={{ mt: 1 }}>{error}</Typography>}

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
    </Box>
  )
}
