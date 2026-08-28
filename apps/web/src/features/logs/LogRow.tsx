import { ChevronRight, Copy, Filter } from 'lucide-react'
import { useState } from 'react'
import { Link } from 'react-router'

import { cn } from '../../lib/cn'
import type { LogEntry, LogLevel } from '../../types/api'

/** The severity ramp, on the level text and as a bar down the row's left edge. */
const LEVEL_STYLE: Record<LogLevel, { text: string; edge: string }> = {
  Fatal: { text: 'text-sev-critical', edge: 'bg-sev-critical' },
  Error: { text: 'text-sev-critical', edge: 'bg-sev-critical' },
  Warning: { text: 'text-sev-high', edge: 'bg-sev-high' },
  Information: { text: 'text-ink-muted', edge: 'bg-transparent' },
  Debug: { text: 'text-ink-subtle', edge: 'bg-transparent' },
  Trace: { text: 'text-ink-subtle', edge: 'bg-transparent' },
}

/**
 * One log line, expandable to everything the line carried.
 *
 * Collapsed it is a single dense row - the whole point of a log view is
 * comparing many lines at once, and a card per line would show four. Expanded
 * it shows the fields and the raw JSON, because the moment someone stops
 * scanning and starts reading, the structured properties are usually the reason.
 *
 * Timestamps carry milliseconds. In a log view the ordering of two lines
 * 40ms apart is frequently the whole question.
 */
export function LogRow({
  entry,
  expanded,
  onToggle,
  onFilterBy,
}: {
  entry: LogEntry
  expanded: boolean
  onToggle: () => void
  onFilterBy: (field: 'service' | 'level' | 'traceId' | 'fingerprint', value: string) => void
}) {
  const level = LEVEL_STYLE[entry.level] ?? LEVEL_STYLE.Information
  const occurred = new Date(entry.occurredAt)

  return (
    <div className={cn('border-b border-line/50', expanded && 'bg-raised/40')}>
      <div
        role="button"
        tabIndex={0}
        aria-expanded={expanded}
        onClick={(event) => {
          if ((event.target as HTMLElement).closest('a,button')) return
          onToggle()
        }}
        onKeyDown={(event) => {
          if (event.key === 'Enter' || event.key === ' ') {
            event.preventDefault()
            onToggle()
          }
        }}
        className={cn(
          'flex cursor-pointer items-center gap-3 px-2 py-1 font-mono text-[11px]',
          'transition-quick hover:bg-raised focus-visible:outline-2 focus-visible:-outline-offset-2',
          'focus-visible:outline-accent',
        )}
      >
        {/* The severity bar. Colour is never the only signal - the level word
            sits two columns along - but it is what makes a wall of lines
            scannable at a glance. */}
        <span aria-hidden className={cn('h-3.5 w-0.5 shrink-0 rounded-full', level.edge)} />

        <ChevronRight
          size={10}
          aria-hidden
          className={cn('shrink-0 text-ink-subtle transition-transform', expanded && 'rotate-90')}
        />

        <time
          dateTime={entry.occurredAt}
          className="w-[92px] shrink-0 tabular text-ink-subtle"
          title={occurred.toLocaleString()}
        >
          {occurred.toLocaleTimeString([], { hour12: false })}.
          {String(occurred.getMilliseconds()).padStart(3, '0')}
        </time>

        <span className={cn('w-[52px] shrink-0 font-semibold uppercase', level.text)}>
          {entry.level.slice(0, 5)}
        </span>

        <span className="w-[132px] shrink-0 truncate text-ink-muted" title={entry.service}>
          {entry.service}
        </span>

        <span className="min-w-0 flex-1 truncate text-ink" title={entry.message}>
          {entry.message}
        </span>

        <span className="w-[104px] shrink-0 truncate text-ink-subtle" title={entry.traceId ?? undefined}>
          {entry.traceId ?? '—'}
        </span>

        <span className="w-[64px] shrink-0">
          {entry.incidentId ? (
            <Link
              to={`/incidents/${entry.incidentId}`}
              onClick={(event) => event.stopPropagation()}
              className="text-accent hover:underline"
            >
              open
            </Link>
          ) : (
            <span className="text-ink-subtle">—</span>
          )}
        </span>
      </div>

      {expanded && (
        <div className="space-y-2 border-t border-line/50 px-2 py-2 pl-[26px]">
          <dl className="grid grid-cols-2 gap-x-4 gap-y-1 text-[11px] sm:grid-cols-4">
            <Field label="Occurred">{occurred.toISOString()}</Field>
            <Field label="Received">{new Date(entry.receivedAt).toISOString()}</Field>
            <Field label="Environment">{entry.environment}</Field>
            <Field label="Host">{entry.host ?? '—'}</Field>
            {entry.exceptionType && <Field label="Exception">{entry.exceptionType}</Field>}
            {entry.spanId && <Field label="Span">{entry.spanId}</Field>}
          </dl>

          {/* Filtering by a value you can see beats retyping it into a box,
              and it is how someone goes from one line to every line like it. */}
          <div className="flex flex-wrap items-center gap-1.5">
            <FilterChip onClick={() => onFilterBy('service', entry.service)}>
              service: {entry.service}
            </FilterChip>
            <FilterChip onClick={() => onFilterBy('level', entry.level)}>
              level: {entry.level}
            </FilterChip>
            {entry.traceId && (
              <>
                <FilterChip onClick={() => onFilterBy('traceId', entry.traceId!)}>
                  trace: {entry.traceId}
                </FilterChip>
                <CopyButton value={entry.traceId} label="Copy trace ID" />
              </>
            )}
            {entry.fingerprint && (
              <FilterChip onClick={() => onFilterBy('fingerprint', entry.fingerprint!)}>
                fingerprint: {entry.fingerprint.slice(0, 12)}…
              </FilterChip>
            )}
          </div>

          {entry.stackTrace && (
            <Block label="Stack trace">{entry.stackTrace}</Block>
          )}

          {entry.properties && <Block label="Properties">{formatJson(entry.properties)}</Block>}
        </div>
      )}
    </div>
  )
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="min-w-0">
      <dt className="text-[9px] uppercase tracking-[0.05em] text-ink-subtle">{label}</dt>
      <dd className="truncate font-mono text-ink-muted">{children}</dd>
    </div>
  )
}

