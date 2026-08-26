/**
 * Client for the incident read API.
 *
 * Every call carries the API key, which is what scopes the response to one
 * organization. The key lives in runtime config rather than the bundle, so the
 * same built image can be pointed at a different tenant or environment.
 */

import { config } from '../config'

export interface IncidentListItem {
  id: string
  title: string
  status: 'Detected' | 'Investigating' | 'Resolved' | 'Ignored'
  severity: 'Low' | 'Medium' | 'High' | 'Critical'
  detectionRule: string
  service: string
  environment: string
  occurrenceCount: number
  firstSeenAt: string
  lastSeenAt: string
  suspectedDeploymentVersion: string | null
  hasAnalysis: boolean
  analysisConfidence: number | null
}

export interface IncidentPattern {
  fingerprint: string
  messageTemplate: string
  sampleMessage: string
  exceptionType: string | null
  httpStatusCode: number | null
  occurrenceCount: number
}

export interface IncidentDeployment {
  version: string
  deployedAt: string
  commitSha: string | null
  deployedBy: string | null
  minutesBeforeIncident: number
}

export interface SimilarIncident {
  incidentId: string
  title: string
  similarity: number
  resolutionNotes: string | null
}

export interface IncidentAnalysis {
  modelProvider: string
  modelName: string | null
  confidence: number | null
  summary: string | null
  probableCause: string | null
  suggestedActions: string[]
  similarIncidents: SimilarIncident[]
  createdAt: string
}

export interface IncidentTimelineEntry {
  type: string
  occurredAt: string
  actorType: string
  message: string | null
}

export interface IncidentSample {
  occurredAt: string
  level: string
  message: string
  host: string | null
  traceId: string | null
}

export interface IncidentDetail {
  incident: IncidentListItem
  pattern: IncidentPattern | null
  deployment: IncidentDeployment | null
  analysis: IncidentAnalysis | null
  timeline: IncidentTimelineEntry[]
  samples: IncidentSample[]
}

export interface PagedResult<T> {
  items: T[]
  page: number
  pageSize: number
  totalCount: number
  totalPages: number
}

export interface IncidentStats {
  detected: number
  investigating: number
  resolvedLast24Hours: number
  critical: number
  totalOccurrences: number
}

export interface ServiceSummary {
  key: string
  displayName: string
  activeIncidents: number
}

export interface IncidentFilters {
  status?: string
  severity?: string
  service?: string
  search?: string
}

export class ApiError extends Error {
  // Assigned in the body rather than as a constructor parameter property:
  // the project builds with erasableSyntaxOnly, which forbids syntax that
  // needs a TypeScript-specific emit.
  readonly status: number

  constructor(message: string, status: number) {
    super(message)
    this.status = status
  }
}

async function request<T>(path: string, signal?: AbortSignal): Promise<T> {
  const response = await fetch(`${config.apiBaseUrl}${path}`, {
    signal,
    headers: { 'X-Api-Key': config.apiKey },
  })

  if (!response.ok) {
    // The API returns RFC 7807 problem details, whose "detail" is written to be
    // read by a person. Surfacing it beats a generic "request failed".
    let detail = `Request failed with ${response.status}`
    try {
      const problem = await response.json()
      if (problem?.detail) detail = problem.detail
    } catch {
      // Body was not JSON; the status alone will have to do.
    }
    throw new ApiError(detail, response.status)
  }

  return response.json() as Promise<T>
}

export function fetchIncidents(
  filters: IncidentFilters,
  signal?: AbortSignal,
): Promise<PagedResult<IncidentListItem>> {
  const params = new URLSearchParams()
  if (filters.status) params.set('status', filters.status)
  if (filters.severity) params.set('severity', filters.severity)
  if (filters.service) params.set('service', filters.service)
  if (filters.search) params.set('search', filters.search)
  params.set('pageSize', '50')

  return request(`/api/v1/incidents?${params}`, signal)
}

export function fetchIncident(id: string, signal?: AbortSignal): Promise<IncidentDetail> {
  return request(`/api/v1/incidents/${id}`, signal)
}

export function fetchStats(signal?: AbortSignal): Promise<IncidentStats> {
  return request('/api/v1/stats', signal)
}

export function fetchServices(signal?: AbortSignal): Promise<ServiceSummary[]> {
  return request('/api/v1/services', signal)
}

/** "3m ago" reads faster than a timestamp when scanning a list. */
export function relativeTime(iso: string): string {
  const seconds = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000)

  if (seconds < 60) return `${Math.floor(seconds)}s ago`
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`
  return `${Math.floor(seconds / 86400)}d ago`
}

export function formatCount(value: number): string {
  if (value < 1000) return String(value)
  if (value < 1_000_000) return `${(value / 1000).toFixed(value < 10_000 ? 1 : 0)}k`
  return `${(value / 1_000_000).toFixed(1)}M`
}
