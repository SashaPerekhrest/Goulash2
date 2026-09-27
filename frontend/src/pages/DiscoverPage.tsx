import { useEffect, useRef, useState } from 'react'
import { Link, useLocation } from 'react-router-dom'
import {
  ApiError,
  discoverSuppliers,
  getAiSettings,
  setSupplierFavorite,
  type AiSettings,
  type DiscoverySearchRequest,
  type DiscoverySearchResponse,
} from '../shared/api/apiClient'
import { Card, EmptyState, ErrorNotice, FormField, LoadingIndicator, PageLoading, SafeExternalLink } from '../shared/ui'
import { compareDecimals, normalizeDecimal } from './discoveryDecimal'

type DiscoveryForm = {
  query: string
  city: string
  region: string
  category: string
  product: string
  priceMin: string
  priceMax: string
  currency: string
  priceUnit: string
  includeApproximatePrices: boolean
  maxDeliveryDays: string
  minMinimumOrder: string
  maxMinimumOrder: string
  minimumOrderUnit: string
}

type SearchSnapshot = {
  request: DiscoverySearchRequest
  signature: string
  summary: string[]
  hasSubstantiveConditions: boolean
  errors: Record<string, string>
}

type SearchIntent = SearchSnapshot & { revision: number; ready: boolean; autoSchedule: boolean }
type ActiveSearch = { controller: AbortController; revision: number; signature: string }
type DiscoveryFailure = { title: string; message: string; code: string }

const initialForm: DiscoveryForm = {
  query: '',
  city: '',
  region: '',
  category: '',
  product: '',
  priceMin: '',
  priceMax: '',
  currency: 'RUB',
  priceUnit: 'kg',
  includeApproximatePrices: false,
  maxDeliveryDays: '',
  minMinimumOrder: '',
  maxMinimumOrder: '',
  minimumOrderUnit: 'kg',
}

