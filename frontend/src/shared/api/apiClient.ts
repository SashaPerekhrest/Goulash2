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

async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers)
  headers.set('Accept', 'application/json, application/problem+json')
  if (init.body !== undefined && !headers.has('Content-Type')) {
    headers.set('Content-Type', 'application/json')
  }

  const response = await fetch(`${apiRoot}${path}`, {
    ...init,
    headers,
    credentials: 'include',
  })

  if (!response.ok) {
    const problem = await readProblem(response)
    throw new ApiError(
      problem.title ?? 'Не удалось выполнить запрос',
      response.status,
      problem.code ?? 'HTTP_ERROR',
      problem.traceId,
      problem.detail,
    )
  }

  if (response.status === 204) return undefined as T
  return await response.json() as T
}

async function readProblem(response: Response): Promise<ProblemDetails> {
  try {
    return await response.json() as ProblemDetails
  } catch {
    return { status: response.status }
  }
}

export const apiClient = {
  get: <T>(path: string, signal?: AbortSignal) => request<T>(path, { method: 'GET', signal }),
  post: <T, TBody = unknown>(path: string, body?: TBody, signal?: AbortSignal) =>
    request<T>(path, { method: 'POST', body: body === undefined ? undefined : JSON.stringify(body), signal }),
  put: <T, TBody = unknown>(path: string, body: TBody, signal?: AbortSignal) =>
    request<T>(path, { method: 'PUT', body: JSON.stringify(body), signal }),
  delete: <T>(path: string, signal?: AbortSignal) => request<T>(path, { method: 'DELETE', signal }),
}

export type ReadinessResponse = { status: 'ready' }

export function getReadiness(signal?: AbortSignal) {
  return apiClient.get<ReadinessResponse>('/health/ready', signal)
}
