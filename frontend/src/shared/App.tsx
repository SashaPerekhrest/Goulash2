import { NavLink, Navigate, Route, Routes } from 'react-router-dom'
import { SupplierDetailsPage } from '../pages/SupplierDetailsPage'
import { SuppliersPage } from '../pages/SuppliersPage'
import { DiscoverPage } from '../pages/DiscoverPage'
import { IntegrationSettingsPage } from '../pages/IntegrationSettingsPage'
import { LoginPage } from '../pages/LoginPage'

const navItems = [
  { to: '/discover', label: 'Найти поставщиков' },
  { to: '/suppliers', label: 'База поставщиков' },
  { to: '/settings/integration', label: 'Настройки интеграции' },
]

export function App() {
  return (
    <div className="app-shell">
      <aside className="sidebar">
        <NavLink className="brand" to="/discover" aria-label="На главную">
          <span className="brand-mark">Г</span>
          <span>гуляш<span className="brand-period">.</span></span>
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
          <span>Каркас приложения</span>
        </div>
      </aside>

      <main className="main-area">
        <Routes>
          <Route path="/" element={<Navigate to="/discover" replace />} />
          <Route path="/login" element={<LoginPage />} />
          <Route path="/discover" element={<DiscoverPage />} />
          <Route path="/suppliers" element={<SuppliersPage />} />
          <Route path="/suppliers/:id" element={<SupplierDetailsPage />} />
          <Route path="/settings/integration" element={<IntegrationSettingsPage />} />
          <Route path="*" element={<NotFound />} />
        </Routes>
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
