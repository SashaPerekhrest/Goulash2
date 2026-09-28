export type ProblemDetails = {
  type?: string
  title?: string
  status?: number
  detail?: string
  code?: string
  traceId?: string
}

export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
    readonly code: string,
    readonly traceId?: string,
    readonly detail?: string,
  ) {
    super(message)
    this.name = 'ApiError'
  }
}

const apiRoot = '/api/v1'
const unauthorizedEvent = 'goulash:unauthorized'
let csrfToken: string | null = null
let csrfRequest: Promise<string> | null = null

type RequestOptions = {
  signal?: AbortSignal
  redirectOnUnauthorized?: boolean
}

async function request<T>(
  path: string,
  init: RequestInit = {},
  options: RequestOptions = {},
): Promise<T> {
  const method = (init.method ?? 'GET').toUpperCase()
  const headers = new Headers(init.headers)
  headers.set('Accept', 'application/json, application/problem+json')
  if (init.body !== undefined && !headers.has('Content-Type')) {
    headers.set('Content-Type', 'application/json')
  }

  if (!isSafeMethod(method)) {
    headers.set('X-CSRF-Token', await getCsrfToken(options.signal))
  }

  const response = await fetch(`${apiRoot}${path}`, {
    ...init,
    method,
    headers,
    signal: options.signal ?? init.signal,
    credentials: 'include',
  })

  if (!response.ok) {
    const problem = await readProblem(response)
    if (problem.code === 'CSRF_INVALID' || response.status === 401) csrfToken = null
    if (response.status === 401 && options.redirectOnUnauthorized !== false) {
      window.dispatchEvent(new Event(unauthorizedEvent))
    }
    throw new ApiError(
      problem.title ?? 'Не удалось выполнить запрос',
      response.status,
      problem.code ?? 'HTTP_ERROR',
      problem.traceId,
      problem.detail,
    )
  }

  if (response.status === 204) return undefined as T
  try {
    return await response.json() as T
  } catch {
    throw new ApiError('Сервер вернул некорректный ответ', 502, 'INVALID_RESPONSE')
  }
}

async function getCsrfToken(signal?: AbortSignal): Promise<string> {
  if (csrfToken) return csrfToken
  if (csrfRequest) return csrfRequest

  csrfRequest = (async () => {
    const response = await fetch(`${apiRoot}/auth/csrf`, {
      method: 'GET',
      headers: { Accept: 'application/json, application/problem+json' },
      credentials: 'include',
      signal,
    })
    if (!response.ok) {
      const problem = await readProblem(response)
      throw new ApiError(
        problem.title ?? 'Не удалось подготовить защищённый запрос',
        response.status,
        problem.code ?? 'HTTP_ERROR',
        problem.traceId,
        problem.detail,
      )
    }

    const result = await response.json() as { csrfToken?: unknown }
    if (typeof result.csrfToken !== 'string' || result.csrfToken.length === 0) {
      throw new ApiError('Сервер не выдал токен защиты запроса', response.status, 'CSRF_TOKEN_MISSING')
    }
    csrfToken = result.csrfToken
    return csrfToken
  })()

  try {
    return await csrfRequest
  } finally {
    csrfRequest = null
  }
}

function isSafeMethod(method: string) {
  return ['GET', 'HEAD', 'OPTIONS', 'TRACE'].includes(method)
}

async function readProblem(response: Response): Promise<ProblemDetails> {
  try {
    return await response.json() as ProblemDetails
  } catch {
    return { status: response.status }
  }
}

export const apiClient = {
  get: <T>(path: string, signal?: AbortSignal) =>
    request<T>(path, { method: 'GET', signal }, { signal }),
  post: <T, TBody = unknown>(path: string, body?: TBody, options: RequestOptions = {}) =>
    request<T>(path, {
      method: 'POST',
      body: body === undefined ? undefined : JSON.stringify(body),
    }, options),
  put: <T, TBody = unknown>(path: string, body: TBody, options: RequestOptions = {}) =>
    request<T>(path, { method: 'PUT', body: JSON.stringify(body) }, options),
  delete: <T>(path: string, options: RequestOptions = {}) =>
    request<T>(path, { method: 'DELETE' }, options),
}