export function DiscoverPage() {
  const location = useLocation()
  const [form, setForm] = useState(initialForm)
  const formRef = useRef(initialForm)
  const [settings, setSettings] = useState<AiSettings | null>(null)
  const [settingsLoading, setSettingsLoading] = useState(true)
  const [settingsError, setSettingsError] = useState(false)
  const [intentRevision, setIntentRevision] = useState(0)
  const [loading, setLoading] = useState(false)
  const [failure, setFailure] = useState<DiscoveryFailure | null>(null)
  const [response, setResponse] = useState<DiscoverySearchResponse | null>(null)
  const [resultSnapshot, setResultSnapshot] = useState<SearchSnapshot | null>(null)
  const [lastRunSnapshot, setLastRunSnapshot] = useState<SearchSnapshot | null>(null)
  const [favoritePending, setFavoritePending] = useState<Set<string>>(() => new Set())
  const [favoriteError, setFavoriteError] = useState(false)

  const intentRef = useRef<SearchIntent | null>(null)
  const activeSearchRef = useRef<ActiveSearch | null>(null)
  const revisionRef = useRef(0)
  const mountedRef = useRef(true)
  const canSearchRef = useRef(false)
  const latestSnapshot = makeSearchSnapshot(form)
  const currentErrors = latestSnapshot.errors
  const canSearch = settings?.hasApiKey === true
  canSearchRef.current = canSearch

  if (intentRef.current === null) {
    intentRef.current = { ...makeSearchSnapshot(initialForm), revision: 0, ready: false, autoSchedule: false }
  }

  useEffect(() => {
    const controller = new AbortController()
    let active = true
    getAiSettings(controller.signal)
      .then((savedSettings) => {
        if (active) setSettings(savedSettings)
      })
      .catch((cause: unknown) => {
        if (active && !(cause instanceof ApiError && cause.status === 401)) setSettingsError(true)
      })
      .finally(() => {
        if (active) setSettingsLoading(false)
      })
    return () => {
      active = false
      controller.abort()
    }
  }, [])

  useEffect(() => {
    mountedRef.current = true
    return () => {
      mountedRef.current = false
      activeSearchRef.current?.controller.abort()
    }
  }, [])

  useEffect(() => {
    const intent = intentRef.current
    if (!intent || !intent.autoSchedule || settingsLoading || settingsError || !canSearch ||
      intent.signature !== latestSnapshot.signature ||
      !intent.hasSubstantiveConditions || Object.keys(intent.errors).length > 0) return

    const scheduledRevision = intent.revision
    const timer = window.setTimeout(() => {
      const latestIntent = intentRef.current
      if (!latestIntent || latestIntent.revision !== scheduledRevision) return
      latestIntent.ready = true
      startSearch(scheduledRevision)
    }, 700)

    return () => window.clearTimeout(timer)
  }, [intentRevision, latestSnapshot.signature, settingsError, settingsLoading, canSearch])

  function changeField<Field extends keyof DiscoveryForm>(field: Field, value: DiscoveryForm[Field]) {
    const previous = formRef.current
    if (previous[field] === value) return
    const next = { ...previous, [field]: value }
    formRef.current = next
    setForm(next)

    const nextSnapshot = makeSearchSnapshot(next)
    const currentIntent = intentRef.current
    if (!currentIntent || currentIntent.signature === nextSnapshot.signature) return

    const revision = ++revisionRef.current
    intentRef.current = { ...nextSnapshot, revision, ready: false, autoSchedule: true }
    setIntentRevision(revision)
    activeSearchRef.current?.controller.abort()
  }

  function startSearch(revision: number) {
    const intent = intentRef.current
    if (!intent || intent.revision !== revision || !intent.ready || activeSearchRef.current ||
      !canSearchRef.current || !intent.hasSubstantiveConditions || Object.keys(intent.errors).length > 0) return

    intent.ready = false
    intent.autoSchedule = false
    const controller = new AbortController()
    const activeSearch: ActiveSearch = { controller, revision, signature: intent.signature }
    activeSearchRef.current = activeSearch
    setLoading(true)
    setFailure(null)
    setFavoriteError(false)
    setLastRunSnapshot(intent)

    void discoverSuppliers(intent.request, controller.signal)
      .then((result) => {
        if (!mountedRef.current || controller.signal.aborted || intentRef.current?.revision !== revision) return
        setResponse(result)
        setResultSnapshot(intent)
      })
      .catch((cause: unknown) => {
        if (!mountedRef.current || controller.signal.aborted || intentRef.current?.revision !== revision) return
        setFailure(describeDiscoveryFailure(cause))
      })
      .finally(() => {
        if (activeSearchRef.current !== activeSearch) return
        activeSearchRef.current = null
        if (mountedRef.current) setLoading(false)

        const latestIntent = intentRef.current
        if (latestIntent?.ready) startSearch(latestIntent.revision)
      })
  }

  function handleImmediateSearch() {
    const snapshot = makeSearchSnapshot(formRef.current)
    if (!snapshot.hasSubstantiveConditions || Object.keys(snapshot.errors).length > 0 || !canSearchRef.current) return
    if (activeSearchRef.current && intentRef.current?.signature === snapshot.signature) return

    const revision = ++revisionRef.current
    intentRef.current = { ...snapshot, revision, ready: true, autoSchedule: false }
    setIntentRevision(revision)
    activeSearchRef.current?.controller.abort()
    startSearch(revision)
  }

  function repeatLastSearch() {
    if (!lastRunSnapshot || !canSearchRef.current) return
    const revision = ++revisionRef.current
    intentRef.current = { ...lastRunSnapshot, revision, ready: true, autoSchedule: false }
    setIntentRevision(revision)
    activeSearchRef.current?.controller.abort()
    startSearch(revision)
  }

  async function toggleFavorite(id: string, currentValue: boolean) {
    setFavoritePending((current) => new Set(current).add(id))
    setFavoriteError(false)
    try {
      const saved = await setSupplierFavorite(id, !currentValue)
      if (saved.id.toLowerCase() !== id.toLowerCase() || typeof saved.isFavorite !== 'boolean') {
        throw new ApiError('Сервер вернул некорректный ответ', 502, 'INVALID_RESPONSE')
      }
      setResponse((current) => current ? {
        ...current,
        items: current.items.map((item) => item.id.toLowerCase() === id.toLowerCase()
          ? { ...item, isFavorite: saved.isFavorite }
          : item),
      } : current)
    } catch (cause) {
      if (!(cause instanceof ApiError && cause.status === 401)) setFavoriteError(true)
    } finally {
      setFavoritePending((current) => {
        const next = new Set(current)
        next.delete(id)
        return next
      })
    }
  }

  if (settingsLoading) return <PageLoading label="Проверяем настройки поиска…" />

  const hasCurrentAttempt = lastRunSnapshot?.signature === latestSnapshot.signature
  const canSubmitCurrent = canSearch && latestSnapshot.hasSubstantiveConditions &&
    Object.keys(currentErrors).length === 0 && !hasCurrentAttempt

  return (
    <section className="page-content discover-page">
      <span className="eyebrow">ПОИСК ПОСТАВЩИКОВ</span>
      <div className="page-heading">
        <div>
          <h1>Найдите тех, кто поставляет нужное</h1>
          <p className="lead">Поиск обращается к веб-источникам и сохраняет подтверждённые сведения в базе поставщиков.</p>
        </div>
      </div>

      {settingsError && (
        <ErrorNotice title="Не удалось проверить настройки поиска" action={<Link className="button secondary" to="/settings/integration">Открыть настройки интеграции</Link>}>
          Обновите страницу или проверьте настройки интеграции.
        </ErrorNotice>
      )}

      {!settingsError && !canSearch && (
        <Card className="discover-config-notice">
          <EmptyState
            title="Сначала настройте веб-поиск"
            description="Выберите провайдера, сохраните API-ключ и проверьте подключение. Форма останется доступна, а поиск запустится после настройки."
            action={<Link className="button primary" to="/settings/integration">Настроить интеграцию</Link>}
          />
        </Card>
      )}

      <Card className="discover-form-card">
        <form className="discover-form" noValidate onSubmit={(event) => {
          event.preventDefault()
          handleImmediateSearch()
        }}>
          <div className="discover-query-row">
            <FormField id="discovery-query" label="Что нужно найти?" hint="Опишите товар, тип поставщика или условия поиска.">
              <input
                id="discovery-query"
                className="text-input"
                type="text"
                maxLength={500}
                value={form.query}
                placeholder="Например, поставщики замороженных ягод"
                aria-invalid={Boolean(currentErrors.query)}
                aria-describedby={currentErrors.query ? 'discovery-query-error' : undefined}
                onChange={(event) => changeField('query', event.target.value)}
              />
              {currentErrors.query && <FieldError id="discovery-query-error">{currentErrors.query}</FieldError>}
            </FormField>
          </div>

          <div className="discover-filter-grid">
            <FormField id="discovery-city" label="Город">
              <input id="discovery-city" className="text-input" value={form.city} maxLength={160} placeholder="Екатеринбург"
                aria-invalid={Boolean(currentErrors.city)} onChange={(event) => changeField('city', event.target.value)} />
              {currentErrors.city && <FieldError>{currentErrors.city}</FieldError>}
            </FormField>
            <FormField id="discovery-region" label="Регион">
              <input id="discovery-region" className="text-input" value={form.region} maxLength={160} placeholder="Свердловская область"
                aria-invalid={Boolean(currentErrors.region)} onChange={(event) => changeField('region', event.target.value)} />
              {currentErrors.region && <FieldError>{currentErrors.region}</FieldError>}
            </FormField>
            <FormField id="discovery-category" label="Категория">
              <input id="discovery-category" className="text-input" value={form.category} maxLength={160} placeholder="Ингредиенты"
                aria-invalid={Boolean(currentErrors.category)} onChange={(event) => changeField('category', event.target.value)} />
              {currentErrors.category && <FieldError>{currentErrors.category}</FieldError>}
            </FormField>
            <FormField id="discovery-product" label="Товар">
              <input id="discovery-product" className="text-input" value={form.product} maxLength={500} placeholder="Малина"
                aria-invalid={Boolean(currentErrors.product)} onChange={(event) => changeField('product', event.target.value)} />
              {currentErrors.product && <FieldError>{currentErrors.product}</FieldError>}
            </FormField>
          </div>

          <div className="discover-condition-grid">
            <fieldset className="discover-fieldset">
              <legend>Диапазон цены</legend>
              <div className="discover-price-grid">
                <FormField id="discovery-price-min" label="От">
                  <input id="discovery-price-min" className="text-input" inputMode="decimal" value={form.priceMin} placeholder="100"
                    aria-invalid={Boolean(currentErrors.priceMin)} onChange={(event) => changeField('priceMin', event.target.value)} />
                  {currentErrors.priceMin && <FieldError>{currentErrors.priceMin}</FieldError>}
                </FormField>
                <FormField id="discovery-price-max" label="До">
                  <input id="discovery-price-max" className="text-input" inputMode="decimal" value={form.priceMax} placeholder="500"
                    aria-invalid={Boolean(currentErrors.priceMax)} onChange={(event) => changeField('priceMax', event.target.value)} />
                  {currentErrors.priceMax && <FieldError>{currentErrors.priceMax}</FieldError>}
                </FormField>
                <FormField id="discovery-price-currency" label="Валюта">
                  <input id="discovery-price-currency" className="text-input" value={form.currency} maxLength={3} placeholder="RUB"
                    aria-invalid={Boolean(currentErrors.currency)} onChange={(event) => changeField('currency', event.target.value)} />
                  {currentErrors.currency && <FieldError>{currentErrors.currency}</FieldError>}
                </FormField>
                <FormField id="discovery-price-unit" label="Единица цены">
                  <input id="discovery-price-unit" className="text-input" value={form.priceUnit} maxLength={40} placeholder="kg"
                    aria-invalid={Boolean(currentErrors.priceUnit)} onChange={(event) => changeField('priceUnit', event.target.value)} />
                  {currentErrors.priceUnit && <FieldError>{currentErrors.priceUnit}</FieldError>}
                </FormField>
              </div>
              <label className="checkbox-field">
                <input type="checkbox" checked={form.includeApproximatePrices}
                  onChange={(event) => changeField('includeApproximatePrices', event.target.checked)} />
                <span>Разрешить примерные цены</span>
              </label>
              <p className="field-hint">По умолчанию учитываются только точные цены. В результатах примерная цена всегда будет помечена.</p>
            </fieldset>

            <div className="discover-condition-stack">
              <FormField id="discovery-max-delivery" label="Максимальный срок доставки, дней">
                <input id="discovery-max-delivery" className="text-input" inputMode="numeric" value={form.maxDeliveryDays} placeholder="Например, 7"
                  aria-invalid={Boolean(currentErrors.maxDeliveryDays)} onChange={(event) => changeField('maxDeliveryDays', event.target.value)} />
                {currentErrors.maxDeliveryDays && <FieldError>{currentErrors.maxDeliveryDays}</FieldError>}
              </FormField>

              <fieldset className="discover-fieldset order-fieldset">
                <legend>Минимальный заказ</legend>
                <div className="discover-order-grid">
                  <FormField id="discovery-order-min" label="От">
                    <input id="discovery-order-min" className="text-input" inputMode="decimal" value={form.minMinimumOrder} placeholder="10"
                      aria-invalid={Boolean(currentErrors.minMinimumOrder)} onChange={(event) => changeField('minMinimumOrder', event.target.value)} />
                    {currentErrors.minMinimumOrder && <FieldError>{currentErrors.minMinimumOrder}</FieldError>}
                  </FormField>
                  <FormField id="discovery-order-max" label="До">
                    <input id="discovery-order-max" className="text-input" inputMode="decimal" value={form.maxMinimumOrder} placeholder="100"
                      aria-invalid={Boolean(currentErrors.maxMinimumOrder)} onChange={(event) => changeField('maxMinimumOrder', event.target.value)} />
                    {currentErrors.maxMinimumOrder && <FieldError>{currentErrors.maxMinimumOrder}</FieldError>}
                  </FormField>
                  <FormField id="discovery-order-unit" label="Единица">
                    <input id="discovery-order-unit" className="text-input" value={form.minimumOrderUnit} maxLength={40} placeholder="kg"
                      aria-invalid={Boolean(currentErrors.minimumOrderUnit)} onChange={(event) => changeField('minimumOrderUnit', event.target.value)} />
                    {currentErrors.minimumOrderUnit && <FieldError>{currentErrors.minimumOrderUnit}</FieldError>}
                  </FormField>
                </div>
              </fieldset>
            </div>
          </div>

          {latestSnapshot.hasSubstantiveConditions && Object.keys(currentErrors).length > 0 && (
            <p className="discover-validation-summary" role="alert">Исправьте отмеченные значения. Поиск не отправлен.</p>
          )}
          {!latestSnapshot.hasSubstantiveConditions && (
            <p className="discover-form-hint">Для поиска введите текст или укажите хотя бы один содержательный фильтр.</p>
          )}

          <div className="discover-form-actions">
            <button className="button primary" type="submit" disabled={!canSubmitCurrent}>
              {loading && activeSearchRef.current?.signature === latestSnapshot.signature ? 'Поиск выполняется…' : 'Найти'}
            </button>
            {lastRunSnapshot && !loading && (
              <button className="button secondary" type="button" onClick={repeatLastSearch} disabled={!canSearch}>
                Повторить последний поиск
              </button>
            )}
            {loading && <LoadingIndicator label="Можно изменить условия — запустится поиск по последним." />}
          </div>
        </form>
      </Card>

      {loading && lastRunSnapshot && (
        <Card className="discover-running-card">
          <div aria-live="polite" role="status">
            <div className="discover-running-heading">
              <LoadingIndicator label="Ищем поставщиков…" />
              <span>Запущенный поиск</span>
            </div>
            <SearchParameters snapshot={lastRunSnapshot} />
          </div>
        </Card>
      )}

      {failure && (
        <>
          <ErrorNotice title={failure.title} action={failure.code === 'PROVIDER_NOT_CONFIGURED'
            ? <Link className="button secondary" to="/settings/integration">Настроить интеграцию</Link>
            : lastRunSnapshot && <button className="button secondary" type="button" onClick={repeatLastSearch}>Повторить поиск</button>}>
            {failure.message}
          </ErrorNotice>
          {lastRunSnapshot && (
            <div className="discover-failed-parameters">
              <span>Параметры неудачного запуска:</span>
              <SearchParameters snapshot={lastRunSnapshot} />
            </div>
          )}
        </>
      )}

      {favoriteError && (
        <ErrorNotice title="Не удалось изменить избранное">
          Проверьте подключение и повторите действие. Состояние карточки не менялось.
        </ErrorNotice>
      )}

      {response && resultSnapshot && (
        <section className="discover-results" aria-live="polite">
          <div className="discover-results-heading">
            <div>
              <span className="section-kicker">РЕЗУЛЬТАТЫ ПОИСКА</span>
              <h2>{response.items.length === 0 ? 'Подходящие поставщики не найдены' : 'Найденные поставщики'}</h2>
              <p>Параметры именно этого запуска:</p>
            </div>
            <span className="discover-result-count">{response.acceptedCount} из 5</span>
          </div>
          <SearchParameters snapshot={resultSnapshot} />
          {response.rejectedCount > 0 && (
            <p className="discover-rejected-count" role="status">
              Не показано записей, которые не прошли проверку или условия: {response.rejectedCount}.
            </p>
          )}

          {response.items.length === 0 ? (
            <Card className="discover-empty-results">
              <EmptyState
                title="Попробуйте уточнить условия"
                description={response.outcome === 'no_sources'
                  ? 'Провайдер не вернул проверяемые веб-источники. Попробуйте повторить поиск или сменить модель.'
                  : response.outcome === 'model_empty'
                    ? 'Веб-источники найдены, но модель не выделила из них поставщиков. Попробуйте уточнить запрос.'
                    : response.rejectedCount > 0
                      ? 'Найденные записи не прошли проверку источников или заданные фильтры.'
                      : 'Подходящие компании не найдены.'}
              />
            </Card>
          ) : (
            <div className="discover-card-list">
              {response.items.map((item) => (
                <Card className="supplier-discovery-card" key={item.id}>
                  <div className="discovery-card-main">
                    <div className="discovery-card-heading">
                      <div>
                        <h3><Link to={`/suppliers/${encodeURIComponent(item.id)}`} state={{ from: `${location.pathname}${location.search}` }}>{item.name}</Link></h3>
                        <p className="discovery-card-location">{item.city ?? 'Местоположение не указано'}</p>
                      </div>
                      {item.hasUnconfirmedData && <span className="unconfirmed-badge">Есть неподтверждённые сведения</span>}
                    </div>

                    {item.products.length > 0 && (
                      <div className="discovery-products" aria-label="Товары">
                        {item.products.map((product, index) => <span className="product-chip" key={`${product}-${index}`}>{product}</span>)}
                      </div>
                    )}

                    <div className="discovery-conditions">
                      {item.pricePreview && (
                        <p><span>Цена</span><strong>{item.pricePreview}{item.priceIsApproximate && <em> · ориентировочно</em>}</strong></p>
                      )}
                      {item.deliveryPreview && <p><span>Доставка</span><strong>{item.deliveryPreview}</strong></p>}
                    </div>

                    {(item.websiteUrl || item.contactPreview) && (
                      <div className="discovery-contacts">
                        {item.websiteUrl && <SafeExternalLink className="safe-external-link" href={item.websiteUrl}>Открыть сайт</SafeExternalLink>}
                        {item.contactPreview && <span>{item.contactPreview}</span>}
                      </div>
                    )}

                    {item.lastDiscoveredAt && <p className="discovery-collected-date">Собрано {formatDate(item.lastDiscoveredAt)}</p>}
                  </div>

                  <div className="discovery-card-actions">
                    <button
                      className={item.isFavorite ? 'favorite-button selected' : 'favorite-button'}
                      type="button"
                      aria-label={item.isFavorite ? `Убрать ${item.name} из избранного` : `Добавить ${item.name} в избранное`}
                      aria-pressed={item.isFavorite}
                      disabled={favoritePending.has(item.id)}
                      onClick={() => void toggleFavorite(item.id, item.isFavorite)}
                    >
                      <span aria-hidden="true">{item.isFavorite ? '★' : '☆'}</span>
                      <span>{item.isFavorite ? 'В избранном' : 'В избранное'}</span>
                    </button>
                    <Link className="button quiet discovery-details-link" to={`/suppliers/${encodeURIComponent(item.id)}`} state={{ from: `${location.pathname}${location.search}` }}>Подробнее</Link>
                  </div>
                </Card>
              ))}
            </div>
          )}
        </section>
      )}

      {!response && !loading && !failure && !lastRunSnapshot && (
        <Card className="discover-empty-state">
          <EmptyState
            title="Начните с запроса или фильтра"
            description="Результаты появятся после запуска поиска. Пустая форма не отправляет запрос к провайдеру."
          />
        </Card>
      )}
    </section>
  )
}

