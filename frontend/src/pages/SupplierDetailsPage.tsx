import { useEffect, useState, type ReactNode } from 'react'
import { Link, useLocation, useParams } from 'react-router-dom'
import {
  ApiError,
  getSupplierDetails,
  setSupplierFavorite,
  setSupplierNote,
  type SupplierDetails,
  type SupplierSourcedValue,
} from '../shared/api/apiClient'
import { SourcedField, Source } from '../shared/SourcedField'
import { Card, ErrorNotice, PageLoading, SafeExternalLink } from '../shared/ui'

type SupplierDetailsLocationState = { from?: string }

export function SupplierDetailsPage() {
  const { id } = useParams()
  const location = useLocation()
  const backTo = (location.state as SupplierDetailsLocationState | null)?.from ?? '/suppliers'
  const [details, setDetails] = useState<SupplierDetails | null>(null)
  const [noteDraft, setNoteDraft] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [favoriteError, setFavoriteError] = useState<string | null>(null)
  const [noteError, setNoteError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [favoritePending, setFavoritePending] = useState(false)
  const [notePending, setNotePending] = useState(false)
  const [revision, setRevision] = useState(0)

  useEffect(() => {
    if (!id) return
    const controller = new AbortController()
    setLoading(true)
    setError(null)
    getSupplierDetails(id, controller.signal)
      .then((value) => {
        if (controller.signal.aborted) return
        setDetails(value)
        setNoteDraft(value.note ?? '')
      })
      .catch((cause: unknown) => {
        if (controller.signal.aborted) return
        setDetails(null)
        setError(cause instanceof ApiError && cause.status === 404
          ? 'Поставщик не найден.' : 'Не удалось загрузить сведения о поставщике.')
      })
      .finally(() => { if (!controller.signal.aborted) setLoading(false) })
    return () => controller.abort()
  }, [id, revision])

  async function toggleFavorite() {
    if (!id || !details) return
    setFavoritePending(true)
    setFavoriteError(null)
    try {
      const response = await setSupplierFavorite(id, !details.isFavorite)
      setDetails((current) => current ? { ...current, isFavorite: response.isFavorite } : current)
    } catch {
      setFavoriteError('Не удалось подтвердить изменение избранного. Обновите сведения и попробуйте ещё раз.')
    } finally {
      setFavoritePending(false)
    }
  }

  async function saveNote() {
    if (!id || !details) return
    setNotePending(true)
    setNoteError(null)
    try {
      const response = await setSupplierNote(id, noteDraft.length === 0 ? null : noteDraft)
      setDetails((current) => current ? { ...current, note: response.note } : current)
      setNoteDraft(response.note ?? '')
    } catch (cause) {
      setNoteError(cause instanceof ApiError && cause.status === 400
        ? cause.detail ?? 'Заметка не сохранена. Проверьте её длину.'
        : 'Не удалось сохранить заметку. Ваш текст оставлен в поле для повторной попытки.')
    } finally {
      setNotePending(false)
    }
  }

  if (loading) return <PageLoading label="Загружаем сведения о поставщике…" />

  return (
    <section className="page-content supplier-details-page">
      <Link className="details-back-link" to={backTo}>← Назад к списку</Link>
      {error && <ErrorNotice title={error} action={<button className="button secondary" type="button" onClick={() => setRevision((value) => value + 1)}>Повторить</button>}>
        Проверьте подключение и попробуйте ещё раз.
      </ErrorNotice>}
      {details && <>
        <div className="supplier-details-heading">
          <div>
            <span className="eyebrow">ПОСТАВЩИК</span>
            <h1>{details.name.value ?? 'Название не указано'}</h1>
          </div>
          <button className={details.isFavorite ? 'favorite-button selected details-favorite' : 'favorite-button details-favorite'}
            type="button" aria-pressed={details.isFavorite} disabled={favoritePending} onClick={() => void toggleFavorite()}>
            <span aria-hidden="true">{details.isFavorite ? '★' : '☆'}</span>
            <span>{favoritePending ? 'Сохраняем…' : details.isFavorite ? 'В избранном' : 'В избранное'}</span>
          </button>
        </div>
        {favoriteError && <ErrorNotice title={favoriteError}>Попробуйте ещё раз.</ErrorNotice>}

        <Card className="details-section">
          <h2>О компании</h2>
          <SourcedField label="Название" field={details.name} />
          <SourcedField label="Описание" field={details.description} />
          <SourcedField label="Адрес" field={details.address} />
          <SourcedField label="Город" field={details.city} />
          <SourcedField label="Регион" field={details.region} />
          <SourcedCollection title="Регионы работы" values={details.serviceRegions} className="service-regions-collection" />
        </Card>

        <Card className="details-section">
          <h2>Контакты</h2>
          <SourcedField label="Сайт" field={details.contacts.website}
            render={(value) => <SafeExternalLink className="safe-external-link" href={value}>{value}</SafeExternalLink>} />
          <SourcedCollection title="Телефоны" values={details.contacts.phones} className="details-contact-collection" />
          <SourcedCollection title="Электронная почта" values={details.contacts.emails} className="details-contact-collection" />
        </Card>

        <Card className="details-section">
          <h2>Товары и цены</h2>
          {details.products.length === 0 ? <p className="details-missing">Нет данных</p> : <div className="details-table-wrap">
            <table className="details-product-table">
              <thead><tr><th>Товар</th><th>Категория</th><th>Цена</th></tr></thead>
              <tbody>{details.products.flatMap((product, index) => product.prices.length === 0
                ? [<tr key={`${product.name.value ?? 'product'}-${index}`}>
                    <td><SourcedField field={product.name} /></td>
                    <td><SourcedField field={product.category} /></td>
                    <td><span className="details-missing">Нет данных</span></td>
                  </tr>]
                : product.prices.map((price, priceIndex) => <tr key={`${product.name.value ?? 'product'}-${index}-${priceIndex}`}>
                    <td><SourcedField field={product.name} /></td>
                    <td><SourcedField field={product.category} /></td>
                    <td><div className="details-field"><span className="details-field-value">
                      {price.amountMin === price.amountMax ? price.amountMin : `${price.amountMin}–${price.amountMax}`} {price.currency}/{price.unit}{price.isApproximate && ' · ориентировочно'}
                    </span>{price.evidence.sources.length > 0 && <ul className="details-source-list">
                      {price.evidence.sources.map((source, sourceIndex) => <li key={`${source.url}-${sourceIndex}`}><Source source={source} /></li>)}
                    </ul>}</div></td>
                  </tr>))}</tbody>
            </table>
          </div>}
        </Card>

        <Card className="details-section">
          <h2>Условия поставки</h2>
          <SourcedField label="Доставка" field={details.delivery.terms} />
          <SourcedField label="Максимальный срок" field={details.delivery.maxDays} render={(value) => `${value} дней`} />
          <SourcedField label="Минимальный заказ" field={details.minimumOrder} render={(value) =>
            [ [value.amount, value.unit].filter(Boolean).join(' '), value.details ].filter(Boolean).join(' · ')} />
          <SourcedCollection title="Сертификаты" values={details.certificates} />
          <SourcedCollection title="Изображения" values={details.images} render={(value) => <SupplierImage value={value} />} />
        </Card>

        <Card className="details-section supplier-note-card">
          <div className="note-heading"><div><h2>Общая заметка</h2><p>Заметку видят все пользователи этой базы.</p></div><span>{noteDraft.length}/2000</span></div>
          <div className="form-field"><label htmlFor="supplier-note">Заметка</label>
            <textarea id="supplier-note" className="text-input supplier-note-input" maxLength={2000} rows={5}
              disabled={notePending}
              value={noteDraft} onChange={(event) => { setNoteDraft(event.target.value); setNoteError(null) }}
              placeholder="Например, связаться по поводу условий поставки" />
          </div>
          {noteError && <ErrorNotice title={noteError}>Проверьте текст заметки и попробуйте ещё раз.</ErrorNotice>}
          <div className="note-actions">
            <button className="button primary" type="button" disabled={notePending || noteDraft === (details.note ?? '')} onClick={() => void saveNote()}>
              {notePending ? 'Сохраняем…' : 'Сохранить заметку'}
            </button>
            {details.note !== null && <button className="button quiet" type="button" disabled={notePending || noteDraft.length === 0} onClick={() => { setNoteDraft(''); setNoteError(null) }}>Очистить</button>}
          </div>
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

function SourcedCollection({ title, values, render, className = '' }: {
  title: string
  values: SupplierSourcedValue<string>[]
  render?: (value: string) => ReactNode
  className?: string
}) {
  return <div className={`details-collection ${className}`}>
    <h3>{title}</h3>
    {values.length === 0 ? <p className="details-missing">Нет данных</p> : values.map((value, index) =>
      <SourcedField key={index} field={value} render={render} />)}
  </div>
}

function SupplierImage({ value }: { value: string }) {
  return <span className="supplier-image-wrap">
    <img className="supplier-image" src={value} alt="Фотография поставщика" loading="lazy"
      onError={(event) => { event.currentTarget.hidden = true }} />
    <SafeExternalLink className="safe-external-link" href={value}>Открыть изображение</SafeExternalLink>
  </span>
}
