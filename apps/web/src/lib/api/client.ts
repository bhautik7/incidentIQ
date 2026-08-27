import { config } from '../../config'

/**
 * Typed HTTP client for the IncidentIQ API.
 *
 * One place that knows about the base URL, the API key and the error shape,
 * so no component ever calls fetch directly and no error handling is written
 * twice.
 */
export class ApiError extends Error {
  readonly status: number
  readonly correlationId?: string

  constructor(message: string, status: number, correlationId?: string) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.correlationId = correlationId
  }
}

export async function apiGet<T>(path: string, signal?: AbortSignal): Promise<T> {
  let response: Response

  try {
    response = await fetch(`${config.apiBaseUrl}${path}`, {
      signal,
      headers: { 'X-Api-Key': config.apiKey },
    })
  } catch (cause) {
    // fetch only rejects for network-level failures, and the browser's message
    // ("Failed to fetch") tells the user nothing about which service is down.
    if ((cause as Error)?.name === 'AbortError') throw cause
    throw new ApiError('The API could not be reached. It may be starting up or unavailable.', 0)
  }

  if (!response.ok) {
    // RFC 7807 problem details: "detail" is written to be read by a person,
    // so it beats a generic status message where one exists.
    let detail = `Request failed with status ${response.status}.`
    let correlationId: string | undefined

    try {
      const problem = await response.json()
      if (problem?.detail) detail = problem.detail
      if (problem?.correlationId) correlationId = problem.correlationId
    } catch {
      // Not JSON; the status stands on its own.
    }

    throw new ApiError(detail, response.status, correlationId)
  }

  return (await response.json()) as T
}

/** Drops undefined and empty values so the URL carries only real filters. */
export function toQuery(params: Record<string, string | number | undefined | null>): string {
  const search = new URLSearchParams()

  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== null && value !== '') {
      search.set(key, String(value))
    }
  }

  const query = search.toString()
  return query ? `?${query}` : ''
}

/**
 * POST, for the handful of actions the dashboard can take.
 *
 * Shares ApiError with apiGet so a component handles one error shape. The
 * status code carries the meaning the UI needs: 409 is "somebody got there
 * first", 403 is "this key cannot act as a person", and those deserve to be
 * said differently.
 */
export async function apiPost<T>(path: string, body?: unknown): Promise<T> {
  let response: Response

  try {
    response = await fetch(`${config.apiBaseUrl}${path}`, {
      method: 'POST',
      headers: {
        'X-Api-Key': config.apiKey,
        ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
      },
      body: body === undefined ? undefined : JSON.stringify(body),
    })
  } catch {
    throw new ApiError('The API could not be reached. It may be starting up or unavailable.', 0)
  }

  if (!response.ok) {
    let detail = `Request failed with status ${response.status}.`
    let correlationId: string | undefined

    try {
      const problem = await response.json()
      if (problem?.detail) detail = problem.detail
      if (problem?.correlationId) correlationId = problem.correlationId
    } catch {
      // Not JSON; the status stands on its own.
    }

    throw new ApiError(detail, response.status, correlationId)
  }

  if (response.status === 204) return undefined as T

  // 202 Accepted may legitimately carry no body.
  const text = await response.text()
  return (text ? JSON.parse(text) : undefined) as T
}
