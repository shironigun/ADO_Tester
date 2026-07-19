import { api } from './client'
import { apiUrl } from '../config/env'

// ── DB Schema Explorer API — /api/schema ─────────────────────────────────────
// Read-only mindmap + live entity-trace module. This module manages scan-target
// products; the scan, graph, entity registry and live tracing endpoints land here
// in later phases. Interfaces mirror SchemaExplorerContracts.cs exactly.

export interface SchemaScan {
  id: number
  productId: number
  status: string
  phase: string | null
  commitId: string | null
  branch: string | null
  serviceCount: number
  tableCount: number
  relationCount: number
  entityCount: number
  errorMessage: string | null
  startedAt: string | null
  completedAt: string | null
  createdAt: string
}

export interface SchemaProduct {
  id: number
  name: string
  adoProject: string
  adoRepository: string
  defaultBranch: string
  description: string | null
  createdAt: string
  updatedAt: string
  latestScan: SchemaScan | null
}

export interface CreateProductRequest {
  name: string
  adoProject: string
  adoRepository: string
  defaultBranch: string
  description?: string | null
}

export interface UpdateProductRequest {
  name?: string
  adoProject?: string
  adoRepository?: string
  defaultBranch?: string
  description?: string | null
}

// ── Methods ───────────────────────────────────────────────────────────────────

export async function listProducts(): Promise<SchemaProduct[]> {
  const { data } = await api.get<{ total: number; items: SchemaProduct[] }>('/api/schema/products')
  return data.items ?? []
}

/** The single Schema Explorer product, derived from the global Azure DevOps settings. */
export async function getCurrentProduct(): Promise<SchemaProduct> {
  const { data } = await api.get<SchemaProduct>('/api/schema/product')
  return data
}

export async function getProduct(id: number): Promise<SchemaProduct> {
  const { data } = await api.get<SchemaProduct>(`/api/schema/products/${id}`)
  return data
}

export async function createProduct(req: CreateProductRequest): Promise<SchemaProduct> {
  const { data } = await api.post<SchemaProduct>('/api/schema/products', req)
  return data
}

export async function updateProduct(id: number, req: UpdateProductRequest): Promise<SchemaProduct> {
  const { data } = await api.patch<SchemaProduct>(`/api/schema/products/${id}`, req)
  return data
}

export async function deleteProduct(id: number): Promise<void> {
  await api.delete(`/api/schema/products/${id}`)
}

export async function listScans(productId: number): Promise<SchemaScan[]> {
  const { data } = await api.get<{ total: number; items: SchemaScan[] }>(`/api/schema/products/${productId}/scans`)
  return data.items ?? []
}

// ── Scan (Phase A: read-only repo inventory) ─────────────────────────────────

/** Bucket → file paths, from the inventory phase. Keys: dbContexts, entityConfigs,
 *  migrations, sql, entities, controllers, frontendApi. */
export interface ScanInventory {
  commitId: string | null
  branch: string
  totalFiles: number
  otherCount: number
  buckets: Record<string, string[]>
}

export interface ScanDetail {
  scan: SchemaScan
  inventory: ScanInventory | null
}

export async function startScan(productId: number): Promise<{ scanId: number }> {
  const { data } = await api.post<{ scanId: number }>(`/api/schema/products/${productId}/scan`)
  return data
}

export async function getScan(scanId: number): Promise<ScanDetail> {
  const { data } = await api.get<ScanDetail>(`/api/schema/scans/${scanId}`)
  return data
}

/** SSE URL for a scan's live progress (consumed by useEventStream). */
export function scanStreamUrl(scanId: number): string {
  return apiUrl(`/api/schema/scans/${scanId}/stream`)
}

// ── Graph (Phase B output) ───────────────────────────────────────────────────

export interface SchemaColumn {
  name: string
  dataType: string | null
  isPrimaryKey: boolean
  isNullable: boolean
  isForeignKey: boolean
  referencesTable: string | null
  referencesColumn: string | null
}

export interface SchemaServiceNode {
  id: number
  name: string
  dbContextClass: string | null
  sourcePath: string | null
  tableCount: number
}

export interface SchemaTableNode {
  id: number
  serviceId: number | null
  name: string
  sqlSchema: string | null
  isKeyless: boolean
  columns: SchemaColumn[]
}

export interface SchemaRelationEdge {
  id: number
  sourceTableId: number
  targetTableId: number
  sourceColumn: string | null
  kind: string
}

export interface SchemaGraph {
  scanId: number
  services: SchemaServiceNode[]
  tables: SchemaTableNode[]
  relations: SchemaRelationEdge[]
}

export async function getScanGraph(scanId: number): Promise<SchemaGraph> {
  const { data } = await api.get<SchemaGraph>(`/api/schema/scans/${scanId}/graph`)
  return data
}

// ── Entity registry (Phase C output) ─────────────────────────────────────────

export interface SchemaEntity {
  id: number
  key: string
  displayName: string
  rootTableName: string | null
  idColumn: string | null
  scopeColumn: string | null
  displayColumns: string[]
  confidence: number | null
  source: string
  isEnabled: boolean
}

export async function listEntities(productId: number): Promise<SchemaEntity[]> {
  const { data } = await api.get<{ total: number; items: SchemaEntity[] }>(`/api/schema/products/${productId}/entities`)
  return data.items ?? []
}

// ── Live trace / connection (read-only) ──────────────────────────────────────

export interface EnvironmentConnection {
  environment: string
  server: string | null
  user: string | null
  database: string | null
}

export interface ConnectionFields {
  environment: string
  server: string
  user: string
  password?: string   // blank when editing = keep existing
  database: string
}

export interface TableTrace {
  table: string
  schema: string | null
  service: string | null
  direction: string        // 'root' | 'child' | 'parent'
  matchColumn: string | null
  sql: string              // SSMS-ready
  rowCount: number
  columns: string[]
  rows: Record<string, unknown>[]
  error: string | null
}

export interface TraceResult {
  entity: string
  rootTable: string
  idColumn: string
  idValue: string
  found: boolean
  root: TableTrace
  linked: TableTrace[]
  linkedTableCount: number
  totalRows: number
  truncated: string | null
}

export async function listConnections(productId: number): Promise<EnvironmentConnection[]> {
  const { data } = await api.get<{ items: EnvironmentConnection[] }>(`/api/schema/products/${productId}/connections`)
  return data.items ?? []
}

export async function upsertConnection(productId: number, req: ConnectionFields): Promise<EnvironmentConnection> {
  const { data } = await api.put<EnvironmentConnection>(`/api/schema/products/${productId}/connections`, req)
  return data
}

export async function deleteConnection(productId: number, environment: string): Promise<void> {
  await api.delete(`/api/schema/products/${productId}/connections/${encodeURIComponent(environment)}`)
}

export async function testConnection(
  productId: number,
  opts: { environment?: string; server?: string; user?: string; password?: string; database?: string },
): Promise<{ ok: boolean; error?: string }> {
  const { data } = await api.post<{ ok: boolean; error?: string }>(`/api/schema/products/${productId}/connections/test`, opts)
  return data
}

export async function traceEntity(productId: number, entityKey: string, id: string, environment?: string): Promise<TraceResult> {
  const { data } = await api.post<TraceResult>(`/api/schema/products/${productId}/trace`, { entityKey, id, environment })
  return data
}