function FieldError({ id, children }: { id?: string; children: string }) {
  return <span className="field-error" id={id}>{children}</span>
}

function SearchParameters({ snapshot }: { snapshot: SearchSnapshot }) {
  if (snapshot.summary.length === 0) return null
  return (
    <ul className="search-parameter-list">
      {snapshot.summary.map((parameter) => <li key={parameter}>{parameter}</li>)}
    </ul>
  )
}

function makeSearchSnapshot(form: DiscoveryForm): SearchSnapshot {
  const errors = validateForm(form)
  const query = form.query.trim()
  const city = form.city.trim()
  const region = form.region.trim()
  const category = form.category.trim()
  const product = form.product.trim()
  const priceMin = normalizeDecimal(form.priceMin)
  const priceMax = normalizeDecimal(form.priceMax)
  const hasPrice = priceMin !== null || priceMax !== null
  const minOrder = normalizeDecimal(form.minMinimumOrder)
  const maxOrder = normalizeDecimal(form.maxMinimumOrder)
  const hasOrder = minOrder !== null || maxOrder !== null
  const maxDeliveryDays = form.maxDeliveryDays.trim() ? Number(form.maxDeliveryDays.trim()) : null
  const hasSubstantiveConditions = Boolean(query || city || region || category || product || hasPrice || hasOrder || maxDeliveryDays !== null)

  const request: DiscoverySearchRequest = {
    query,
    filters: {
      city: city || null,
      region: region || null,
      category: category || null,
      product: product || null,
      price: hasPrice ? {
        min: priceMin,
        max: priceMax,
        currency: form.currency.trim().toUpperCase(),
        unit: form.priceUnit.trim(),
      } : null,
      includeApproximatePrices: hasPrice && form.includeApproximatePrices,
      maxDeliveryDays,
      minMinimumOrder: minOrder === null ? null : { amount: minOrder, unit: form.minimumOrderUnit.trim() },
      maxMinimumOrder: maxOrder === null ? null : { amount: maxOrder, unit: form.minimumOrderUnit.trim() },
    },
  }

  const summary: string[] = []
  if (query) summary.push(`Запрос: ${query}`)
  if (city) summary.push(`Город: ${city}`)
  if (region) summary.push(`Регион: ${region}`)
  if (category) summary.push(`Категория: ${category}`)
  if (product) summary.push(`Товар: ${product}`)
  if (hasPrice) {
    const range = [priceMin !== null ? `от ${priceMin}` : null, priceMax !== null ? `до ${priceMax}` : null].filter(Boolean).join(' ')
    summary.push(`Цена: ${range} ${form.currency.trim().toUpperCase()}/${form.priceUnit.trim()}`)
    if (form.includeApproximatePrices) summary.push('Учитываются примерные цены')
  }
  if (maxDeliveryDays !== null && Number.isInteger(maxDeliveryDays)) summary.push(`Доставка: не более ${maxDeliveryDays} дней`)
  if (minOrder !== null || maxOrder !== null) {
    const range = [minOrder !== null ? `от ${minOrder}` : null, maxOrder !== null ? `до ${maxOrder}` : null].filter(Boolean).join(' ')
    summary.push(`Минимальный заказ: ${range} ${form.minimumOrderUnit.trim()}`)
  }

  return { request, signature: JSON.stringify(request), summary, hasSubstantiveConditions, errors }
}

