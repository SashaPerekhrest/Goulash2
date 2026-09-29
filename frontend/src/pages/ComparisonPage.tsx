import { useEffect, useState, type ReactNode } from 'react'
import { Link } from 'react-router-dom'
import { getSupplierDetails, type SupplierDetails, type SupplierSourcedValue } from '../shared/api/apiClient'
import { useComparison } from '../shared/comparison/ComparisonContext'
import { Card, EmptyState, PageLoading, SafeExternalLink } from '../shared/ui'

type SupplierLoad =
  | { status: 'loading' }
  | { status: 'loaded'; details: SupplierDetails }
  | { status: 'failed' }

export function ComparisonPage() {
  const { supplierIds, notes, removeSupplier, setLocalNote } = useComparison()
  const [suppliers, setSuppliers] = useState<Record<string, SupplierLoad>>({})

  useEffect(() => {
    if (supplierIds.length === 0) {
      setSuppliers({})
      return
    }

    const controller = new AbortController()
    setSuppliers(Object.fromEntries(supplierIds.map((id) => [id, { status: 'loading' as const }])))
    for (const id of supplierIds) {
      getSupplierDetails(id, controller.signal)
        .then((details) => {
          if (!controller.signal.aborted) setSuppliers((current) => ({ ...current, [id]: { status: 'loaded', details } }))
        })
        .catch(() => {
          if (!controller.signal.aborted) setSuppliers((current) => ({ ...current, [id]: { status: 'failed' } }))
        })
    }
    return () => controller.abort()
  }, [supplierIds])

  if (supplierIds.length === 0) {
    return <section className="page-content comparison-page">
      <span className="eyebrow">СРАВНЕНИЕ</span>
      <h1>Сравнение поставщиков</h1>
      <Card className="comparison-empty-card">
        <EmptyState
          title="Пока нечего сравнивать"
          description="Добавьте поставщиков кнопкой «Сравнить» в базе поставщиков, чтобы сопоставить их условия."
          action={<Link className="button primary" to="/suppliers">Перейти в базу поставщиков</Link>}
        />
      </Card>
    </section>
  }

  const loadedCount = Object.values(suppliers).filter((entry) => entry.status === 'loaded').length
  if (loadedCount === 0 && supplierIds.some((id) => suppliers[id]?.status === 'loading')) {
    return <section className="page-content comparison-page">
      <span className="eyebrow">СРАВНЕНИЕ</span>
      <h1>Сравнение поставщиков</h1>
      <PageLoading label="Загружаем профили поставщиков…" />
    </section>
  }

  return <section className="page-content comparison-page">
    <div className="comparison-heading">
      <div><span className="eyebrow">СРАВНЕНИЕ</span><h1>Сравнение поставщиков</h1></div>
      <span className="comparison-count">{supplierIds.length} в сравнении</span>
    </div>
    <div className="comparison-scroll" role="table" aria-label="Сравнение поставщиков">
      <div className="comparison-grid" style={{
        gridTemplateColumns: `minmax(140px, .65fr) repeat(${supplierIds.length}, minmax(230px, 1fr))`,
        minWidth: `${140 + supplierIds.length * 230}px`,
      }}>
        <div className="comparison-row" role="row">
          <div className="comparison-label comparison-header-label" role="columnheader">Параметр</div>
          {supplierIds.map((id) => {
            const entry = suppliers[id]
            const name = entry?.status === 'loaded' ? entry.details.name.value : null
            return <div className="comparison-vendor-header" role="columnheader" key={id}>
              <div className="comparison-vendor-name">{name ?? (entry?.status === 'failed' ? 'Поставщик недоступен' : 'Загружаем…')}</div>
              <button className="comparison-remove" type="button" aria-label={`Убрать ${name ?? 'поставщика'} из сравнения`}
                title="Убрать из сравнения" onClick={() => removeSupplier(id)}>×</button>
            </div>
          })}
        </div>

        <ComparisonRow label="Название">
          {supplierIds.map((id) => <SupplierCell key={id} entry={suppliers[id]}>{(details) => details.name.value || 'Нет данных'}</SupplierCell>)}
        </ComparisonRow>
        <ComparisonRow label="Сайт и контакты">
          {supplierIds.map((id) => <SupplierCell key={id} entry={suppliers[id]}>{(details) => <div className="comparison-contact-list">
            <FieldLine label="Сайт">{details.contacts.website.value
              ? <SafeExternalLink className="safe-external-link" href={details.contacts.website.value}>{details.contacts.website.value}</SafeExternalLink>
              : 'Нет данных'}</FieldLine>
            <FieldLine label="Телефон">{joinFields(details.contacts.phones) || 'Нет данных'}</FieldLine>
            <FieldLine label="Почта">{joinFields(details.contacts.emails) || 'Нет данных'}</FieldLine>
          </div>}</SupplierCell>)}
        </ComparisonRow>
        <ComparisonRow label="Адреса">
          {supplierIds.map((id) => <SupplierCell key={id} entry={suppliers[id]}>{(details) => <div className="comparison-compact-list">
            <FieldLine label="Адрес">{fieldText(details.address) || 'Нет данных'}</FieldLine>
            <FieldLine label="Город">{fieldText(details.city) || 'Нет данных'}</FieldLine>
            <FieldLine label="Регион">{fieldText(details.region) || 'Нет данных'}</FieldLine>
            <FieldLine label="Работает в">{joinFields(details.serviceRegions) || 'Нет данных'}</FieldLine>
          </div>}</SupplierCell>)}
        </ComparisonRow>
        <ComparisonRow label="Товары">
          {supplierIds.map((id) => <SupplierCell key={id} entry={suppliers[id]}>{(details) => details.products.length === 0
            ? 'Нет данных'
            : <ul className="comparison-list">{details.products.map((product, index) => {
              const name = fieldText(product.name)
              const category = fieldText(product.category)
              return <li key={`${name}-${index}`}>{[name, category].filter(Boolean).join(' · ') || 'Нет данных'}</li>
            })}</ul>}</SupplierCell>)}
        </ComparisonRow>
        <ComparisonRow label="Условия поставки">
          {supplierIds.map((id) => <SupplierCell key={id} entry={suppliers[id]}>{(details) => <div className="comparison-compact-list">
            <FieldLine label="Доставка">{fieldText(details.delivery.terms) || 'Нет данных'}</FieldLine>
            <FieldLine label="Срок">{details.delivery.maxDays.value === null ? 'Нет данных' : `${details.delivery.maxDays.value} дней`}</FieldLine>
            <FieldLine label="Минимальный заказ">{minimumOrderText(details) || 'Нет данных'}</FieldLine>
          </div>}</SupplierCell>)}
        </ComparisonRow>
        <ComparisonRow label="Сертификаты">
          {supplierIds.map((id) => <SupplierCell key={id} entry={suppliers[id]}>{(details) => details.certificates.length === 0
            ? 'Нет данных'
            : <ul className="comparison-list">{details.certificates.map((certificate, index) =>
              <li key={`${fieldText(certificate)}-${index}`}>{fieldText(certificate) || 'Нет данных'}</li>)}</ul>}</SupplierCell>)}
        </ComparisonRow>
        <ComparisonRow label="Заметка">
          {supplierIds.map((id) => <SupplierCell key={id} entry={suppliers[id]}>{(details) => {
            const note = Object.hasOwn(notes, id) ? notes[id] : details.note ?? ''
            return <div className="comparison-note-field">
              <textarea aria-label={`Локальная заметка: ${details.name.value ?? 'поставщик'}`} maxLength={2000} rows={4}
                value={note} onChange={(event) => setLocalNote(id, event.target.value)}
                placeholder="Добавить заметку для сравнения" />
              <span>{note.length}/2000 · хранится в этом браузере</span>
            </div>
          }}</SupplierCell>)}
        </ComparisonRow>
      </div>
    </div>
    {supplierIds.some((id) => suppliers[id]?.status === 'failed') &&
      <p className="comparison-unavailable-hint">Не удалось загрузить часть профилей. Удалите недоступную запись крестиком или повторите загрузку страницы.</p>}
  </section>
}

