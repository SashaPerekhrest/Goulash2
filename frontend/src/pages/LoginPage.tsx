import { useState, type FormEvent } from 'react'
import { useLocation, useNavigate } from 'react-router-dom'
import { ApiError, login } from '../shared/api/apiClient'
import { getSafeReturnPath, useAuth } from '../shared/auth'
import { Card, ErrorNotice, FormField } from '../shared/ui'

export function LoginPage() {
  const auth = useAuth()
  const location = useLocation()
  const navigate = useNavigate()
  const [password, setPassword] = useState('')
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (password.length === 0) {
      setError('Введите пароль администратора.')
      return
    }

    setError(null)
    setSubmitting(true)
    try {
      await login(password)
      setPassword('')
      auth.markAuthenticated()
      navigate(getSafeReturnPath(location.state), { replace: true })
    } catch (cause) {
      if (cause instanceof ApiError && cause.status === 401) {
        setError('Неверный пароль. Проверьте его и попробуйте снова.')
      } else if (cause instanceof ApiError && cause.status === 429) {
        setError('Слишком много попыток входа. Подождите и попробуйте позже.')
      } else if (cause instanceof ApiError && cause.code === 'CSRF_INVALID') {
        setError('Не удалось подтвердить запрос. Повторите попытку.')
      } else {
        setError('Не удалось выполнить вход. Проверьте соединение и попробуйте ещё раз.')
      }
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <section className="login-layout">
      <div className="login-aside">
        <span className="brand-mark login-brand-mark">Г</span>
        <p className="eyebrow">FOOD SUPPLY DESK</p>
        <p className="login-aside-copy">Источники, условия и контакты поставщиков — в одном рабочем пространстве.</p>
      </div>
      <Card className="login-card">
        <span className="eyebrow">ЗАЩИЩЁННЫЙ ДОСТУП</span>
        <h1>Вход администратора</h1>
        <p className="lead">Введите пароль, чтобы открыть рабочее пространство.</p>
        <form className="form-stack login-form" onSubmit={handleSubmit}>
          <FormField id="admin-password" label="Пароль">
            <input
              id="admin-password"
              className="text-input"
              type="password"
              autoComplete="current-password"
              value={password}
              onChange={(event) => setPassword(event.target.value)}
              autoFocus
              required
              disabled={submitting}
            />
          </FormField>
          {error && <ErrorNotice title="Войти не получилось">{error}</ErrorNotice>}
          <button className="button primary full-width" type="submit" disabled={submitting || password.length === 0}>
            {submitting ? 'Проверяем…' : 'Войти'}
          </button>
        </form>
      </Card>
    </section>
  )
}
