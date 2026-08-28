/**
 * The log parser, in the browser.
 *
 * A port of `scripts/upload-log.py`, and deliberately a port rather than a
 * second design: the script has been run against real files and already
 * handles the two things that make real logs harder than generated ones.
 *
 * - **Stack traces span many lines.** A continuation line belongs to the
 *   message above it. Sent as separate events they become one pattern per
 *   stack frame, and the actual error is buried under its own trace.
 * - **Formats vary.** Structured JSON, Serilog's compact format and plain text
 *   turn up in the same estate. Each is parsed; an unrecognised line still goes
 *   through as a message, because dropping a line silently is worse than
 *   classifying it imperfectly.
 *
 * It runs here, before anything is sent, so the user can see what was
 * understood while they can still correct it - which is the whole argument for
 * the preview step. Ingestion keeps its existing contract and receives exactly
 * what the script sends.
 */

export type Severity = 'Trace' | 'Debug' | 'Information' | 'Warning' | 'Error' | 'Fatal'

export interface ParsedEvent {
  message: string
  severity: Severity
  timestamp: Date | null
  exceptionType: string | null
  stackLines: string[]
}

const LEVEL_WORDS: Record<string, Severity> = {
  trace: 'Trace', verbose: 'Trace', debug: 'Debug', dbg: 'Debug',
  info: 'Information', information: 'Information', inf: 'Information',
  notice: 'Information', warn: 'Warning', warning: 'Warning', wrn: 'Warning',
  error: 'Error', err: 'Error', eror: 'Error', fatal: 'Fatal',
  critical: 'Fatal', crit: 'Fatal', ftl: 'Fatal',
}

/** Leading timestamp in the shapes that actually turn up in log files. */
const TIMESTAMP_PATTERNS = [
  /^\[?(\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:[.,]\d+)?(?:Z|[+-]\d{2}:?\d{2})?)\]?/,
  /^\[?(\d{2}\/[A-Za-z]{3}\/\d{4}[: ]\d{2}:\d{2}:\d{2})\]?/,
]

const LEVEL_PATTERN =
  /\b(TRACE|VERBOSE|DEBUG|DBG|INFO|INFORMATION|INF|NOTICE|WARN|WARNING|WRN|ERROR|ERR|EROR|FATAL|CRITICAL|CRIT|FTL)\b/i

/** "System.TimeoutException: message" / "java.lang.NullPointerException: message" */
const EXCEPTION_PATTERN = /\b((?:[A-Za-z_]\w*\.)+[A-Z]\w*(?:Exception|Error))\b\s*:?/

/**
 * A line that continues the one above it: an indented frame, "at Foo.Bar(...)",
 * "Caused by:", or a chained "--->".
 */
const CONTINUATION_PATTERN = /^(\s+|at\s|Caused by:|\.\.\.\s|--->|\tat\s)/

const MONTHS: Record<string, number> = {
  jan: 0, feb: 1, mar: 2, apr: 3, may: 4, jun: 5,
  jul: 6, aug: 7, sep: 8, oct: 9, nov: 10, dec: 11,
}

export function parseTimestamp(raw: string): Date | null {
  const cleaned = raw.trim().replace(',', '.')

  // "12/Mar/2026:09:14:22", the access-log shape. Parsed explicitly because
  // Date.parse is free to reject it, and browsers disagree about whether it
  // does. Treated as UTC, like the script.
  const apache = /^(\d{2})\/([A-Za-z]{3})\/(\d{4})[: ](\d{2}):(\d{2}):(\d{2})$/.exec(cleaned)

  if (apache) {
    const month = MONTHS[apache[2].toLowerCase()]
    if (month === undefined) return null

    return new Date(
      Date.UTC(Number(apache[3]), month, Number(apache[1]),
        Number(apache[4]), Number(apache[5]), Number(apache[6])),
    )
  }

  // No zone means the log was written in some local time we cannot know, so it
  // is read as UTC - the same assumption the script makes, and the one that
  // keeps a file's internal intervals intact.
  const iso = /^\d{4}-\d{2}-\d{2}[T ]/.test(cleaned) && !/(Z|[+-]\d{2}:?\d{2})$/.test(cleaned)
    ? `${cleaned.replace(' ', 'T')}Z`
    : cleaned.replace(' ', 'T')

  const parsed = new Date(iso)
  return Number.isNaN(parsed.getTime()) ? null : parsed
}

