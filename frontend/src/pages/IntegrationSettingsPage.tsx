import { useEffect, useMemo, useState, type FormEvent } from 'react'
import {
  ApiError,
  checkAiSettings,
  deleteAiApiKey,
  getAiProviders,
  getAiSettings,
  updateAiSettings,
  type AiProvider,
  type AiSettings,
} from '../shared/api/apiClient'
import { Card, EmptyState, ErrorNotice, FormField, LoadingIndicator, PageLoading } from '../shared/ui'

type CheckState = {
  connected: boolean | null
  webSearchAvailable: boolean | null
  checkedAt: string | null
  error: string | null
}

export function IntegrationSettingsPage() {
  const [providers, setProviders] = useState<AiProvider[]>([])
  const [settings, setSettings] = useState<AiSettings | null>(null)
  const [providerId, setProviderId] = useState('')
  const [model, setModel] = useState('')
  const [routeProvider, setRouteProvider] = useState('')
  const [basePrompt, setBasePrompt] = useState('')
  const [apiKey, setApiKey] = useState('')
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [formError, setFormError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const [deleting, setDeleting] = useState(false)
  const [checking, setChecking] = useState(false)
  const [checkState, setCheckState] = useState<CheckState | null>(null)

  useEffect(() => {
    const controller = new AbortController()
    let active = true
    setLoading(true)
    setLoadError(null)

    Promise.all([getAiProviders(controller.signal), getAiSettings(controller.signal)])
      .then(([providersResponse, savedSettings]) => {
        if (!active) return
        const availableProviders = providersResponse.items.filter((provider) => provider.supportsWebSearch)
        setProviders(availableProviders)
        setSettings(savedSettings)
        const initialProviderId = savedSettings.providerId ?? availableProviders[0]?.id ?? ''
        const initialProvider = availableProviders.find((provider) => provider.id === initialProviderId)
        setProviderId(initialProviderId)
        setModel(savedSettings.model ?? getRecommendedModel(initialProvider))
        setRouteProvider(savedSettings.routeProvider ?? '')
        setBasePrompt(savedSettings.basePrompt ?? initialProvider?.basePrompt ?? initialProvider?.defaultBasePrompt ?? '')
      })
      .catch(() => {
        if (active) setLoadError('Не удалось загрузить список провайдеров и сохранённые настройки.')
      })
      .finally(() => {
        if (active) setLoading(false)
      })

    return () => {
      active = false
      controller.abort()
    }
  }, [])

  const selectedProvider = providers.find((provider) => provider.id === providerId)
  const needsNewKey = !settings?.hasApiKey || (settings.providerId !== null && providerId !== settings.providerId)
  const hasUnsavedChanges = Boolean(settings) && (
    providerId !== settings?.providerId || model !== settings?.model ||
    routeProvider !== (settings?.routeProvider ?? '') ||
    basePrompt !== (settings?.basePrompt ?? '') || apiKey.length > 0
  )
  const canCheck = Boolean(settings?.hasApiKey) && !hasUnsavedChanges && !checking && !saving && !deleting

  const connectionLabel = useMemo(() => {
    if (checkState?.error) return 'Не удалось подтвердить'
    if (checkState?.connected === true) return 'Соединение установлено'
    if (checkState?.connected === false) return 'Нет соединения'
    return 'Ещё не проверено'
  }, [checkState])

  async function handleSave(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setFormError(null)
    if (!providerId || !selectedProvider || !model.trim()) {
      setFormError(selectedProvider?.supportsFreeformModel
        ? 'Укажите провайдера и ID модели.'
        : 'Выберите провайдера и модель из списка.')
      return
    }
    if (!basePrompt.trim()) {
      setFormError('Базовый промпт не может быть пустым.')
      return
    }
    if (needsNewKey && apiKey.trim().length === 0) {
      setFormError('Для выбранного провайдера введите API-ключ.')
      return
    }

    setSaving(true)
    setCheckState(null)
    try {
      const saved = await updateAiSettings({
        providerId,
        model: model.trim(),
        basePrompt,
        ...(selectedProvider.supportsProviderRouting ? { routeProvider: routeProvider.trim() } : {}),
        ...(apiKey.length > 0 ? { apiKey } : {}),
      })
      setSettings(saved)
      setProviderId(saved.providerId ?? '')
      setModel(saved.model ?? '')
      setRouteProvider(saved.routeProvider ?? '')
      setBasePrompt(saved.basePrompt ?? selectedProvider.defaultBasePrompt)
      setProviders((current) => current.map((provider) => ({
        ...provider, basePrompt: saved.basePrompt ?? provider.defaultBasePrompt,
      })))
      setApiKey('')
    } catch (cause) {
      setFormError(getSettingsError(cause, 'Не удалось сохранить настройки.'))
    } finally {
      setSaving(false)
    }
  }

  async function handleDeleteKey() {
    setFormError(null)
    setCheckState(null)
    setDeleting(true)
    try {
      await deleteAiApiKey()
      setSettings((current) => current ? { ...current, hasApiKey: false, apiKeyMask: null, updatedAt: null } : current)
      setApiKey('')
    } catch (cause) {
      setFormError(getSettingsError(cause, 'Не удалось удалить API-ключ.'))
    } finally {
      setDeleting(false)
    }
  }

  async function handleCheck() {
    setChecking(true)
    setCheckState(null)
    try {
      const result = await checkAiSettings()
      setCheckState({
        connected: result.connected,
        webSearchAvailable: result.webSearchAvailable,
        checkedAt: result.checkedAt,
        error: null,
      })
    } catch (cause) {
      setCheckState({
        connected: null,
        webSearchAvailable: null,
        checkedAt: null,
        error: getCheckError(cause),
      })
    } finally {
      setChecking(false)
    }
  }

  function handleProviderChange(nextProviderId: string) {
    const nextProvider = providers.find((provider) => provider.id === nextProviderId)
    setProviderId(nextProviderId)
    setModel(getRecommendedModel(nextProvider))
    setRouteProvider('')
    setApiKey('')
    setFormError(null)
    setCheckState(null)
  }

  if (loading) return <PageLoading label="Загружаем настройки интеграции…" />

  return (
    <section className="page-content settings-page">
      <span className="eyebrow">ИНТЕГРАЦИЯ</span>
      <div className="page-heading">
        <div>
          <h1>Настройки провайдера</h1>
          <p className="lead">Подключите провайдера с веб-поиском, чтобы искать новых поставщиков по актуальным источникам.</p>
        </div>
      </div>

      {loadError && (
        <ErrorNotice title="Настройки недоступны" action={
          <button className="button secondary" type="button" onClick={() => window.location.reload()}>
            Обновить страницу
          </button>
        }>
          {loadError}
        </ErrorNotice>
      )}

      {!loadError && providers.length === 0 && (
        <Card className="settings-card">
          <EmptyState
            title="Нет доступных провайдеров"
            description="Сервер пока не зарегистрировал адаптер с поддержкой веб-поиска. Настройки можно будет сохранить, когда адаптер станет доступен."
          />
        </Card>
      )}

      {!loadError && providers.length > 0 && (
        <div className="settings-grid">
          <Card className="settings-card">
            <div className="card-heading">
              <div>
                <span className="section-kicker">ПРОВАЙДЕР И ДОСТУП</span>
                <h2>Конфигурация</h2>
              </div>
              {settings?.updatedAt && <span className="updated-badge">Сохранено</span>}
            </div>
            <form className="form-stack" onSubmit={handleSave}>
              <FormField id="provider" label="Адаптер" hint="Доступны только серверные адаптеры с веб-поиском.">
                <select
                  id="provider"
                  className="text-input select-input"
                  value={providerId}
                  onChange={(event) => handleProviderChange(event.target.value)}
                  disabled={saving || deleting || checking}
                  required
                >
                  {providers.map((provider) => <option key={provider.id} value={provider.id}>{provider.displayName}</option>)}
                </select>
              </FormField>

              <FormField
                id="model"
                label={selectedProvider?.supportsFreeformModel ? 'ID модели' : 'Модель'}
                hint={selectedProvider?.supportsFreeformModel
                  ? 'Введите ID из каталога Polza, например openai/gpt-4o.'
                  : undefined}
              >
                {selectedProvider?.supportsFreeformModel ? (
                  <input
                    id="model"
                    className="text-input"
                    type="text"
                    autoComplete="off"
                    maxLength={200}
                    value={model}
                    onChange={(event) => {
                      setModel(event.target.value)
                      setFormError(null)
                      setCheckState(null)
                    }}
                    placeholder={selectedProvider.models[0] ?? 'vendor/model'}
                    disabled={saving || deleting || checking}
                    required
                  />
                ) : (
                  <select
                    id="model"
                    className="text-input select-input"
                    value={model}
                    onChange={(event) => {
                      setModel(event.target.value)
                      setCheckState(null)
                    }}
                    disabled={saving || deleting || checking || !selectedProvider?.models.length}
                    required
                  >
                    {selectedProvider?.models.map((item) => <option key={item} value={item}>{item}</option>)}
                  </select>
                )}
              </FormField>

              {selectedProvider?.supportsProviderRouting && (
                <FormField
                  id="route-provider"
                  label="Провайдер модели (необязательно)"
                  hint="Добавляется к модели в формате @provider=… . Оставьте пустым для автоматического выбора Polza."
                >
                  <input
                    id="route-provider"
                    className="text-input"
                    type="text"
                    autoComplete="off"
                    maxLength={120}
                    value={routeProvider}
                    onChange={(event) => {
                      setRouteProvider(event.target.value)
                      setFormError(null)
                      setCheckState(null)
                    }}
                    placeholder="Например, DeepInfra"
                    disabled={saving || deleting || checking}
                  />
                </FormField>
              )}

              <FormField
                id="base-prompt"
                label="Базовый промпт"
                hint={selectedProvider?.id === 'polza'
                  ? 'Применяется при извлечении данных из найденных страниц. Сам веб-поиск строится по запросу и фильтрам.'
                  : 'Инструкция передаётся модели вместе с запросом и фильтрами при поиске поставщиков.'}
              >
                <textarea
                  id="base-prompt"
                  className="text-input textarea-input"
                  rows={10}
                  maxLength={8000}
                  value={basePrompt}
                  onChange={(event) => {
                    setBasePrompt(event.target.value)
                    setFormError(null)
                    setCheckState(null)
                  }}
                  disabled={saving || deleting || checking}
                  required
                />
                <div className="form-actions">
                  <span className="inline-note">{basePrompt.length} / 8000</span>
                  <button
                    className="button secondary"
                    type="button"
                    onClick={() => {
                      setBasePrompt(selectedProvider?.defaultBasePrompt ?? '')
                      setFormError(null)
                      setCheckState(null)
                    }}
                    disabled={saving || deleting || checking || !selectedProvider}
                  >
                    Восстановить стандартный
                  </button>
                </div>
              </FormField>

              <FormField
                id="api-key"
                label={settings?.hasApiKey ? 'Заменить API-ключ' : 'API-ключ'}
                hint={settings?.hasApiKey
                  ? `Сохранённый ключ: ${settings.apiKeyMask ?? '••••••••'}. Полный ключ недоступен для просмотра.`
                  : 'Ключ отправляется только на сервер и не сохраняется в браузере.'}
              >
                <input
                  id="api-key"
                  className="text-input"
                  type="password"
                  autoComplete="off"
                  value={apiKey}
                  onChange={(event) => {
                    setApiKey(event.target.value)
                    setFormError(null)
                    setCheckState(null)
                  }}
                  placeholder={needsNewKey ? 'Введите новый API-ключ' : 'Оставьте пустым, чтобы сохранить текущий ключ'}
                  disabled={saving || deleting || checking}
                  required={needsNewKey}
                />
              </FormField>

              {needsNewKey && settings?.providerId && providerId !== settings.providerId && (
                <p className="inline-note">При смене провайдера нужен отдельный API-ключ для нового адаптера.</p>
              )}
              {formError && <ErrorNotice title="Ошибка интеграции">{formError}</ErrorNotice>}

              <div className="form-actions">
                <button className="button primary" type="submit" disabled={saving || deleting || checking}>
                  {saving ? <LoadingIndicator label="Сохраняем…" /> : 'Сохранить настройки'}
                </button>
                {settings?.hasApiKey && (
                  <button className="button danger-quiet" type="button" onClick={() => void handleDeleteKey()} disabled={saving || deleting || checking}>
                    {deleting ? 'Удаляем…' : 'Удалить ключ'}
                  </button>
                )}
              </div>
            </form>
            {settings?.updatedAt && <p className="settings-updated">Последнее изменение: {formatDate(settings.updatedAt)}</p>}
          </Card>

          <Card className="settings-card check-card">
            <div className="card-heading">
              <div>
                <span className="section-kicker">ДОСТУП К ИСТОЧНИКАМ</span>
                <h2>Проверка подключения</h2>
              </div>
              <span className={`connection-indicator ${checkState?.connected ? 'connected' : checkState?.error ? 'unknown' : ''}`} aria-hidden="true" />
            </div>
            <p className="check-description">Проверка выполняет тот же поиск и разбор JSON, что и поиск поставщиков. Она может занять несколько минут.</p>
            <div className="check-results" aria-live="polite">
              <CheckRow label="Соединение" value={connectionLabel} success={checkState?.connected === true} />
              <CheckRow
                label="Веб-поиск"
                value={getWebSearchLabel(checkState)}
                success={checkState?.webSearchAvailable === true}
              />
            </div>
            {checkState?.error && <ErrorNotice title="Проверка не пройдена">{checkState.error}</ErrorNotice>}
            {checkState?.checkedAt && <p className="settings-updated">Проверено: {formatDate(checkState.checkedAt)}</p>}
            {!settings?.hasApiKey && <p className="inline-note">Сначала сохраните провайдера и API-ключ.</p>}
            {hasUnsavedChanges && settings?.hasApiKey && <p className="inline-note">Сохраните изменения перед проверкой.</p>}
            <button className="button secondary full-width" type="button" onClick={() => void handleCheck()} disabled={!canCheck}>
              {checking ? <LoadingIndicator label="Проверяем…" /> : 'Проверить подключение'}
            </button>
          </Card>
        </div>
      )}
    </section>
  )
}

function CheckRow({ label, value, success }: { label: string; value: string; success: boolean }) {
  return (
    <div className="check-row">
      <span>{label}</span>
      <strong className={success ? 'check-success' : ''}>{value}</strong>
    </div>
  )
}

function getRecommendedModel(provider?: AiProvider) {
  if (!provider?.models.length) return ''
  return provider.id === 'perplexity' && provider.models.includes('sonar-pro')
    ? 'sonar-pro'
    : provider.models[0]
}

function getSettingsError(error: unknown, fallback: string) {
  if (!(error instanceof ApiError)) return fallback
  switch (error.code) {
    case 'API_KEY_REQUIRED': return 'Введите новый ключ для выбранного провайдера.'
    case 'UNSUPPORTED_PROVIDER': return 'Выбранный адаптер больше недоступен. Обновите список провайдеров.'
    case 'UNSUPPORTED_MODEL': return 'Эта модель не поддерживается выбранным адаптером.'
    case 'VALIDATION_ERROR': return 'Проверьте провайдера, модель, маршрут и API-ключ.'
    case 'CSRF_INVALID': return 'Не удалось подтвердить запрос. Повторите попытку.'
    case 'UNAUTHORIZED': return 'Сессия завершилась. Войдите снова.'
    default: return fallback
  }
}

function getCheckError(error: unknown) {
  if (!(error instanceof ApiError)) return 'Не удалось связаться с сервером. Проверьте соединение и повторите проверку.'
  switch (error.code) {
    case 'PROVIDER_NOT_CONFIGURED': return 'Провайдер или API-ключ ещё не сохранён.'
    case 'PROVIDER_TIMEOUT': return 'Провайдер не ответил вовремя. Попробуйте ещё раз.'
    case 'PROVIDER_INVALID_RESPONSE': return 'Провайдер вернул ответ, который не удалось проверить.'
    case 'PROVIDER_UNAVAILABLE': return 'Провайдер недоступен или не подтвердил доступ к веб-источникам.'
    case 'UNSUPPORTED_MODEL': return 'Выбранная модель не поддерживается провайдером.'
    case 'CSRF_INVALID': return 'Не удалось подтвердить запрос. Повторите проверку.'
    default: return 'Не удалось проверить провайдера. Проверьте ключ и повторите попытку.'
  }
}

function getWebSearchLabel(result: CheckState | null) {
  if (result?.error) return 'Не удалось подтвердить'
  if (result?.webSearchAvailable === true) return 'Доступен'
  if (result?.webSearchAvailable === false) return 'Недоступен'
  return 'Ещё не проверен'
}

function formatDate(value: string) {
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return value
  return new Intl.DateTimeFormat('ru-RU', { dateStyle: 'medium', timeStyle: 'short' }).format(date)
}
