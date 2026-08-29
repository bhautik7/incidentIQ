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

// ---------------------------------------------------------------------------
// Incident detail
// ---------------------------------------------------------------------------

export type TimelineEntryType =
  | 'Created'
  | 'Escalated'
  | 'SeverityChanged'
  | 'InvestigationStarted'
  | 'Assigned'
  | 'Commented'
  | 'AiAnalysisCompleted'
  | 'Resolved'
  | 'Reopened'
  | 'Ignored'

export interface IncidentTimelineEntry {
  type: TimelineEntryType
  occurredAt: string
  actorType: 'System' | 'User' | 'Ai'
  /** Null for the detector's own entries - "the system noticed" is not a person. */
  actorName: string | null
  message: string
}

export interface IncidentPattern {
  fingerprint: string
  messageTemplate: string
  /** Raw, unlike the template. Never sent to the model. */
  sampleMessage: string | null
  exceptionType: string | null
  httpStatusCode: number | null
  occurrenceCount: number
}

export interface IncidentDeployment {
  version: string
  deployedAt: string
  commitSha: string | null
  deployedBy: string | null
  /**
   * Minutes between the deployment and the incident's first occurrence.
   *
   * Signed, and the sign matters: negative means the deployment landed *after*
   * the incident began, which is evidence against it being the cause.
   */
  minutesBeforeIncident: number
}

export interface SimilarIncident {
  /** A string, not a Guid: the worker writes this jsonb and is not bound to our id type. */
  incidentId: string
  title: string
  similarity: number
  /** What fixed it last time - the whole reason a past incident is worth surfacing. */
  resolutionNotes: string | null
}

export interface IncidentAnalysis {
  modelProvider: string
  modelName: string | null
  confidence: number
  summary: string | null
  probableCause: string | null
  suggestedActions: string[]
  similarIncidents: SimilarIncident[]
  createdAt: string
}

export interface IncidentSample {
  occurredAt: string
  level: string
  message: string
  host: string | null
  traceId: string | null
}

export interface IncidentOwner {
  userId: string
  displayName: string
  email: string
}

export type IncidentAction =
  | 'acknowledge'
  | 'assign'
  | 'resolve'
  | 'ignore'
  | 'reopen'
  | 'notes'
  | 'analyze'

export interface IncidentDetail {
  incident: IncidentListItem
  pattern: IncidentPattern | null
  deployment: IncidentDeployment | null
  analysis: IncidentAnalysis | null
  timeline: IncidentTimelineEntry[]
  samples: IncidentSample[]
  owner: IncidentOwner | null
  /** What the server will currently accept. An affordance, not the check. */
  availableActions: IncidentAction[]
}

export interface OrganizationMember {
  userId: string
  displayName: string
  email: string
}

export type LogLevel = 'Trace' | 'Debug' | 'Information' | 'Warning' | 'Error' | 'Fatal'

/**
 * One page of a stream.
 *
 * No total count and no page number, deliberately: counting a log table means
 * scanning it, and the answer changes between the count and the fetch because
 * lines keep arriving. A cursor states the only thing that stays true - what
 * comes next.
 */
export interface CursorPage<T> {
  items: T[]
  /** Opaque. Null once the end of the retention window is reached. */
  nextCursor: string | null
}

export interface LogEntry {
  id: number
  occurredAt: string
  receivedAt: string
  level: LogLevel
  service: string
  environment: string
  message: string
  exceptionType: string | null
  stackTrace: string | null
  traceId: string | null
  spanId: string | null
  host: string | null
  /** Raw jsonb text, rendered in the row's JSON view. */
  properties: string | null
  fingerprint: string | null
  /** An incident currently open for this line's pattern, if any. */
  incidentId: string | null
}

/**
 * How far back the explorer can actually see.
 *
 * Rendered beside the results so an empty table reads as "nothing is retained
 * that far back" rather than "nothing happened".
 */
export interface LogWindow {
  retentionHours: number
  oldestAvailableAt: string | null
}

export interface LogSearchResult {
  page: CursorPage<LogEntry>
  window: LogWindow
}

/**
 * The result of asking for an uploaded log to be diagnosed.
 *
 * "pending" is a normal answer, not an error: ingestion returns as soon as the
 * batch is on Kafka, so there is a window in which the upload succeeded and the
 * patterns behind it do not exist yet. The client polls until it is something
 * else.
 */
export interface DiagnoseResult {
  status: 'pending' | 'opened' | 'existing'
  incidentId: string | null
  /** The dominant pattern's fingerprint, for linking to its raw lines. */
  fingerprint: string | null
  title: string | null
  /** Occurrences inside the upload's window, not the pattern's lifetime total. */
  occurrenceCount: number
  patternsFound: number
  /** Written to be shown to the person waiting. */
  message: string
}

/**
 * Who the API key resolves to.
 *
 * There is no login, so this is not a session in the usual sense - it is the
 * only identity the system has. `actor` is the person incident actions are
 * recorded against, and is null when the key is bound to no user, which is
 * exactly when those actions return 403.
 */
export interface CurrentSession {
  organization: { id: string; name: string; slug: string } | null
  actor: { userId: string; displayName: string; email: string } | null
  apiKeyName: string
}