function severityFrom(raw: string | undefined | null): Severity {
  return LEVEL_WORDS[(raw ?? '').trim().toLowerCase()] ?? 'Information'
}

/** Structured JSON, including Serilog's compact format (@t, @l, @m, @x). */
function parseStructured(line: string): ParsedEvent | null {
  const stripped = line.trim()

  if (!(stripped.startsWith('{') && stripped.endsWith('}'))) return null

  let record: unknown

  try {
    record = JSON.parse(stripped)
  } catch {
    return null
  }

  if (typeof record !== 'object' || record === null || Array.isArray(record)) return null

  const fields = record as Record<string, unknown>

  const pick = (...names: string[]): string | null => {
    for (const name of names) {
      const value = fields[name]
      if (typeof value === 'string' && value.trim()) return value
    }
    return null
  }

  const message = pick('@m', '@mt', 'message', 'msg', 'Message', 'event', 'log') ?? stripped
  const rawTime = pick('@t', 'timestamp', 'time', '@timestamp', 'Timestamp', 'asctime')
  const exception = pick('@x', 'exception', 'exc_info', 'Exception', 'stack_trace', 'stackTrace')

  const event: ParsedEvent = {
    message,
    // Serilog writes "Warning"; Python's logging writes "WARNING". Both land on
    // the same canonical value.
    severity: severityFrom(pick('@l', 'level', 'severity', 'levelname', 'Level')),
    timestamp: rawTime ? parseTimestamp(rawTime) : null,
    exceptionType: null,
    stackLines: [],
  }

  if (exception) {
    event.stackLines = exception.split('\n')
    event.exceptionType = EXCEPTION_PATTERN.exec(exception)?.[1] ?? null
  }

  if (!event.exceptionType) {
    event.exceptionType = EXCEPTION_PATTERN.exec(message)?.[1] ?? null
  }

  return event
}

/** Plain text: pull off a leading timestamp and a level word, keep the rest. */
function parsePlain(line: string): ParsedEvent {
  let remainder = line.replace(/\s+$/, '')
  let timestamp: Date | null = null

  for (const pattern of TIMESTAMP_PATTERNS) {
    const match = pattern.exec(remainder)

    if (match) {
      timestamp = parseTimestamp(match[1])
      remainder = remainder.slice(match[0].length).replace(/^[ \-\t|]+/, '')
      break
    }
  }

  let severity: Severity = 'Information'
  const head = remainder.slice(0, 60)
  const levelMatch = LEVEL_PATTERN.exec(head)

  if (levelMatch) {
    severity = severityFrom(levelMatch[1])

    // Only strip the level when it sits at the front; a level word inside the
    // message is part of the message.
    if (levelMatch.index < 20) {
      remainder = (remainder.slice(0, levelMatch.index) + remainder.slice(levelMatch.index + levelMatch[0].length))
        .replace(/^[ \-\t|:[\]]+/, '')
    }
  }

  return {
    message: remainder.trim() || line.trim(),
    severity,
    timestamp,
    exceptionType: EXCEPTION_PATTERN.exec(remainder)?.[1] ?? null,
    stackLines: [],
  }
}

export function parseLog(text: string): ParsedEvent[] {
  const events: ParsedEvent[] = []

  for (const raw of text.split(/\r?\n/)) {
    if (!raw.trim()) continue

    // A continuation belongs to the event above it. Treating it as its own
    // event would create one pattern per stack frame.
    const previous = events[events.length - 1]

    if (previous && CONTINUATION_PATTERN.test(raw) && !raw.trim().startsWith('{')) {
      previous.stackLines.push(raw.replace(/\s+$/, ''))

      if (!previous.exceptionType) {
        previous.exceptionType = EXCEPTION_PATTERN.exec(raw)?.[1] ?? null
      }

      continue
    }

    events.push(parseStructured(raw) ?? parsePlain(raw))
  }

  return events
}

export const ERROR_SEVERITIES: Severity[] = ['Error', 'Fatal']

export function isError(event: ParsedEvent): boolean {
  return ERROR_SEVERITIES.includes(event.severity)
}
