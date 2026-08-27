import {
  AlertTriangle,
  ArrowUpCircle,
  Ban,
  CheckCircle2,
  Flag,
  MessageSquare,
  RotateCcw,
  Sparkles,
  UserPlus,
  Wrench,
} from 'lucide-react'
import type { ReactNode } from 'react'

import { cn } from '../../lib/cn'
import type { IncidentTimelineEntry, TimelineEntryType } from '../../types/api'

/**
 * What happened, in order.
 *
 * The chronology is the half of the page the AI panel cannot replace: the model
 * says what it thinks caused this, and the timeline says what actually occurred
 * and when. Someone joining a live incident reads this first.
 *
 * System and human entries are visually distinct on purpose. "The detector
 * opened this" and "Ravi decided to roll back" are different kinds of claim,
 * and a timeline that renders them identically invites reading a machine's
 * guess as a colleague's decision.
 */
const ENTRY_STYLE: Record<TimelineEntryType, { icon: ReactNode; ring: string; label: string }> = {
  Created: { icon: <Flag size={11} />, ring: 'text-sev-critical border-sev-critical/40', label: 'Incident opened' },
  Escalated: { icon: <ArrowUpCircle size={11} />, ring: 'text-sev-high border-sev-high/40', label: 'Escalated' },
  SeverityChanged: {
    icon: <AlertTriangle size={11} />,
    ring: 'text-sev-high border-sev-high/40',
    label: 'Severity changed',
  },
  InvestigationStarted: {
    icon: <Wrench size={11} />,
    ring: 'text-state-warn border-state-warn/40',
    label: 'Investigation started',
  },
  Assigned: { icon: <UserPlus size={11} />, ring: 'text-accent border-accent/40', label: 'Assigned' },
  Commented: { icon: <MessageSquare size={11} />, ring: 'text-ink-muted border-line-strong', label: 'Note' },
  AiAnalysisCompleted: {
    icon: <Sparkles size={11} />,
    ring: 'text-accent border-accent/40',
    label: 'AI analysis completed',
  },
  Resolved: { icon: <CheckCircle2 size={11} />, ring: 'text-state-ok border-state-ok/40', label: 'Resolved' },
  Reopened: { icon: <RotateCcw size={11} />, ring: 'text-sev-high border-sev-high/40', label: 'Reopened' },
  Ignored: { icon: <Ban size={11} />, ring: 'text-ink-subtle border-line-strong', label: 'Ignored' },
}

export function Timeline({ entries }: { entries: IncidentTimelineEntry[] }) {
  if (entries.length === 0) {
    return (
      <p className="px-1 py-3 text-[12px] text-ink-muted">
        Nothing has been recorded yet. Entries appear as the detector, the AI worker and people act on
        this incident.
      </p>
    )
  }

  return (
    <ol className="relative space-y-0">
      {entries.map((entry, index) => {
        const style = ENTRY_STYLE[entry.type] ?? ENTRY_STYLE.Commented
        const isLast = index === entries.length - 1

        return (
          <li key={`${entry.occurredAt}-${index}`} className="relative flex gap-2.5 pb-3 last:pb-0">
            {/* The rail, drawn behind the markers and stopped at the last one
                so the sequence reads as finished rather than cut off. */}
            {!isLast && (
              <span aria-hidden className="absolute left-[9px] top-[18px] bottom-0 w-px bg-line" />
            )}

            <span
              aria-hidden
              className={cn(
                'relative z-10 mt-0.5 flex size-[18px] shrink-0 items-center justify-center',
                'rounded-full border bg-surface',
                style.ring,
              )}
            >
              {style.icon}
            </span>

            <div className="min-w-0 flex-1">
              <div className="flex flex-wrap items-baseline gap-x-2">
                <time
                  dateTime={entry.occurredAt}
                  className="tabular text-[11px] text-ink-muted"
                  title={new Date(entry.occurredAt).toLocaleString()}
                >
                  {new Date(entry.occurredAt).toLocaleTimeString([], {
                    hour: '2-digit',
                    minute: '2-digit',
                  })}
                </time>

                <span className="text-[11px] font-medium text-ink">{style.label}</span>

                {/* Who, and never a bare "User". An entry the detector wrote
                    says so; one a person wrote names them. */}
                <span className="text-[10px] text-ink-subtle">
                  {entry.actorName ?? (entry.actorType === 'Ai' ? 'AI worker' : 'System')}
                </span>
              </div>

              <p
                className={cn(
                  'mt-0.5 text-[12px] leading-snug',
                  // A note is somebody's words; the rest is generated prose.
                  entry.type === 'Commented' ? 'text-ink' : 'text-ink-muted',
                )}
              >
                {entry.message}
              </p>
            </div>
          </li>
        )
      })}
    </ol>
  )
}
