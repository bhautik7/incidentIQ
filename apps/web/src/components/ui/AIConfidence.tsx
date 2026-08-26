import { cn } from '../../lib/cn'

/**
 * AI confidence as a bar, a number and - when it is weak - a word.
 *
 * 87% and 34% look structurally identical when read quickly, so the bar carries
 * the meaning and the number is the detail. Below 40% the value is named as a
 * hint rather than dressed up as a measurement: a tool that overstates its own
 * certainty gets ignored after the first wrong call.
 */
export function AIConfidence({ confidence, showLabel }: { confidence: number; showLabel?: boolean }) {
  const percent = Math.round(confidence * 100)
  const strength = percent >= 70 ? 'high' : percent >= 40 ? 'medium' : 'low'

  const description =
    strength === 'low' ? 'Low - treat as a hint' : strength === 'medium' ? 'Medium' : 'High'

  return (
    <span className="inline-flex items-center gap-1.5" title={`AI confidence ${percent}% - ${description}`}>
      <span aria-hidden className="h-1 w-8 overflow-hidden rounded-full bg-line">
        <span
          className={cn(
            'block h-full rounded-full',
            strength === 'high' && 'bg-state-ok',
            strength === 'medium' && 'bg-sev-medium',
            strength === 'low' && 'bg-ink-subtle',
          )}
          style={{ width: `${percent}%` }}
        />
      </span>

      <span
        className={cn('tabular text-[11px]', strength === 'low' ? 'text-ink-subtle' : 'text-ink-muted')}
      >
        {percent}%
      </span>

      {showLabel && <span className="text-[10px] text-ink-subtle">{description}</span>}
    </span>
  )
}

/** Placeholder for an incident whose analysis has not completed yet. */
export function AIPending() {
  return <span className="text-[10px] text-ink-subtle">analysing…</span>
}