function validateForm(form: DiscoveryForm): Record<string, string> {
  const errors: Record<string, string> = {}
  if (form.query.trim().length > 500) errors.query = 'Запрос не должен быть длиннее 500 символов.'
  if (form.city.trim().length > 160) errors.city = 'Город не должен быть длиннее 160 символов.'
  if (form.region.trim().length > 160) errors.region = 'Регион не должен быть длиннее 160 символов.'
  if (form.category.trim().length > 160) errors.category = 'Категория не должна быть длиннее 160 символов.'
  if (form.product.trim().length > 500) errors.product = 'Товар не должен быть длиннее 500 символов.'

  const decimalFields: Array<[keyof DiscoveryForm, string]> = [
    ['priceMin', 'priceMin'], ['priceMax', 'priceMax'],
    ['minMinimumOrder', 'minMinimumOrder'], ['maxMinimumOrder', 'maxMinimumOrder'],
  ]
  for (const [field, key] of decimalFields) {
    const value = form[field]
    if (typeof value !== 'string' || !value.trim()) continue
    if (normalizeDecimal(value) === null) errors[key] = 'Введите неотрицательное число, например 12.50.'
  }

  const priceMin = normalizeDecimal(form.priceMin)
  const priceMax = normalizeDecimal(form.priceMax)
  if (priceMin !== null && priceMax !== null && compareDecimals(priceMin, priceMax) > 0) {
    errors.priceMax = 'Верхняя граница должна быть не меньше нижней.'
  }
  if (priceMin !== null || priceMax !== null) {
    if (!/^[a-z]{3}$/i.test(form.currency.trim())) errors.currency = 'Укажите трёхбуквенный код валюты, например RUB.'
    if (!form.priceUnit.trim() || form.priceUnit.trim().length > 40) errors.priceUnit = 'Укажите единицу цены длиной до 40 символов.'
  }
  if (form.priceUnit.trim().length > 40) errors.priceUnit = 'Единица цены не должна быть длиннее 40 символов.'

  const minOrder = normalizeDecimal(form.minMinimumOrder)
  const maxOrder = normalizeDecimal(form.maxMinimumOrder)
  if (minOrder !== null && maxOrder !== null && compareDecimals(minOrder, maxOrder) > 0) {
    errors.maxMinimumOrder = 'Верхняя граница должна быть не меньше нижней.'
  }
  if (minOrder !== null || maxOrder !== null) {
    if (!form.minimumOrderUnit.trim() || form.minimumOrderUnit.trim().length > 40) {
      errors.minimumOrderUnit = 'Укажите единицу минимального заказа длиной до 40 символов.'
    }
  }
  if (form.minimumOrderUnit.trim().length > 40) errors.minimumOrderUnit = 'Единица не должна быть длиннее 40 символов.'

  const days = form.maxDeliveryDays.trim()
  if (days && (!/^\d+$/.test(days) || !Number.isSafeInteger(Number(days)) || Number(days) > 2147483647)) {
    errors.maxDeliveryDays = 'Укажите целое неотрицательное число дней.'
  }
  return errors
}

