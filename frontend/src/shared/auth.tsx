import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import { Navigate, useLocation } from 'react-router-dom'
import { ApiError, getAuthSession } from './api/apiClient'
import { ErrorNotice, PageLoading } from './ui'

type AuthStatus = 'checking' | 'authenticated' | 'anonymous' | 'error'
type AuthContextValue = {
  status: AuthStatus
  refresh: (signal?: AbortSignal) => Promise<void>
  markAuthenticated: () => void
  markAnonymous: () => void
}

const AuthContext = createContext<AuthContextValue | null>(null)
const unauthorizedEvent = 'goulash:unauthorized'

export function AuthProvider({ children }: { children: ReactNode }) {
  const [status, setStatus] = useState<AuthStatus>('checking')

  const refresh = useCallback(async (signal?: AbortSignal) => {
    setStatus('checking')
    try {
      await getAuthSession(signal)
      if (!signal?.aborted) setStatus('authenticated')
    } catch (error) {
      if (signal?.aborted) return
      setStatus(error instanceof ApiError && error.status === 401 ? 'anonymous' : 'error')
    }
  }, [])

  const markAuthenticated = useCallback(() => setStatus('authenticated'), [])
  const markAnonymous = useCallback(() => setStatus('anonymous'), [])

  useEffect(() => {
    const controller = new AbortController()
    void refresh(controller.signal)
    return () => controller.abort()
  }, [refresh])

  useEffect(() => {
    const handleUnauthorized = () => setStatus('anonymous')
    window.addEventListener(unauthorizedEvent, handleUnauthorized)
    return () => window.removeEventListener(unauthorizedEvent, handleUnauthorized)
  }, [])

  const value = useMemo<AuthContextValue>(() => ({
    status,
    refresh,
    markAuthenticated,
    markAnonymous,
  }), [status, refresh, markAuthenticated, markAnonymous])

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

export function useAuth() {
  const value = useContext(AuthContext)
  if (!value) throw new Error('useAuth must be used within AuthProvider')
  return value
}

export function ProtectedRoute({ children }: { children: ReactNode }) {
  const auth = useAuth()
  const location = useLocation()

  if (auth.status === 'checking') return <PageLoading label="Проверяем сессию…" />
  if (auth.status === 'error') {
    return (
      <div className="auth-gate">
        <ErrorNotice title="Не удалось проверить сессию" action={
          <button className="button secondary" type="button" onClick={() => void auth.refresh()}>
            Повторить
          </button>
        }>
          Проверьте соединение и попробуйте ещё раз.
        </ErrorNotice>
      </div>
    )
  }
  if (auth.status === 'anonymous') {
    return <Navigate to="/login" replace state={{ from: location }} />
  }
  return children
}

export function GuestRoute({ children }: { children: ReactNode }) {
  const auth = useAuth()
  const location = useLocation()
  if (auth.status === 'authenticated') {
    return <Navigate to={getSafeReturnPath(location.state)} replace />
  }
  return children
}

export function getSafeReturnPath(state: unknown): string {
  if (!state || typeof state !== 'object' || !('from' in state)) return '/discover'
  const from = (state as { from?: { pathname?: unknown; search?: unknown; hash?: unknown } }).from
  if (!from || typeof from.pathname !== 'string' || !from.pathname.startsWith('/') || from.pathname.startsWith('//')) {
    return '/discover'
  }
  if (from.pathname === '/login') return '/discover'
  const search = typeof from.search === 'string' ? from.search : ''
  const hash = typeof from.hash === 'string' ? from.hash : ''
  return `${from.pathname}${search}${hash}`
}
