/** Mirrors the C# contracts in services/api/Contracts. */

export type Severity = 'Critical' | 'High' | 'Medium' | 'Low'
export type IncidentStatus = 'Detected' | 'Investigating' | 'Resolved' | 'Ignored'
export type ServiceHealthStatus = 'Healthy' | 'Degraded' | 'Critical' | 'Unknown'

export interface MetricSummary {
  value: number
  previousValue: number
  /** Null when the previous window was zero - a change from nothing is not a percentage. */
  changePercent: number | null
  series: number[]
}

export interface TimelinePoint {
  bucketStart: string
  errorEvents: number
  warningEvents: number
}

export interface TimelineMarker {
  kind: 'deployment' | 'incident'
  at: string
  label: string
  service: string
  incidentId: string | null
  severity: Severity | null
}

export interface OverviewResponse {
  windowStart: string
  windowEnd: string
  bucketMinutes: number
  activeIncidents: MetricSummary
  errorEvents: MetricSummary
  servicesAffected: MetricSummary
  meanTimeToResolutionMinutes: MetricSummary
  aiInvestigations: MetricSummary
  totalServices: number
  timeline: TimelinePoint[]
  markers: TimelineMarker[]
}

export interface ServiceHealth {
  key: string
  displayName: string
  ownerTeam: string | null
  health: ServiceHealthStatus
  activeIncidents: number
  errorEvents: number
  distinctErrorPatterns: number
  lastIncidentAt: string | null
  errorSeries: number[]
}

/** The service picker's options, from GET /api/v1/services. */
export interface ServiceSummary {
  key: string
  displayName: string
  activeIncidents: number
}

export interface IncidentListItem {
  id: string
  title: string
  status: IncidentStatus
  severity: Severity
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

export interface PagedResult<T> {
  items: T[]
  page: number
  pageSize: number
  totalCount: number
  totalPages: number
}
