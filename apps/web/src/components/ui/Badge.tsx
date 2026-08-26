import type { ReactNode } from 'react'

import { cn } from '../../lib/cn'

export type Severity = 'Critical' | 'High' | 'Medium' | 'Low'
export type IncidentStatus = 'Detected' | 'Investigating' | 'Resolved' | 'Ignored'
export type Health = 'Healthy' | 'Degraded' | 'Critical' | 'Unknown'

/**
 * Severity, carrying its meaning three ways: colour, a shape glyph, and the
 * word itself.
 *
 * Roughly one in twelve male engineers has a colour-vision deficiency, and
 * red/amber is the exact axis they lose - which is the axis severity uses.
 * The glyph and the label are not decoration; they are what makes this legible
 * to those users, on a projector, and in a screenshot pasted into Slack.
 */
const SEVERITY_STYLE: Record<Severity, { glyph: string; className: string }> = {
  Critical: { glyph: '■', className: 'text-sev-critical bg-sev-critical/12 border-sev-critical/30' },
  High: { glyph: '▲', className: 'text-sev-high bg-sev-high/12 border-sev-high/30' },
  Medium: { glyph: '●', className: 'text-sev-medium bg-sev-medium/12 border-sev-medium/25' },
  Low: { glyph: '○', className: 'text-sev-low bg-sev-low/12 border-sev-low/25' },
}

export function SeverityBadge({ severity, compact }: { severity: Severity; compact?: boolean }) {
  const style = SEVERITY_STYLE[severity]

  return (
    <span
      className={cn(
        'inline-flex items-center gap-1 rounded-[4px] border px-1.5 py-px',
        'text-[10px] font-semibold uppercase tracking-wide',
        style.className,
      )}
    >
      <span aria-hidden>{style.glyph}</span>
      {compact ? severity.slice(0, 4) : severity}
    </span>
  )
}

const STATUS_STYLE: Record<IncidentStatus, string> = {
  Detected: 'text-state-bad border-state-bad/40',
  Investigating: 'text-state-warn border-state-warn/40',
  Resolved: 'text-state-ok border-state-ok/40',
  Ignored: 'text-ink-subtle border-line-strong',
}

export function StatusBadge({ status }: { status: IncidentStatus }) {
  return (
    <span
      className={cn(
        'inline-flex items-center rounded-full border px-2 py-px text-[10px] font-medium',
        STATUS_STYLE[status],
      )}
    >
      {status}
    </span>
  )
}

const HEALTH_STYLE: Record<Health, { glyph: string; className: string }> = {
  Healthy: { glyph: '●', className: 'text-state-ok' },
  Degraded: { glyph: '◐', className: 'text-state-warn' },
  Critical: { glyph: '■', className: 'text-state-bad' },
  Unknown: { glyph: '○', className: 'text-state-idle' },
}

export function HealthDot({ health, label = true }: { health: Health; label?: boolean }) {
  const style = HEALTH_STYLE[health]

  return (
    <span className="inline-flex items-center gap-1.5">
      <span className={cn('text-[10px] leading-none', style.className)} aria-hidden>
        {style.glyph}
      </span>
      {label ? <span className="text-ink-muted">{health}</span> : <span className="sr-only">{health}</span>}
    </span>
  )
}

/** Neutral chip for metadata: environment, detection rule, service. */
export function Tag({ children, mono }: { children: ReactNode; mono?: boolean }) {
  return (
    <span
      className={cn(
        'inline-flex items-center rounded-[4px] border border-line bg-raised px-1.5 py-px',
        'text-[10px] text-ink-muted',
        mono && 'font-mono',
      )}
    >
      {children}
    </span>
  )
}
