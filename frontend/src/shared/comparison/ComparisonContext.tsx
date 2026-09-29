import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'

const STORAGE_KEY = 'goulash:supplier-comparison:v1'

type ComparisonState = {
  supplierIds: string[]
  notes: Record<string, string>
}

type ComparisonContextValue = ComparisonState & {
  addSupplier: (id: string) => void
  removeSupplier: (id: string) => void
  hasSupplier: (id: string) => boolean
  setLocalNote: (id: string, note: string) => void
}

const emptyState: ComparisonState = { supplierIds: [], notes: {} }
const ComparisonContext = createContext<ComparisonContextValue | null>(null)

function readStoredState(): ComparisonState {
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY)
    if (!raw) return emptyState
    const parsed: unknown = JSON.parse(raw)
    if (!parsed || typeof parsed !== 'object') return emptyState

    const candidate = parsed as { supplierIds?: unknown; notes?: unknown }
    const supplierIds = Array.isArray(candidate.supplierIds)
      ? [...new Set(candidate.supplierIds.filter((id): id is string => typeof id === 'string' && id.length > 0))]
      : []
    const notes = candidate.notes && typeof candidate.notes === 'object' && !Array.isArray(candidate.notes)
      ? Object.fromEntries(Object.entries(candidate.notes).filter((entry): entry is [string, string] => typeof entry[1] === 'string'))
      : {}
    return { supplierIds, notes }
  } catch {
    return emptyState
  }
}

export function ComparisonProvider({ children }: { children: ReactNode }) {
  const [state, setState] = useState<ComparisonState>(readStoredState)

  useEffect(() => {
    try {
      window.localStorage.setItem(STORAGE_KEY, JSON.stringify(state))
    } catch {
      // Keep the current comparison usable in memory if browser storage is unavailable.
    }
  }, [state])

  useEffect(() => {
    function handleStorage(event: StorageEvent) {
      if (event.key === STORAGE_KEY) setState(readStoredState())
    }
    window.addEventListener('storage', handleStorage)
    return () => window.removeEventListener('storage', handleStorage)
  }, [])

  const value = useMemo<ComparisonContextValue>(() => ({
    ...state,
    addSupplier: (id) => setState((current) => current.supplierIds.includes(id)
      ? current
      : { ...current, supplierIds: [...current.supplierIds, id] }),
    removeSupplier: (id) => setState((current) => {
      const notes = { ...current.notes }
      delete notes[id]
      return { supplierIds: current.supplierIds.filter((supplierId) => supplierId !== id), notes }
    }),
    hasSupplier: (id) => state.supplierIds.includes(id),
    setLocalNote: (id, note) => setState((current) => ({ ...current, notes: { ...current.notes, [id]: note } })),
  }), [state])

  return <ComparisonContext.Provider value={value}>{children}</ComparisonContext.Provider>
}

export function useComparison() {
  const context = useContext(ComparisonContext)
  if (!context) throw new Error('useComparison must be used inside ComparisonProvider')
  return context
}