function Block({ label, children }: { label: string; children: string }) {
  return (
    <div>
      <p className="mb-0.5 text-[9px] uppercase tracking-[0.05em] text-ink-subtle">{label}</p>
      <pre className="max-h-56 overflow-auto rounded-[4px] border border-line bg-canvas p-1.5 font-mono text-[10px] leading-relaxed text-ink-muted">
        {children}
      </pre>
    </div>
  )
}

function FilterChip({ onClick, children }: { onClick: () => void; children: React.ReactNode }) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={cn(
        'inline-flex items-center gap-1 rounded-[4px] border border-line bg-raised px-1.5 py-px',
        'font-mono text-[10px] text-ink-muted transition-quick hover:border-line-strong hover:text-ink',
      )}
    >
      <Filter size={9} aria-hidden />
      {children}
    </button>
  )
}

function CopyButton({ value, label }: { value: string; label: string }) {
  const [copied, setCopied] = useState(false)

  return (
    <button
      type="button"
      aria-label={label}
      onClick={() => {
        void navigator.clipboard?.writeText(value).then(() => {
          setCopied(true)
          window.setTimeout(() => setCopied(false), 1200)
        })
      }}
      className="text-ink-subtle transition-quick hover:text-ink"
    >
      {copied ? <span className="text-[10px] text-state-ok">copied</span> : <Copy size={10} aria-hidden />}
    </button>
  )
}

/** Pretty-prints the jsonb, falling back to the raw text if it will not parse. */
function formatJson(raw: string): string {
  try {
    return JSON.stringify(JSON.parse(raw), null, 2)
  } catch {
    // A cross-language boundary: the worker writes this column, and a value
    // that will not parse should still be readable rather than hidden.
    return raw
  }
}
