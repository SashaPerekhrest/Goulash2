import { useState } from 'react'
import { NavLink, Navigate, Outlet, Route, Routes } from 'react-router-dom'
import { logout as sendLogout } from './api/apiClient'
import { GuestRoute, ProtectedRoute, useAuth } from './auth'
import { SupplierDetailsPage } from '../pages/SupplierDetailsPage'
import { SuppliersPage } from '../pages/SuppliersPage'
import { DiscoverPage } from '../pages/DiscoverPage'
import { IntegrationSettingsPage } from '../pages/IntegrationSettingsPage'
import { LoginPage } from '../pages/LoginPage'
import { ErrorNotice } from './ui'

const navItems = [
  { to: '/discover', label: 'Найти поставщиков' },
  { to: '/suppliers', label: 'База поставщиков' },
  { to: '/settings/integration', label: 'Настройки интеграции' },
]

export function App() {
  return (
    <Routes>
      <Route path="/login" element={<GuestRoute><LoginPage /></GuestRoute>} />
      <Route element={<ProtectedRoute><AppShell /></ProtectedRoute>}>
        <Route index element={<Navigate to="/discover" replace />} />
        <Route path="discover" element={<DiscoverPage />} />
        <Route path="suppliers" element={<SuppliersPage />} />
        <Route path="suppliers/:id" element={<SupplierDetailsPage />} />
        <Route path="settings/integration" element={<IntegrationSettingsPage />} />
        <Route path="*" element={<NotFound />} />
      </Route>
    </Routes>
  )
}

function AppShell() {
  const auth = useAuth()
  const [loggingOut, setLoggingOut] = useState(false)
  const [logoutError, setLogoutError] = useState(false)

  async function handleLogout() {
    setLoggingOut(true)
    setLogoutError(false)
    try {
      await sendLogout()
      auth.markAnonymous()
    } catch (error) {
      if (error instanceof Error && 'status' in error && error.status === 401) {
        auth.markAnonymous()
      } else {
        setLogoutError(true)
      }
    } finally {
      setLoggingOut(false)
    }
  }

  return (
    <div className="app-shell">
      <aside className="sidebar">
        <NavLink className="brand" to="/discover" aria-label="На главную">
          <img className="brand-logo" src="/diet-icon.png" alt="" />
          <span>Фуд сервис</span>
        </NavLink>
        <p className="sidebar-caption">FOOD SUPPLY DESK</p>
        <nav className="main-nav" aria-label="Основная навигация">
          {navItems.map(({ to, label }) => (
            <NavLink key={to} to={to} className={({ isActive }) => isActive ? 'nav-link active' : 'nav-link'}>
              <span className="nav-dot" aria-hidden="true" />
              {label}
            </NavLink>
          ))}
        </nav>
        <div className="sidebar-footer">
          <span className="status-pip" />
          <span>Сессия активна</span>
        </div>
      </aside>

      <main className="main-area">
        <header className="topbar">
          <span className="topbar-caption">РАБОЧЕЕ ПРОСТРАНСТВО</span>
          <button className="button quiet logout-button" type="button" onClick={() => void handleLogout()} disabled={loggingOut}>
            {loggingOut ? 'Выходим…' : 'Выйти'}
          </button>
        </header>
        {logoutError && (
          <div className="global-notice">
            <ErrorNotice title="Не удалось завершить сессию">
              Повторите попытку. Ваш вход пока остаётся активным.
            </ErrorNotice>
          </div>
        )}
        <Outlet />
      </main>
    </div>
  )
}

function NotFound() {
  return (
    <section className="page-content">
      <span className="eyebrow">404 · СТРАНИЦА НЕ НАЙДЕНА</span>
      <h1>Здесь пока пусто</h1>
      <p className="lead">Проверьте адрес или выберите раздел в меню.</p>
    </section>
  )
}
