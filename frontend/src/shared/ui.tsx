import type { ReactNode } from 'react'

export function Card({
  children,
  className = '',
}: {
  children: ReactNode
  className?: string
}) {
  return <section className={`ui-card ${className}`.trim()}>{children}</section>
}

export function FormField({
  id,
  label,
  hint,
  children,
}: {
  id: string
  label: string
  hint?: string
  children: ReactNode
}) {
  return (
    <div className="form-field">
      <label htmlFor={id}>{label}</label>
      {children}
      {hint && <span className="field-hint">{hint}</span>}
    </div>
  )
}

export function EmptyState({
  title,
  description,
  action,
}: {
  title: string
  description: string
  action?: ReactNode
}) {
  return (
    <div className="empty-state">
      <span className="empty-state-mark" aria-hidden="true">✳</span>
      <h2>{title}</h2>
      <p>{description}</p>
      {action}
    </div>
  )
}

export function PageLoading({ label = 'Загружаем страницу…' }: { label?: string }) {
  return (
    <div className="page-loading" role="status" aria-live="polite">
      <span className="loading-spinner" aria-hidden="true" />
      <span>{label}</span>
    </div>
  )
}

export function LoadingIndicator({ label = 'Загрузка…' }: { label?: string }) {
  return (
    <span className="inline-loading" role="status" aria-live="polite">
      <span className="loading-spinner small" aria-hidden="true" />
      {label}
    </span>
  )
}

export function ErrorNotice({
  title = 'Не удалось выполнить действие',
  children,
  action,
}: {
  title?: string
  children: ReactNode
  action?: ReactNode
}) {
  return (
    <div className="error-notice" role="alert">
      <span className="error-notice-mark" aria-hidden="true">!</span>
      <div className="error-notice-content">
        <strong>{title}</strong>
        <p>{children}</p>
        {action && <div className="notice-action">{action}</div>}
      </div>
    </div>
  )
}

export function SafeExternalLink({
  href,
  children,
  className,
}: {
  href: string
  children: ReactNode
  className?: string
}) {
  let safeHref: string | null = null
  try {
    const url = new URL(href)
    if (url.protocol === 'https:' || url.protocol === 'http:') safeHref = url.href
  } catch {
    safeHref = null
  }

  if (!safeHref) return <span className={className}>{children}</span>
  return <a className={className} href={safeHref} target="_blank" rel="noopener noreferrer">{children}</a>
}
