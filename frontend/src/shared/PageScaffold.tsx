type PageScaffoldProps = {
  eyebrow: string
  title: string
  description: string
}

export function PageScaffold({ eyebrow, title, description }: PageScaffoldProps) {
  return (
    <section className="page-content">
      <span className="eyebrow">{eyebrow}</span>
      <div className="page-heading">
        <div>
          <h1>{title}</h1>
          <p className="lead">{description}</p>
        </div>
      </div>
      <div className="foundation-card">
        <span className="foundation-icon" aria-hidden="true">✳</span>
        <div>
          <strong>Основа готова</strong>
          <p>Маршрутизация, единый API-клиент и подключение к базе подготовлены.</p>
        </div>
        <span className="foundation-tag">СПРИНТ 01</span>
      </div>
    </section>
  )
}
