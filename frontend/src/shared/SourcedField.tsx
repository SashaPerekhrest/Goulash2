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
      {field.sources.length > 0 && <ul className="details-source-list">
        {field.sources.map((source, index) => <li key={`${source.url}-${index}`}><Source source={source} /></li>)}
      </ul>}
      {field.alternatives.length > 0 && <details className="details-alternatives">
        <summary>Другие наблюдения: {field.alternatives.length}</summary>
        {field.alternatives.map((alternative, index) => <div key={index} className="details-alternative">
          <span>{show(alternative.value, true)}</span>
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
    <span>Страница сайта</span>
    <p>{source.excerpt}</p>
  </div>
}
