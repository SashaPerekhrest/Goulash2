import { useEffect, useMemo, useState } from 'react'
import { Link, useLocation, useSearchParams } from 'react-router-dom'
import {
  ApiError,
  deleteSupplier,
  getSuppliers,
  setSupplierFavorite,
  type SupplierCatalogPage,
} from '../shared/api/apiClient'
import { Card, EmptyState, ErrorNotice, FormField, LoadingIndicator, PageLoading, SafeExternalLink } from '../shared/ui'
import { useComparison } from '../shared/comparison/ComparisonContext'

const filterKeys = [
  'q', 'city', 'region', 'category', 'product', 'priceMin', 'priceMax', 'currency', 'unit',
  'includeApproximatePrices', 'maxDeliveryDays', 'minMinimumOrder', 'maxMinimumOrder', 'minimumOrderUnit',
  'favoriteOnly', 'sort', 'page', 'pageSize',
]
const substantiveFilterKeys = [
  'q', 'city', 'region', 'category', 'product', 'priceMin', 'priceMax', 'maxDeliveryDays',
  'minMinimumOrder', 'maxMinimumOrder', 'favoriteOnly',
]

export function SuppliersPage() {
  const comparison = useComparison()
  const [searchParams, setSearchParams] = useSearchParams()
  const location = useLocation()
  const searchKey = searchParams.toString()
  const requestParams = useMemo(() => {
    const params = new URLSearchParams()
    const current = new URLSearchParams(searchKey)
    for (const key of filterKeys) {
      const value = current.get(key)
      if (value !== null && value !== '') params.set(key, value)
    }
    if (params.has('priceMin') || params.has('priceMax')) {
      if (!params.has('currency')) params.set('currency', 'RUB')
      if (!params.has('unit')) params.set('unit', 'kg')
    }
    if ((params.has('minMinimumOrder') || params.has('maxMinimumOrder')) && !params.has('minimumOrderUnit')) {
      params.set('minimumOrderUnit', 'kg')
    }
    if (!params.has('page')) params.set('page', '1')
    if (!params.has('pageSize')) params.set('pageSize', '20')
    return params
  }, [searchKey])

  const [result, setResult] = useState<SupplierCatalogPage | null>(null)
  const [baseHasSuppliers, setBaseHasSuppliers] = useState<boolean | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [favoriteError, setFavoriteError] = useState<string | null>(null)
  const [favoritePending, setFavoritePending] = useState<Set<string>>(() => new Set())
  const [deletePending, setDeletePending] = useState<Set<string>>(() => new Set())
  const [revision, setRevision] = useState(0)
  const [filtersExpanded, setFiltersExpanded] = useState(false)

  useEffect(() => {
    const controller = new AbortController()
    setLoading(true)
    setError(null)
    setBaseHasSuppliers(null)
    getSuppliers(requestParams, controller.signal)
      .then(async (page) => {
        if (controller.signal.aborted) return
        setResult(page)
        if (page.totalItems > 0) {
          setBaseHasSuppliers(true)
          return
        }
        const hasFilters = substantiveFilterKeys.some((key) => {
          const value = requestParams.get(key)
          return key === 'favoriteOnly' ? value === 'true' : value !== null && value !== ''
        })
        if (!hasFilters) {
          setBaseHasSuppliers(false)
          return
        }
        const basePage = await getSuppliers(new URLSearchParams({ page: '1', pageSize: '10' }), controller.signal)
        if (!controller.signal.aborted) setBaseHasSuppliers(basePage.totalItems > 0)
      })
      .catch((cause: unknown) => {
        if (controller.signal.aborted) return
        setResult(null)
        setBaseHasSuppliers(null)
        setError(cause instanceof ApiError && cause.status === 400
          ? cause.detail ?? 'Проверьте параметры фильтрации.'
          : 'Не удалось загрузить базу поставщиков. Проверьте подключение и попробуйте ещё раз.')
      })
      .finally(() => { if (!controller.signal.aborted) setLoading(false) })
    return () => controller.abort()
  }, [requestParams, revision])

  function updateParam(key: string, value: string, isPage = false) {
    setSearchParams((previous) => {
      const next = new URLSearchParams(previous)
      if (value === '') next.delete(key)
      else next.set(key, value)
      if (!isPage && key !== 'page') next.set('page', '1')
      return next
    }, { replace: true })
  }

  async function toggleFavorite(id: string, currentValue: boolean) {
    setFavoritePending((previous) => new Set(previous).add(id))
    setFavoriteError(null)
    try {
      const response = await setSupplierFavorite(id, !currentValue)
      setResult((current) => current ? {
        ...current,
        items: current.items.map((item) => item.id === id ? { ...item, isFavorite: response.isFavorite } : item),
      } : current)
      setRevision((value) => value + 1)
    } catch {
      setFavoriteError('Не удалось подтвердить изменение избранного. Обновите список и попробуйте ещё раз.')
    } finally {
      setFavoritePending((previous) => {
        const next = new Set(previous)
        next.delete(id)
        return next
      })
    }
  }

  async function removeSupplier(id: string, name: string) {
    if (!window.confirm(`Удалить поставщика «${name}» и все связанные сведения из базы?`)) return
    setDeletePending((current) => new Set(current).add(id))
    setError(null)
    try {
      await deleteSupplier(id)
      comparison.removeSupplier(id)
      setRevision((value) => value + 1)
    } catch (cause) {
      setError(cause instanceof ApiError && cause.status === 404
        ? 'Поставщик уже удалён.'
        : 'Не удалось удалить поставщика. Обновите список и попробуйте ещё раз.')
    } finally {
      setDeletePending((current) => {
        const next = new Set(current)
        next.delete(id)
        return next
      })
    }
  }

  const read = (key: string, fallback = '') => searchParams.get(key) ?? fallback
  const isFiltered = substantiveFilterKeys.some((key) => {
    const value = requestParams.get(key)
    return key === 'favoriteOnly' ? value === 'true' : value !== null && value !== ''
  })

  if (loading && !result) return <PageLoading label="Загружаем базу поставщиков…" />

  return (
    <section className="page-content suppliers-page">
      <span className="eyebrow">ВАША БАЗА</span>
      <div className="page-heading">
        <div>
          <h1>Поставщики</h1>
          <p className="lead">Сохранённые компании для повторного поиска и сравнения условий.</p>
        </div>
        {loading && <LoadingIndicator label="Обновляем список…" />}
      </div>

      <Card className="catalog-filter-card">
        <div className="catalog-filter-heading">
          <div><span className="section-kicker">ПОИСК ПО БАЗЕ</span><h2>Найдите подходящих поставщиков</h2></div>
          <div className="catalog-filter-actions">
            <button className="button secondary" type="button" aria-expanded={filtersExpanded} aria-controls="catalog-filters"
              onClick={() => setFiltersExpanded((expanded) => !expanded)}>
              Фильтры <span aria-hidden="true">{filtersExpanded ? '−' : '+'}</span>
            </button>
            <button className="button quiet" type="button" onClick={() => setSearchParams({}, { replace: true })}>Сбросить фильтры</button>
          </div>
        </div>
        <div className="catalog-query-row">
          <FormField id="catalog-query" label="Название или товар">
            <input id="catalog-query" className="text-input" type="search" maxLength={500}
              placeholder="Например, поставщики замороженной малины" value={read('q')}
              onChange={(event) => updateParam('q', event.target.value)} />
          </FormField>
        </div>
        {filtersExpanded && <div id="catalog-filters" className="catalog-filter-panel">
        <div className="catalog-filter-grid">
          <FormField id="catalog-city" label="Город"><input id="catalog-city" className="text-input" maxLength={160} value={read('city')} onChange={(event) => updateParam('city', event.target.value)} /></FormField>
          <FormField id="catalog-region" label="Регион"><input id="catalog-region" className="text-input" maxLength={160} value={read('region')} onChange={(event) => updateParam('region', event.target.value)} /></FormField>
          <FormField id="catalog-category" label="Категория"><input id="catalog-category" className="text-input" maxLength={160} value={read('category')} onChange={(event) => updateParam('category', event.target.value)} /></FormField>
          <FormField id="catalog-product" label="Товар"><input id="catalog-product" className="text-input" maxLength={500} value={read('product')} onChange={(event) => updateParam('product', event.target.value)} /></FormField>
        </div>

        <div className="catalog-condition-grid">
          <fieldset className="discover-fieldset">
            <legend>Цена</legend>
            <div className="discover-price-grid">
              <FormField id="catalog-price-min" label="От"><input id="catalog-price-min" className="text-input" type="number" min="0" step="any" value={read('priceMin')} onChange={(event) => updateParam('priceMin', event.target.value)} /></FormField>
              <FormField id="catalog-price-max" label="До"><input id="catalog-price-max" className="text-input" type="number" min="0" step="any" value={read('priceMax')} onChange={(event) => updateParam('priceMax', event.target.value)} /></FormField>
              <FormField id="catalog-currency" label="Валюта"><input id="catalog-currency" className="text-input" maxLength={3} value={read('currency', 'RUB')} onChange={(event) => updateParam('currency', event.target.value.toUpperCase())} /></FormField>
              <FormField id="catalog-unit" label="Единица"><input id="catalog-unit" className="text-input" maxLength={40} value={read('unit', 'kg')} onChange={(event) => updateParam('unit', event.target.value)} /></FormField>
            </div>
            <label className="checkbox-field"><input type="checkbox" checked={read('includeApproximatePrices') === 'true'} onChange={(event) => updateParam('includeApproximatePrices', event.target.checked ? 'true' : 'false')} />Учитывать примерные цены</label>
          </fieldset>

          <div className="catalog-condition-stack">
            <fieldset className="discover-fieldset">
              <legend>Доставка</legend>
              <FormField id="catalog-delivery-days" label="Максимальный срок, дней"><input id="catalog-delivery-days" className="text-input" type="number" min="0" step="1" value={read('maxDeliveryDays')} onChange={(event) => updateParam('maxDeliveryDays', event.target.value)} /></FormField>
            </fieldset>
            <fieldset className="discover-fieldset">
              <legend>Минимальный заказ</legend>
              <div className="discover-order-grid">
                <FormField id="catalog-order-min" label="От"><input id="catalog-order-min" className="text-input" type="number" min="0" step="any" value={read('minMinimumOrder')} onChange={(event) => updateParam('minMinimumOrder', event.target.value)} /></FormField>
                <FormField id="catalog-order-max" label="До"><input id="catalog-order-max" className="text-input" type="number" min="0" step="any" value={read('maxMinimumOrder')} onChange={(event) => updateParam('maxMinimumOrder', event.target.value)} /></FormField>
                <FormField id="catalog-order-unit" label="Единица"><input id="catalog-order-unit" className="text-input" maxLength={40} value={read('minimumOrderUnit', 'kg')} onChange={(event) => updateParam('minimumOrderUnit', event.target.value)} /></FormField>
              </div>
            </fieldset>
          </div>
        </div>

        <div className="catalog-toolbar">
          <label className="checkbox-field catalog-favorite-filter"><input type="checkbox" checked={read('favoriteOnly') === 'true'} onChange={(event) => updateParam('favoriteOnly', event.target.checked ? 'true' : 'false')} />Только избранное</label>
          <FormField id="catalog-sort" label="Сортировка">
            <select id="catalog-sort" className="text-input select-input" value={read('sort', 'created_desc')} onChange={(event) => updateParam('sort', event.target.value)}>
              <option value="created_desc">Сначала недавно добавленные</option>
              <option value="created_asc">Сначала давно добавленные</option>
              <option value="name_asc">По названию А–Я</option>
              <option value="name_desc">По названию Я–А</option>
            </select>
          </FormField>
          <FormField id="catalog-page-size" label="На странице">
            <select id="catalog-page-size" className="text-input select-input" value={read('pageSize', '20')} onChange={(event) => updateParam('pageSize', event.target.value)}>
              <option value="10">10</option><option value="20">20</option><option value="50">50</option>
            </select>
          </FormField>
        </div>
        </div>}
      </Card>

      {favoriteError && <ErrorNotice title={favoriteError}>Попробуйте изменить избранное ещё раз.</ErrorNotice>}
      {error && <ErrorNotice title={error} action={<button className="button secondary" type="button" onClick={() => setRevision((value) => value + 1)}>Повторить</button>}>Проверьте подключение и параметры фильтрации.</ErrorNotice>}

      {result && !error && <section className="catalog-results" aria-live="polite">
        <div className="catalog-results-heading">
          <div><span className="section-kicker">СОХРАНЁННЫЕ КОМПАНИИ</span><h2>{supplierCountLabel(result.totalItems)}</h2></div>
          {result.totalItems > 0 && <span className="catalog-result-count">{result.totalItems} в базе</span>}
        </div>

        {result.totalItems === 0 ? (
          <Card className="catalog-empty-card"><EmptyState
            title={baseHasSuppliers === false ? 'База пока пуста' : isFiltered ? 'Нет совпадений по фильтрам' : 'Поставщики не найдены'}
            description={baseHasSuppliers === false
              ? 'После нового поиска сохранённые компании появятся здесь.'
              : 'Измените условия поиска или сбросьте фильтры, чтобы увидеть сохранённые компании.'}
            action={isFiltered ? <button className="button secondary" type="button" onClick={() => setSearchParams({}, { replace: true })}>Сбросить фильтры</button> : undefined}
          /></Card>
        ) : result.items.length === 0 ? (
          <Card className="catalog-empty-card"><EmptyState
            title={`На странице ${read('page', '1')} нет поставщиков`}
            description={`В базе ${result.totalItems} записей на ${result.totalPages} страницах. Перейдите к последней доступной странице.`}
            action={<button className="button secondary" type="button" onClick={() => updateParam('page', String(result.totalPages), true)}>Открыть страницу {result.totalPages}</button>}
          /></Card>
        ) : <div className="discover-card-list catalog-card-list">
          {result.items.map((item) => <Card className="supplier-discovery-card" key={item.id}>
            <div className="discovery-card-main">
              <div className="discovery-card-heading">
                <div><h3><Link to={`/suppliers/${encodeURIComponent(item.id)}`} state={{ from: `${location.pathname}${location.search}` }}>{item.name}</Link></h3>
                  <p className="discovery-card-location">{item.city ?? 'Город не указан'}</p></div>
              </div>
              <div className="discovery-products" aria-label="Товары">
                {item.products.length > 0 ? item.products.map((product, index) => <span className="product-chip" key={`${product}-${index}`}>{product}</span>) : <span className="catalog-no-products">Товары не указаны</span>}
              </div>
              <div className="discovery-conditions">
                <p><span>Цена</span><strong>{item.pricePreview ? <>{item.pricePreview}{item.priceIsApproximate && <em> · ориентировочно</em>}</> : 'Нет данных'}</strong></p>
                <p><span>Доставка</span><strong>{item.deliveryPreview ?? 'Нет данных'}</strong></p>
              </div>
              {(item.websiteUrl || item.contactPreview) && <div className="discovery-contacts">
                {item.websiteUrl && <SafeExternalLink className="safe-external-link" href={item.websiteUrl}>Открыть сайт</SafeExternalLink>}
                {item.contactPreview && <span>{item.contactPreview}</span>}
              </div>}
            </div>
            <div className="discovery-card-actions">
              <button className={item.isFavorite ? 'favorite-button selected' : 'favorite-button'} type="button"
                aria-label={item.isFavorite ? `Убрать ${item.name} из избранного` : `Добавить ${item.name} в избранное`}
                aria-pressed={item.isFavorite} disabled={favoritePending.has(item.id)}
                onClick={() => void toggleFavorite(item.id, item.isFavorite)}>
                <span aria-hidden="true">{item.isFavorite ? '★' : '☆'}</span>
                <span>{favoritePending.has(item.id) ? 'Сохраняем…' : item.isFavorite ? 'В избранном' : 'В избранное'}</span>
              </button>
              <button className={comparison.hasSupplier(item.id) ? 'button secondary comparison-button selected' : 'button secondary comparison-button'} type="button"
                aria-pressed={comparison.hasSupplier(item.id)}
                onClick={() => comparison.hasSupplier(item.id) ? comparison.removeSupplier(item.id) : comparison.addSupplier(item.id)}>
                {comparison.hasSupplier(item.id) ? 'Убрать из сравнения' : 'Сравнить'}
              </button>
              <Link className="button quiet discovery-details-link" to={`/suppliers/${encodeURIComponent(item.id)}`} state={{ from: `${location.pathname}${location.search}` }}>Подробнее</Link>
              <button className="button danger-quiet" type="button" disabled={deletePending.has(item.id)}
                onClick={() => void removeSupplier(item.id, item.name)}>
                {deletePending.has(item.id) ? 'Удаляем…' : 'Удалить'}
              </button>
            </div>
          </Card>)}
        </div>}

        {result.totalPages > 1 && <nav className="catalog-pagination" aria-label="Страницы базы поставщиков">
          <button className="button secondary" type="button" disabled={Number(read('page', '1')) <= 1} onClick={() => updateParam('page', '1', true)}>В начало</button>
          <button className="button secondary" type="button" disabled={Number(read('page', '1')) <= 1} onClick={() => updateParam('page', String(Math.max(1, Number(read('page', '1')) - 1)), true)}>Назад</button>
          <span>Страница {read('page', '1')} из {result.totalPages}</span>
          <button className="button secondary" type="button" disabled={Number(read('page', '1')) >= result.totalPages} onClick={() => updateParam('page', String(Number(read('page', '1')) + 1), true)}>Вперёд</button>
          <button className="button secondary" type="button" disabled={Number(read('page', '1')) >= result.totalPages} onClick={() => updateParam('page', String(result.totalPages), true)}>В конец</button>
        </nav>}
      </section>}
    </section>
  )
}

function supplierCountLabel(count: number): string {
  const lastTwo = count % 100
  const lastDigit = count % 10
  const noun = lastTwo >= 11 && lastTwo <= 14 ? 'поставщиков'
    : lastDigit === 1 ? 'поставщик'
      : lastDigit >= 2 && lastDigit <= 4 ? 'поставщика' : 'поставщиков'
  return `${count === 1 ? 'Найден' : 'Найдено'} ${count} ${noun}`
}
