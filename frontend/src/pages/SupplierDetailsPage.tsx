import { useEffect, useState, type ReactNode } from 'react'
import { Link, useParams } from 'react-router-dom'
import { ApiError, getSupplierDetails, type SupplierDetails, type SupplierFactSource, type SupplierSourcedValue } from '../shared/api/apiClient'
import { Card, ErrorNotice, PageLoading, SafeExternalLink } from '../shared/ui'

export function SupplierDetailsPage() {
  const { id } = useParams()
  const [details, setDetails] = useState<SupplierDetails | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [revision, setRevision] = useState(0)

  useEffect(() => {
    if (!id) return
    const controller = new AbortController()
    setLoading(true)
    setError(null)
    getSupplierDetails(id, controller.signal)
      .then(setDetails)
      .catch((cause: unknown) => {
        if (controller.signal.aborted) return
        setDetails(null)
        setError(cause instanceof ApiError && cause.status === 404
          ? 'Поставщик не найден.' : 'Не удалось загрузить сведения о поставщике.')
      })
      .finally(() => { if (!controller.signal.aborted) setLoading(false) })
    return () => controller.abort()
  }, [id, revision])

  if (loading) return <PageLoading label="Загружаем сведения о поставщике…" />

  return (
    <section className="page-content supplier-details-page">
      <Link className="details-back-link" to="/discover">← К поиску поставщиков</Link>
      {error && <ErrorNotice title={error} action={<button className="button secondary" type="button" onClick={() => setRevision((value) => value + 1)}>Повторить</button>}>
        Проверьте подключение и попробуйте ещё раз.
      </ErrorNotice>}
      {details && <>
        <span className="eyebrow">ПОСТАВЩИК</span>
        <h1>{details.name.value ?? 'Название не указано'}</h1>
        <p className="details-date">Собрано: {date(details.lastDiscoveredAt)} · Обновлено: {date(details.updatedAt)}</p>

        <Card className="details-section">
          <h2>О компании</h2>
          <SourcedField label="Название" field={details.name} />
          <SourcedField label="Описание" field={details.description} />
          <SourcedField label="Адрес" field={details.address} />
          <SourcedField label="Город" field={details.city} />
          <SourcedField label="Регион" field={details.region} />
          <SourcedCollection title="Регионы работы" values={details.serviceRegions} />
        </Card>

        <Card className="details-section">
          <h2>Контакты</h2>
          <SourcedField label="Сайт" field={details.contacts.website} render={(value) => <SafeExternalLink className="safe-external-link" href={value}>{value}</SafeExternalLink>} />
          <SourcedCollection title="Телефоны" values={details.contacts.phones} />
          <SourcedCollection title="Электронная почта" values={details.contacts.emails} />
        </Card>

        <Card className="details-section">
          <h2>Товары и цены</h2>
          {details.products.length === 0 ? <p className="details-missing">Нет данных</p> : details.products.map((product, index) => (
            <div className="details-product" key={`${product.name.value ?? 'product'}-${index}`}>
              <SourcedField label="Товар" field={product.name} />
              <SourcedField label="Категория" field={product.category} />
              {product.prices.length === 0 ? <p className="details-missing">Цена: нет данных</p> : product.prices.map((price, priceIndex) => (
                <SourcedField key={priceIndex} label="Цена" field={price.evidence}
                  render={() => <>{price.amountMin === price.amountMax ? price.amountMin : `${price.amountMin}–${price.amountMax}`} {price.currency}/{price.unit}{price.isApproximate && ' · ориентировочно'}</>} />
              ))}
            </div>
          ))}
        </Card>

        <Card className="details-section">
          <h2>Условия поставки</h2>
          <SourcedField label="Доставка" field={details.delivery.terms} />
          <SourcedField label="Максимальный срок" field={details.delivery.maxDays} render={(value) => `${value} дней`} />
          <SourcedField label="Минимальный заказ" field={details.minimumOrder} render={(value) => `${value.amount} ${value.unit}`} />
          <SourcedCollection title="Сертификаты" values={details.certificates} />
          <SourcedCollection title="Изображения" values={details.images} render={(value) => <SafeExternalLink className="safe-external-link" href={value}>Открыть изображение</SafeExternalLink>} />
        </Card>

        <Card className="details-section">
          <h2>Все источники</h2>
          {details.sources.length === 0 ? <p className="details-missing">Нет данных</p> : <ul className="details-source-list">
            {details.sources.map((source, index) => <li key={`${source.url}-${index}`}><Source source={source} /></li>)}
          </ul>}
        </Card>
      </>}
    </section>
  )
}

function SourcedCollection({ title, values, render }: {
  title: string
  values: SupplierSourcedValue<string>[]
  render?: (value: string) => ReactNode
}) {
  return <div className="details-collection">
    <h3>{title}</h3>
    {values.length === 0 ? <p className="details-missing">Нет данных</p> : values.map((value, index) =>
      <SourcedField key={index} field={value} render={render} />)}
  </div>
}

function SourcedField<T>({ label, field, render }: {
  label?: string
  field: SupplierSourcedValue<T>
  render?: (value: T) => ReactNode
}) {
  const show = render ?? ((value: T) => String(value))
  return <div className="details-field">
    {label && <strong className="details-field-label">{label}</strong>}
    {field.status === 'missing' || field.value === null ? <span className="details-missing">Нет данных</span> : <>
      <span className="details-field-value">{show(field.value)}</span>
      <span className={field.status === 'external' ? 'details-status external' : 'details-status official'}>
        {field.status === 'external' ? 'Не подтверждённая информация' : 'Подтверждено официальным источником'}
      </span>
      {field.observedAt && <span className="details-date">Получено: {date(field.observedAt)}</span>}
      <ul className="details-source-list">
        {field.sources.map((source, index) => <li key={`${source.url}-${index}`}><Source source={source} /></li>)}
      </ul>
      {field.alternatives.length > 0 && <details className="details-alternatives">
        <summary>Другие наблюдения: {field.alternatives.length}</summary>
        {field.alternatives.map((alternative, index) => <div key={index} className="details-alternative">
          <span>{show(alternative.value)}</span>
          <span className={alternative.status === 'external' ? 'details-status external' : 'details-status official'}>
            {alternative.status === 'external' ? 'Не подтверждённая информация' : 'Подтверждено официальным источником'}
          </span>
          <span className="details-date">Получено: {date(alternative.observedAt)}</span>
          <ul className="details-source-list">{alternative.sources.map((source, sourceIndex) => <li key={`${source.url}-${sourceIndex}`}><Source source={source} /></li>)}</ul>
        </div>)}
      </details>}
    </>}
  </div>
}

function Source({ source }: { source: SupplierFactSource }) {
  return <div className="details-source">
    <SafeExternalLink className="safe-external-link" href={source.url}>{source.title || source.url}</SafeExternalLink>
    <span>{source.type === 'official' ? 'Официальный' : 'Сторонний'} · {date(source.retrievedAt)}</span>
    <p>{source.excerpt}</p>
  </div>
}

function date(value: string | null): string {
  return value ? new Intl.DateTimeFormat('ru-RU', { dateStyle: 'medium' }).format(new Date(value)) : 'Нет данных'
}
