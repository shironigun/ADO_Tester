import { api } from './client'

export interface Doc {
  id: number
  fileName: string
  folderPath?: string | null
  chunkCount: number
  sizeBytes: number
  uploadedAt: string
}

export interface Hit {
  documentId: number
  fileName: string
  content: string
  score: number
}

export interface FolderUploadItem {
  folderPath: string
  fileName: string
  content: string
}

export interface FolderUploadResponse {
  uploaded: { id: number; fileName: string }[]
  failed:   { fileName: string; error: string }[]
}

export async function listDocs(projectId: number): Promise<Doc[]> {
  const { data } = await api.get<Doc[]>(`/api/knowledgebase/${projectId}`)
  return data
}

export async function uploadDocs(projectId: number, form: FormData): Promise<void> {
  await api.post(`/api/knowledgebase/${projectId}`, form)
}

/** Batch folder upload — preserves directory structure via folderPath. */
export async function uploadFolder(
  projectId: number,
  items: FolderUploadItem[],
): Promise<FolderUploadResponse> {
  const { data } = await api.post<FolderUploadResponse>(
    `/api/knowledgebase/${projectId}/folder`,
    items,
  )
  return data
}

export async function deleteDoc(id: number): Promise<void> {
  await api.delete(`/api/knowledgebase/doc/${id}`)
}

/** Delete a folder and all docs under it (exact path + descendants). Empty path = root-only. */
export async function deleteFolder(projectId: number, path: string): Promise<{ deleted: number }> {
  const { data } = await api.delete<{ deleted: number }>(
    `/api/knowledgebase/${projectId}/folder`,
    { params: { path } },
  )
  return data
}

export async function searchDocs(projectId: number, q: string, k = 8): Promise<Hit[]> {
  const { data } = await api.get<Hit[]>(`/api/knowledgebase/${projectId}/search`, { params: { q, k } })
  return data
}
