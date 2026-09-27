import type { ReactNode } from 'react'
import { SafeExternalLink } from './ui'
import type { SupplierFactSource, SupplierSourcedValue } from './api/apiClient'

export function SourcedField<T>({ label, field, render }: {
  label?: string
  field: SupplierSourcedValue<T>
  render?: (value: T, isAlternative: boolean) => ReactNode
}) {
  const show = render ?? ((value: T) => String(value))
  return <div className="details-field">
    {label && <strong className="details-field-label">{label}</strong>}
    {field.status === 'missing' || field.value === null ? <span className="details-missing">Нет данных</span> : <>
      <span className="details-field-value">{show(field.value, false)}</span>
      <span className={field.status === 'external' ? 'details-status external' : 'details-status official'}>
        {field.status === 'external' ? 'Не подтверждённая информация' : 'Подтверждено официальным источником'}
      </span>
      {field.observedAt && <span className="details-date">Получено: {formatDate(field.observedAt)}</span>}
      {field.sources.length > 0 && <ul className="details-source-list">
        {field.sources.map((source, index) => <li key={`${source.url}-${index}`}><Source source={source} /></li>)}
      </ul>}
      {field.alternatives.length > 0 && <details className="details-alternatives">
        <summary>Другие наблюдения: {field.alternatives.length}</summary>
        {field.alternatives.map((alternative, index) => <div key={index} className="details-alternative">
          <span>{show(alternative.value, true)}</span>
          <span className={alternative.status === 'external' ? 'details-status external' : 'details-status official'}>
            {alternative.status === 'external' ? 'Не подтверждённая информация' : 'Подтверждено официальным источником'}
          </span>
          <span className="details-date">Получено: {formatDate(alternative.observedAt)}</span>
          {alternative.sources.length > 0 && <ul className="details-source-list">
            {alternative.sources.map((source, sourceIndex) => <li key={`${source.url}-${sourceIndex}`}><Source source={source} /></li>)}
          </ul>}
        </div>)}
      </details>}
    </>}
  </div>
}

export function Source({ source }: { source: SupplierFactSource }) {
  return <div className="details-source">
    <SafeExternalLink className="safe-external-link" href={source.url}>{source.title || source.url}</SafeExternalLink>
    <span>{source.type === 'official' ? 'Официальный' : 'Сторонний'} · {formatDate(source.retrievedAt)}</span>
    <p>{source.excerpt}</p>
  </div>
}

export function formatDate(value: string | null): string {
  if (!value) return 'Нет данных'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? 'Нет данных' : new Intl.DateTimeFormat('ru-RU', { dateStyle: 'medium' }).format(date)
}
