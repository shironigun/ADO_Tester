// Shared coloring + naming helpers for the Schema Explorer ERD.
// Pure (no React/MUI) so layout.ts and the SVG components can all import it.

// Distinct-enough categorical palette; services map to colors by their order in the graph.
export const PALETTE = [
  '#6366f1', '#0ea5e9', '#10b981', '#f59e0b', '#ef4444', '#8b5cf6',
  '#ec4899', '#14b8a6', '#f97316', '#22c55e', '#3b82f6', '#a855f7',
]

/** Fallback for tables whose owning service is unknown (serviceId == null). */
export const NEUTRAL_COLOR = '#64748b'

/** Map service ids → palette colors by their order in the services array. */
export function buildServiceColorMap(serviceIds: number[]): Map<number, string> {
  const m = new Map<number, string>()
  serviceIds.forEach((id, i) => m.set(id, PALETTE[i % PALETTE.length]))
  return m
}

/** Friendly module label for QAs: CustomerDbContext → "Customer", "Lead Service" → "Lead". */
export function prettifyModule(name: string): string {
  const trimmed = name
    .replace(/DbContext$/i, '')
    .replace(/Context$/i, '')
    .replace(/Service$/i, '')
    .trim()
  if (!trimmed) return name
  // Capitalize the first letter (scan produces lowercase path segments like "customer").
  return trimmed.charAt(0).toUpperCase() + trimmed.slice(1)
}