function ComparisonRow({ label, children }: { label: string; children: ReactNode[] }) {
  return <div className="comparison-row" role="row">
    <div className="comparison-label" role="rowheader">{label}</div>
    {children}
  </div>
}

function SupplierCell({ entry, children }: {
  entry: SupplierLoad | undefined
  children: (details: SupplierDetails) => ReactNode
}) {
  if (entry?.status === 'loading' || !entry) return <div className="comparison-cell" role="cell">Загружаем…</div>
  if (entry.status === 'failed') return <div className="comparison-cell comparison-cell-error" role="cell">Профиль не загрузился</div>
  return <div className="comparison-cell" role="cell">{children(entry.details)}</div>
}

function FieldLine({ label, children }: { label: string; children: ReactNode }) {
  return <div className="comparison-field-line"><span>{label}</span><div>{children}</div></div>
}

function fieldText(field: SupplierSourcedValue<string>) {
  return field.status === 'missing' ? '' : field.value?.trim() ?? ''
}

function joinFields(fields: SupplierSourcedValue<string>[]) {
  return fields.map(fieldText).filter(Boolean).join(', ')
}

function minimumOrderText(details: SupplierDetails) {
  const order = details.minimumOrder.value
  if (!order) return ''
  return [[order.amount, order.unit].filter(Boolean).join(' '), order.details].filter(Boolean).join(' · ')
}
