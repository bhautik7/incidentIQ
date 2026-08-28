import { ChevronRight } from 'lucide-react'
import { useState } from 'react'

import { Tag } from '../../components/ui/Badge'
import { cn } from '../../lib/cn'
import { formatCount } from '../../lib/format'
import type { PatternGroup, UploadSummary } from './upload'

/**
 * What the parser understood, shown before anything is sent.
 *
 * This screen is where the tool earns the right to be believed. Every claim it
 * makes later - one incident, this many occurrences, this probable cause -
 * rests on the grouping shown here, and the user is the only one who can tell
 * whether four groups out of two hundred lines is right. Sending first and
 * explaining afterwards would move that check to a point where it is too late
 * to act on.
 *
 * The masked template sits next to a real line from the group on purpose: it is
 * the only way to see which parts were treated as noise.
 */
export function ParsedPreview({ summary }: { summary: UploadSummary }) {
  const shiftMinutes = Math.round(summary.shiftMs / 60_000)

  return (
    <div className="space-y-3">
      <dl className="grid grid-cols-2 gap-2 sm:grid-cols-4">
        <Stat label="Lines read" value={formatCount(summary.lineCount)} />
        <Stat
          label="Events"
          value={formatCount(summary.eventCount)}
          hint={
            summary.eventCount < summary.lineCount
              ? `${formatCount(summary.lineCount - summary.eventCount)} continuation line(s) folded into the event above`
              : undefined
          }
        />
        <Stat
          label="Errors"
          value={formatCount(summary.errorCount)}
          tone={summary.errorCount > 0 ? 'critical' : 'muted'}
        />
        <Stat
          label="Error patterns"
          value={formatCount(summary.patterns.length)}
          hint={summary.stackTraceCount > 0
            ? `${formatCount(summary.stackTraceCount)} with a stack trace attached`
            : undefined}
        />
      </dl>

      {/* Stated, not hidden. The timestamps that arrive downstream are not the
          ones in the file, and a user comparing the two would otherwise be
          right to conclude the tool had mangled their log. */}
      {shiftMinutes > 1 && (
        <p className="rounded-[4px] border border-line bg-raised px-2 py-1.5 text-[11px] text-ink-muted">
          These lines are {formatRelativeMinutes(shiftMinutes)} old. Every timestamp will be moved
          forward so the last line lands now, keeping the intervals between them intact — logs older
          than seven days are refused at ingestion, and only the last 48 hours are searchable.
        </p>
      )}

      {summary.patterns.length === 0 ? (
        <p className="rounded-[4px] border border-dashed border-line px-3 py-4 text-center text-[12px] text-ink-muted">
          No error or fatal lines were found. The log can still be uploaded and searched, but there
          is nothing here to open an incident for.
        </p>
      ) : (
        <div>
          <h3 className="mb-1.5 text-[11px] font-semibold uppercase tracking-[0.06em] text-ink-muted">
            Grouped errors
          </h3>

          <ul className="space-y-1">
            {summary.patterns.map((pattern, index) => (
              <PatternRow key={index} pattern={pattern} dominant={index === 0} />
            ))}
          </ul>

          <p className="mt-2 text-[11px] text-ink-subtle">
            Grouping shown here is computed in this browser, using the same masking rules and the
            same top three stack frames the pipeline uses. It remains an approximation: the
            authoritative fingerprint also folds in the organization, the service and the
            environment, which this page cannot know. An incident will be opened for the largest
            group.
          </p>
        </div>
      )}
    </div>
  )
}

function PatternRow({ pattern, dominant }: { pattern: PatternGroup; dominant: boolean }) {
  const [expanded, setExpanded] = useState(false)

  return (
    <li
      className={cn(
        'rounded-[4px] border bg-surface px-2 py-1.5',
        dominant ? 'border-accent/40' : 'border-line',
      )}
    >
      <div className="flex items-start gap-2">
        <span className="tabular mt-px shrink-0 text-[12px] font-medium text-ink">
          ×{formatCount(pattern.count)}
        </span>

        <div className="min-w-0 flex-1">
          <p className="break-words font-mono text-[12px] leading-relaxed text-ink">
            {pattern.template}
          </p>

          <div className="mt-1 flex flex-wrap items-center gap-1.5">
            {pattern.exceptionType && <Tag mono>{pattern.exceptionType}</Tag>}
            <Tag>{pattern.severity}</Tag>
            {dominant && <Tag>incident will be opened for this</Tag>}
          </div>

          <button
            type="button"
            onClick={() => setExpanded((open) => !open)}
            aria-expanded={expanded}
            className="mt-1 inline-flex items-center gap-1 text-[11px] text-ink-muted transition-quick hover:text-ink"
          >
            <ChevronRight
              size={12}
              aria-hidden
              className={cn('transition-transform', expanded && 'rotate-90')}
            />
            {expanded ? 'Hide' : 'Show'} a real line from this group
          </button>

          {expanded && (
            <p className="mt-1 break-all rounded-[4px] border border-line bg-canvas px-1.5 py-1 font-mono text-[11px] leading-relaxed text-ink-muted">
              {pattern.sample}
            </p>
          )}
        </div>
      </div>
    </li>
  )
}

function Stat({
  label,
  value,
  hint,
  tone = 'default',
}: {
  label: string
  value: string
  hint?: string
  tone?: 'default' | 'critical' | 'muted'
}) {
  return (
    <div className="rounded-[4px] border border-line bg-surface px-2 py-1.5">
      <dt className="text-[10px] uppercase tracking-[0.05em] text-ink-subtle">{label}</dt>
      <dd
        className={cn(
          'tabular text-[16px] font-semibold leading-tight',
          tone === 'critical' && 'text-sev-critical',
          tone === 'muted' && 'text-ink-muted',
          tone === 'default' && 'text-ink',
        )}
      >
        {value}
      </dd>
      {hint && <p className="mt-0.5 text-[10px] leading-snug text-ink-subtle">{hint}</p>}
    </div>
  )
}

function formatRelativeMinutes(minutes: number): string {
  if (minutes < 90) return `${minutes} minute(s)`
  const hours = minutes / 60
  return hours < 48 ? `${hours.toFixed(1)} hour(s)` : `${(hours / 24).toFixed(1)} day(s)`
}
