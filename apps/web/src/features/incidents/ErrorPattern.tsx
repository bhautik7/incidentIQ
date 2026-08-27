import { ChevronRight, Copy } from 'lucide-react'
import { useState } from 'react'

import { Tag } from '../../components/ui/Badge'
import { cn } from '../../lib/cn'
import { formatCount } from '../../lib/format'
import type { IncidentPattern, IncidentSample } from '../../types/api'

/**
 * The normalised error behind the incident, and the raw lines it came from.
 *
 * The template is what makes 4,200 log lines into one incident: placeholders
 * name what was recognised ({NUM}), not what it meant. Showing it next to a
 * real sample is what makes the deduplication legible rather than magic - you
 * can see which parts were treated as noise.
 *
 * Samples are collapsed by default. They are the raw material, useful when the
 * aggregate is not enough, and expanding twenty log lines by default would
 * push everything below them off the screen.
 */
export function ErrorPattern({
  pattern,
  samples,
}: {
  pattern: IncidentPattern
  samples: IncidentSample[]
}) {
  const [expanded, setExpanded] = useState(false)

  return (
    <div className="space-y-2.5">
      <div>
        <p className="break-words font-mono text-[12px] leading-relaxed text-ink">
          {pattern.messageTemplate}
        </p>

        <div className="mt-1.5 flex flex-wrap items-center gap-1.5">
          {pattern.exceptionType && <Tag mono>{pattern.exceptionType}</Tag>}
          {pattern.httpStatusCode !== null && <Tag mono>HTTP {pattern.httpStatusCode}</Tag>}
          <Tag>{formatCount(pattern.occurrenceCount)} occurrences</Tag>
        </div>
      </div>

      <div className="flex items-center gap-1.5">
        <span className="text-[10px] uppercase tracking-[0.05em] text-ink-subtle">Fingerprint</span>
        <code className="truncate font-mono text-[11px] text-ink-muted" title={pattern.fingerprint}>
          {pattern.fingerprint.slice(0, 16)}…
        </code>
        <CopyButton value={pattern.fingerprint} label="Copy fingerprint" />
      </div>

      {samples.length > 0 && (
        <div>
          <button
            type="button"
            onClick={() => setExpanded((open) => !open)}
            aria-expanded={expanded}
            className="inline-flex items-center gap-1 text-[11px] text-ink-muted transition-quick hover:text-ink"
          >
            <ChevronRight
              size={12}
              aria-hidden
              className={cn('transition-transform', expanded && 'rotate-90')}
            />
            {expanded ? 'Hide' : 'Show'} {samples.length} sample log line
            {samples.length === 1 ? '' : 's'}
          </button>

          {expanded && (
            <ul className="mt-1.5 max-h-64 space-y-px overflow-y-auto rounded-[4px] border border-line bg-canvas p-1.5">
              {samples.map((sample, index) => (
                <li key={index} className="flex gap-2 px-1 py-0.5 font-mono text-[11px] leading-relaxed">
                  <time
                    dateTime={sample.occurredAt}
                    className="shrink-0 text-ink-subtle"
                    title={new Date(sample.occurredAt).toLocaleString()}
                  >
                    {new Date(sample.occurredAt).toLocaleTimeString([], {
                      hour: '2-digit',
                      minute: '2-digit',
                      second: '2-digit',
                    })}
                  </time>
                  <span className="shrink-0 text-sev-critical">{sample.level.toUpperCase()}</span>
                  {/* The raw message, unlike the template above. This is the
                      value the LLM boundary deliberately never carries. */}
                  <span className="min-w-0 break-all text-ink-muted">{sample.message}</span>
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
    </div>
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
      {copied ? (
        <span className="text-[10px] text-state-ok">copied</span>
      ) : (
        <Copy size={11} aria-hidden />
      )}
    </button>
  )
}