export type ReadinessResponse = { status: 'ready' }
export type AuthSessionResponse = { authenticated: true }
export type AiProvider = {
  id: string
  displayName: string
  supportsWebSearch: boolean
  models: string[]
  supportsFreeformModel: boolean
  supportsProviderRouting: boolean
  basePrompt: string
  defaultBasePrompt: string
  profilePrompt: string
  defaultProfilePrompt: string
}
export type AiProvidersResponse = { items: AiProvider[] }
export type AiSettings = {
  providerId: string | null
  model: string | null
  routeProvider: string | null
  basePrompt: string | null
  profilePrompt: string | null
  hasApiKey: boolean
  apiKeyMask: string | null
  updatedAt: string | null
}
export type AiSettingsUpdate = {
  providerId: string
  model: string
  routeProvider?: string
  basePrompt?: string
  profilePrompt?: string
  apiKey?: string
}
export type AiSettingsCheck = {
  connected: boolean
  webSearchAvailable: boolean
  checkedAt: string
}

export type DiscoverySearchRequest = {
  query: string
  filters: {
    city: string | null
    region: string | null
    category: string | null
    product: string | null
    price: { min: string | null; max: string | null; currency: string; unit: string } | null
    includeApproximatePrices: boolean
    maxDeliveryDays: number | null
    minMinimumOrder: { amount: string; unit: string } | null
    maxMinimumOrder: { amount: string; unit: string } | null
  }
}

export type DiscoverySearchCard = {
  id: string
  name: string
  city: string | null
  products: string[]
  pricePreview: string | null
  priceIsApproximate: boolean
  deliveryPreview: string | null
  websiteUrl: string | null
  contactPreview: string | null
  isFavorite: boolean
  hasUnconfirmedData: boolean
  lastDiscoveredAt: string | null
}

export type DiscoverySearchResponse = {
  discoveryId: string
  items: DiscoverySearchCard[]
  acceptedCount: number
  failedProfileCount: number
  updatedExistingCount: number
  outcome: 'complete' | 'partial' | 'no_candidates' | 'profiles_failed'
  sourcePageCount: number
}

export type DiscoveryJobAccepted = { discoveryId: string; status: 'queued' }
export type DiscoveryJobStatus = {
  discoveryId: string
  status: 'queued' | 'running' | 'succeeded' | 'failed' | 'cancelled'
  stage: string
  candidateCount: number
  completedCandidates: number
  acceptedCount: number
  failedProfileCount: number
  errorCode: string | null
  result: DiscoverySearchResponse | null
}

export type SupplierFavoriteResponse = { id: string; isFavorite: boolean }
export type SupplierNoteResponse = { id: string; note: string | null }
export type SupplierCatalogCard = {
  id: string
  name: string
  city: string | null
  products: string[]
  pricePreview: string | null
  priceIsApproximate: boolean
  deliveryPreview: string | null
  websiteUrl: string | null
  contactPreview: string | null
  isFavorite: boolean
  hasUnconfirmedData: boolean
  lastDiscoveredAt: string | null
}
export type SupplierCatalogPage = {
  items: SupplierCatalogCard[]
  page: number
  pageSize: number
  totalItems: number
  totalPages: number
}

