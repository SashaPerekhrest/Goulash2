import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { ApiError, getAiSettings, type AiSettings } from '../shared/api/apiClient'
import { Card, EmptyState, ErrorNotice, PageLoading } from '../shared/ui'

export function DiscoverPage() {
  const [settings, setSettings] = useState<AiSettings | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState(false)

  useEffect(() => {
    const controller = new AbortController()
    let active = true
    getAiSettings(controller.signal)
      .then((savedSettings) => {
        if (active) setSettings(savedSettings)
      })
      .catch((cause: unknown) => {
        if (active && !(cause instanceof ApiError && cause.status === 401)) setError(true)
      })
      .finally(() => {
        if (active) setLoading(false)
      })
    return () => {
      active = false
      controller.abort()
    }
  }, [])

  if (loading) return <PageLoading label="Проверяем настройки поиска…" />

  return (
    <section className="page-content">
      <span className="eyebrow">ПОИСК ПОСТАВЩИКОВ</span>
      <div className="page-heading">
        <div>
          <h1>Найдите тех, кто поставляет нужное</h1>
          <p className="lead">Поиск обращается к веб-источникам и сохраняет подтверждённые сведения в базе поставщиков.</p>
        </div>
      </div>

      {error && (
        <ErrorNotice title="Не удалось проверить настройки поиска">
          Обновите страницу или откройте настройки интеграции.
        </ErrorNotice>
      )}

      {!error && !settings?.hasApiKey && (
        <Card className="discover-setup-card">
          <EmptyState
            title="Сначала настройте веб-поиск"
            description="Выберите провайдера, сохраните API-ключ и проверьте подключение. После этого можно будет искать новых поставщиков."
            action={<Link className="button primary" to="/settings/integration">Настроить интеграцию</Link>}
          />
        </Card>
      )}

      {!error && settings?.hasApiKey && (
        <Card className="discover-setup-card">
          <EmptyState
            title="Интеграция готова"
            description="Настройки провайдера сохранены. Форма поиска по запросу и фильтрам появится в следующем спринте."
            action={<Link className="button secondary" to="/settings/integration">Открыть настройки интеграции</Link>}
          />
        </Card>
      )}
    </section>
  )
}