function describeDiscoveryFailure(cause: unknown): DiscoveryFailure {
  if (cause instanceof ApiError) {
    if (cause.code === 'PROVIDER_NOT_CONFIGURED') {
      return { title: 'Провайдер веб-поиска не настроен', message: 'Сохраните ключ провайдера и проверьте интеграцию в настройках.', code: cause.code }
    }
    if (cause.code === 'PROVIDER_TIMEOUT' || cause.status === 504) {
      return { title: 'Поиск занял слишком много времени', message: 'Провайдер не ответил вовремя. Повторите поиск.', code: cause.code }
    }
    if (cause.code === 'PROVIDER_INVALID_RESPONSE' || cause.code === 'INVALID_RESPONSE') {
      return { title: 'Получен некорректный ответ', message: 'Ответ не удалось безопасно разобрать. Повторите поиск позже.', code: cause.code }
    }
    if (cause.code === 'VALIDATION_ERROR' || cause.status === 400) {
      return { title: 'Проверьте параметры поиска', message: cause.detail ?? 'Сервер отклонил параметры поиска.', code: cause.code }
    }
    if (cause.code === 'PROVIDER_UNAVAILABLE' || cause.status === 502 || cause.status >= 500) {
      return { title: 'Провайдер временно недоступен', message: 'Результаты не обновлены. Проверьте подключение и повторите поиск.', code: cause.code }
    }
    return { title: 'Не удалось выполнить поиск', message: cause.detail ?? 'Проверьте подключение и повторите поиск.', code: cause.code }
  }
  return { title: 'Сервис поиска недоступен', message: 'Проверьте подключение и попробуйте ещё раз.', code: 'NETWORK_ERROR' }
}

function formatDate(value: string): string {
  return new Intl.DateTimeFormat('ru-RU', { dateStyle: 'medium' }).format(new Date(value))
}