export type SupplierFactSource = {
  url: string
  title: string | null
  excerpt: string
  type: 'official' | 'external'
  retrievedAt: string
}
export type SupplierFactAlternative<T> = {
  value: T
  status: 'official' | 'external' | 'aiGenerated'
  sources: SupplierFactSource[]
  observedAt: string
}
export type SupplierSourcedValue<T> = {
  value: T | null
  status: 'official' | 'external' | 'aiGenerated' | 'missing'
  sources: SupplierFactSource[]
  observedAt: string | null
  alternatives: SupplierFactAlternative<T>[]
}
export type SupplierDetails = {
  id: string
  name: SupplierSourcedValue<string>
  description: SupplierSourcedValue<string>
  address: SupplierSourcedValue<string>
  city: SupplierSourcedValue<string>
  region: SupplierSourcedValue<string>
  serviceRegions: SupplierSourcedValue<string>[]
  contacts: {
    phones: SupplierSourcedValue<string>[]
    emails: SupplierSourcedValue<string>[]
    website: SupplierSourcedValue<string>
  }
  products: Array<{
    name: SupplierSourcedValue<string>
    category: SupplierSourcedValue<string>
    prices: Array<{
      amountMin: number
      amountMax: number
      currency: string
      unit: string
      isApproximate: boolean
      evidence: SupplierSourcedValue<string>
    }>
  }>
  delivery: { terms: SupplierSourcedValue<string>; maxDays: SupplierSourcedValue<number> }
  minimumOrder: SupplierSourcedValue<{ amount: string | null; unit: string | null; details: string | null }>
  certificates: SupplierSourcedValue<string>[]
  images: SupplierSourcedValue<string>[]
  sources: SupplierFactSource[]
  isFavorite: boolean
  note: string | null
  createdAt: string
  updatedAt: string
  lastDiscoveredAt: string | null
}

export function getReadiness(signal?: AbortSignal) {
  return apiClient.get<ReadinessResponse>('/health/ready', signal)
}

export function getAuthSession(signal?: AbortSignal) {
  return request<AuthSessionResponse>(
    '/auth/session',
    { method: 'GET', signal },
    { signal, redirectOnUnauthorized: false },
  )
}

export async function login(password: string) {
  await apiClient.post<void, { password: string }>('/auth/login', { password }, {
    redirectOnUnauthorized: false,
  })
  // The token used for login belongs to the anonymous principal.
  csrfToken = null
}

export async function logout() {
  await apiClient.post<void>('/auth/logout')
  // A token issued for the signed-in principal cannot be reused after logout.
  csrfToken = null
}

export function getAiProviders(signal?: AbortSignal) {
  return apiClient.get<AiProvidersResponse>('/ai/providers', signal)
}

export function getAiSettings(signal?: AbortSignal) {
  return apiClient.get<AiSettings>('/ai/settings', signal)
}

export function updateAiSettings(settings: AiSettingsUpdate) {
  return apiClient.put<AiSettings>('/ai/settings', settings)
}

export function deleteAiApiKey() {
  return apiClient.delete<void>('/ai/settings/key')
}

export function checkAiSettings() {
  return apiClient.post<AiSettingsCheck>('/ai/settings/check')
}

export async function discoverSuppliers(request: DiscoverySearchRequest, signal?: AbortSignal) {
  const response = await apiClient.post<unknown, DiscoverySearchRequest>('/discoveries', request, { signal })
  if (!isRecord(response) || typeof response.discoveryId !== 'string' || !response.discoveryId ||
    response.status !== 'queued') {
    throw new ApiError('Сервер вернул некорректный ответ', 502, 'INVALID_RESPONSE')
  }
  return response as DiscoveryJobAccepted
}

export async function getDiscoveryStatus(id: string, signal?: AbortSignal) {
  const response = await apiClient.get<unknown>(`/discoveries/${encodeURIComponent(id)}`, signal)
  if (!isDiscoveryJobStatus(response, id)) throw new ApiError('Сервер вернул некорректный ответ', 502, 'INVALID_RESPONSE')
  return response as DiscoveryJobStatus
}

export async function getActiveDiscovery(signal?: AbortSignal) {
  const response = await apiClient.get<unknown>('/discoveries/active', signal)
  if (response === undefined || response === null) return null
  if (!isRecord(response) || typeof response.discoveryId !== 'string' ||
    !isDiscoveryJobStatus(response, response.discoveryId)) {
    throw new ApiError('Сервер вернул некорректный ответ', 502, 'INVALID_RESPONSE')
  }
  return response as DiscoveryJobStatus
}

export async function cancelDiscovery(id: string) {
  const response = await apiClient.post<unknown>(`/discoveries/${encodeURIComponent(id)}/cancel`)
  if (!isRecord(response) || response.discoveryId !== id || typeof response.status !== 'string') {
    throw new ApiError('Сервер вернул некорректный ответ', 502, 'INVALID_RESPONSE')
  }
  return response as { discoveryId: string; status: string }
}

