import { config } from '../../config'
import { ApiError } from '../../lib/api/client'
import { normalizeMessage, normalizeStackFrames } from './normalize'
import { isError, type ParsedEvent, type Severity } from './parseLog'

/** The endpoint caps a batch at 500 events. */
const BATCH_SIZE = 500

const MAX_MESSAGE = 8000
const MAX_EXCEPTION_TYPE = 500
const MAX_STACK_TRACE = 32000

/** One group of near-identical lines: what the upload actually contains. */
export interface PatternGroup {
  template: string
  count: number
  severity: Severity
  exceptionType: string | null
  /** A real line from the group, un-masked, so the masking can be checked. */
  sample: string
}

export interface UploadSummary {
  lineCount: number
  eventCount: number
  errorCount: number
  stackTraceCount: number
  oldest: Date | null
  newest: Date | null
  /**
   * Milliseconds every timestamp is moved forward so the last line lands just
   * before now.
   *
   * Not cosmetic. Ingestion rejects events older than seven days outright, and
   * the log explorer only retains 48 hours - so an unshifted log from last
   * month is either refused at the door or accepted into a window nothing can
   * read it back from. The shift preserves the intervals between lines, which
   * is the part that carries meaning, and the UI states it when it is large
   * enough to matter.
   */
  shiftMs: number
  /**
   * Where the upload will land on the clock: the oldest line, once shifted.
   *
   * The diagnosis is scoped by this rather than by "when the upload started",
   * and the difference is not cosmetic. A shifted log occupies its own duration
   * *backwards* from now - an hour-long log starts an hour ago - so a window
   * beginning at the moment of upload contains almost none of it, and the
   * pattern that actually broke is usually in the part it excludes.
   */
  windowStart: Date
  /** Error and fatal groups, loudest first. */
  patterns: PatternGroup[]
}

const SEVERITY_RANK: Record<Severity, number> = {
  Trace: 0, Debug: 1, Information: 2, Warning: 3, Error: 4, Fatal: 5,
}

export function summarize(events: ParsedEvent[], lineCount: number, now = new Date()): UploadSummary {
  const dated = events.map((event) => event.timestamp).filter((value): value is Date => value !== null)
  const times = dated.map((value) => value.getTime())

  const newest = times.length > 0 ? new Date(Math.max(...times)) : null
  const oldest = times.length > 0 ? new Date(Math.min(...times)) : null

  // Land the last line five seconds before now and pull everything else with
  // it, so the shape of the burst survives.
  const shiftMs = newest ? now.getTime() - newest.getTime() - 5_000 : 0

  const groups = new Map<string, PatternGroup>()

  for (const event of events) {
    if (!isError(event)) continue

    const template = normalizeMessage(event.message)
    // Grouped by everything the server's fingerprint uses and this page can
    // see: the exception type and the top stack frames as well as the template.
    // Two different exceptions carrying the same sentence are two patterns
    // downstream, and so are two call sites throwing the same one - a preview
    // that merged either would be describing an outcome that is not going to
    // happen.
    const key = `${event.exceptionType ?? ''}|${template}|${normalizeStackFrames(event.stackLines)}`
    const existing = groups.get(key)

    if (existing) {
      existing.count += 1

      if (SEVERITY_RANK[event.severity] > SEVERITY_RANK[existing.severity]) {
        existing.severity = event.severity
      }

      continue
    }

    groups.set(key, {
      template,
      count: 1,
      severity: event.severity,
      exceptionType: event.exceptionType,
      sample: event.message,
    })
  }

  return {
    lineCount,
    eventCount: events.length,
    errorCount: events.filter(isError).length,
    stackTraceCount: events.filter((event) => event.stackLines.length > 0).length,
    oldest,
    newest,
    shiftMs: Number.isFinite(shiftMs) ? shiftMs : 0,
    // Undated lines are sent with the current time, so an undated log starts now.
    windowStart: oldest ? new Date(oldest.getTime() + shiftMs) : now,
    patterns: [...groups.values()].sort((a, b) => b.count - a.count),
  }
}

export interface ApiLogEvent {
  eventId: string
  service: string
  environment: string
  timestamp: string
  severity: Severity
  message: string
  metadata: Record<string, string>
  exceptionType?: string
  stackTrace?: string
}

function newEventId(): string {
  // Available in every secure context, which includes localhost. The fallback
  // keeps the page working over plain HTTP on a LAN address, where a missing id
  // would cost idempotency on retry rather than break the upload.
  if (typeof crypto?.randomUUID === 'function') return crypto.randomUUID()

  return `${Date.now().toString(16)}-${Math.random().toString(16).slice(2, 10)}`
}

export function toApiEvents(
  events: ParsedEvent[],
  service: string,
  environment: string,
  shiftMs: number,
  now = new Date(),
): ApiLogEvent[] {
  return events.map((event) => {
    const when = event.timestamp ? new Date(event.timestamp.getTime() + shiftMs) : now

    const payload: ApiLogEvent = {
      eventId: newEventId(),
      service,
      environment,
      timestamp: when.toISOString(),
      severity: event.severity,
      message: event.message.slice(0, MAX_MESSAGE),
      metadata: { uploadedBy: 'diagnose-page' },
    }

    if (event.exceptionType) payload.exceptionType = event.exceptionType.slice(0, MAX_EXCEPTION_TYPE)

    if (event.stackLines.length > 0) {
      payload.stackTrace = event.stackLines.join('\n').slice(0, MAX_STACK_TRACE)
    }

    return payload
  })
}

export interface IngestionOutcome {
  accepted: number
  rejected: number
  /** The first few rejections, as ingestion reports them, for showing verbatim. */
  errors: { index: number; field: string; message: string }[]
}

/**
 * Posts to ingestion, not to the read API.
 *
 * A different service on a different port, so this cannot go through apiGet /
 * apiPost - and that separation is the point. The pasted log takes the same
 * public path a production agent's logs take, which is what makes the answer on
 * the other end mean anything.
 */
export async function sendToIngestion(
  events: ApiLogEvent[],
  onProgress?: (sent: number, total: number) => void,
): Promise<IngestionOutcome> {
  const outcome: IngestionOutcome = { accepted: 0, rejected: 0, errors: [] }

  for (let start = 0; start < events.length; start += BATCH_SIZE) {
    const batch = events.slice(start, start + BATCH_SIZE)
    let response: Response

    try {
      response = await fetch(`${config.ingestionBaseUrl}/api/v1/logs/batch`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'X-Api-Key': config.apiKey },
        body: JSON.stringify({ events: batch }),
      })
    } catch {
      throw new ApiError(
        'Ingestion could not be reached, so nothing was uploaded. It may be starting up or unavailable.',
        0,
      )
    }

    if (!response.ok) {
      let detail = `Ingestion rejected the batch with status ${response.status}.`

      try {
        const problem = await response.json()
        if (problem?.detail) detail = problem.detail
      } catch {
        // Not JSON; the status stands on its own.
      }

      throw new ApiError(detail, response.status)
    }

    const result = await response.json()

    outcome.accepted += result?.accepted ?? 0
    outcome.rejected += result?.rejected ?? 0

    for (const error of result?.errors ?? []) {
      if (outcome.errors.length < 5) outcome.errors.push(error)
    }

    onProgress?.(Math.min(start + BATCH_SIZE, events.length), events.length)
  }

  return outcome
}