export async function setSupplierFavorite(id: string, isFavorite: boolean) {
  const response = await apiClient.put<unknown, { isFavorite: boolean }>(
    `/suppliers/${encodeURIComponent(id)}/favorite`, { isFavorite },
  )
  if (!isRecord(response) || typeof response.id !== 'string' || typeof response.isFavorite !== 'boolean') {
    throw new ApiError('Сервер вернул некорректный ответ', 502, 'INVALID_RESPONSE')
  }
  return response as SupplierFavoriteResponse
}

export async function setSupplierNote(id: string, note: string | null) {
  const response = await apiClient.put<unknown, { note: string | null }>(
    `/suppliers/${encodeURIComponent(id)}/note`, { note },
  )
  if (!isRecord(response) || typeof response.id !== 'string' ||
    !(response.note === null || typeof response.note === 'string')) {
    throw new ApiError('Сервер вернул некорректный ответ', 502, 'INVALID_RESPONSE')
  }
  return response as SupplierNoteResponse
}

export function getSuppliers(query: URLSearchParams, signal?: AbortSignal) {
  const suffix = query.toString()
  return apiClient.get<SupplierCatalogPage>(`/suppliers${suffix ? `?${suffix}` : ''}`, signal)
}

export function getSupplierDetails(id: string, signal?: AbortSignal) {
  return apiClient.get<SupplierDetails>(`/suppliers/${encodeURIComponent(id)}`, signal)
}

export function deleteSupplier(id: string) {
  return apiClient.delete<void>(`/suppliers/${encodeURIComponent(id)}`)
}

function isDiscoveryJobStatus(value: unknown, expectedId: string): value is DiscoveryJobStatus {
  return isRecord(value) && value.discoveryId === expectedId &&
    ['queued', 'running', 'succeeded', 'failed', 'cancelled'].includes(value.status as string) &&
    typeof value.stage === 'string' && isNonNegativeInteger(value.candidateCount) &&
    isNonNegativeInteger(value.completedCandidates) && isNonNegativeInteger(value.acceptedCount) &&
    isNonNegativeInteger(value.failedProfileCount) &&
    (value.errorCode === null || typeof value.errorCode === 'string') &&
    (value.result === null || isDiscoverySearchResponse(value.result))
}

function isDiscoverySearchResponse(value: unknown): value is DiscoverySearchResponse {
  if (!isRecord(value) || typeof value.discoveryId !== 'string' || !value.discoveryId ||
    !Array.isArray(value.items) || value.items.length > 20 ||
    !isNonNegativeInteger(value.acceptedCount) || value.acceptedCount !== value.items.length ||
    !isNonNegativeInteger(value.failedProfileCount) || !isNonNegativeInteger(value.updatedExistingCount) ||
    value.updatedExistingCount > value.acceptedCount ||
    !['complete', 'partial', 'no_candidates', 'profiles_failed'].includes(value.outcome as string) ||
    !isNonNegativeInteger(value.sourcePageCount)) return false

  return value.items.every((item) => isRecord(item) &&
    typeof item.id === 'string' && item.id.length > 0 &&
    typeof item.name === 'string' && item.name.trim().length > 0 &&
    (item.city === null || typeof item.city === 'string') &&
    Array.isArray(item.products) && item.products.every((product) => typeof product === 'string') &&
    (item.pricePreview === null || typeof item.pricePreview === 'string') &&
    typeof item.priceIsApproximate === 'boolean' &&
    (item.deliveryPreview === null || typeof item.deliveryPreview === 'string') &&
    (item.websiteUrl === null || typeof item.websiteUrl === 'string') &&
    (item.contactPreview === null || typeof item.contactPreview === 'string') &&
    typeof item.isFavorite === 'boolean' && typeof item.hasUnconfirmedData === 'boolean' &&
    (item.lastDiscoveredAt === null || (typeof item.lastDiscoveredAt === 'string' &&
      Number.isFinite(Date.parse(item.lastDiscoveredAt))))
  )
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
}

function isNonNegativeInteger(value: unknown): value is number {
  return typeof value === 'number' && Number.isInteger(value) && value >= 0
}
