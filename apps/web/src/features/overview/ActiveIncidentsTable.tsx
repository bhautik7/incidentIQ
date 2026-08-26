import { ShieldCheck } from 'lucide-react'
import { Link, useNavigate } from 'react-router'

import { SeverityBadge, StatusBadge, Tag } from '../../components/ui/Badge'
import { EmptyState } from '../../components/ui/States'
import { cn } from '../../lib/cn'
import { formatCount, formatDuration, formatRelative } from '../../lib/format'
import type { IncidentListItem } from '../../types/api'

export function ActiveIncidentsTable({ incidents }: { incidents: IncidentListItem[] }) {
  const navigate = useNavigate()

  if (incidents.length === 0) {
    return (
      <EmptyState
        icon={<ShieldCheck size={20} className="text-state-ok" aria-hidden />}
        title="No active incidents"
        description="Every monitored service in this environment is currently healthy."
      />
    )
  }

  return (
    <div className="overflow-x-auto rounded-panel border border-line bg-surface">
      <table className="w-full min-w-[860px] border-collapse text-[12px]">
        <caption className="sr-only">Active incidents</caption>
        <thead>
          <tr className="border-b border-line text-left text-[10px] uppercase tracking-[0.05em] text-ink-subtle">
            <th scope="col" className="px-3 py-1.5 font-medium">Severity</th>
            <th scope="col" className="px-3 py-1.5 font-medium">Service</th>
            <th scope="col" className="px-3 py-1.5 font-medium">Title</th>
            <th scope="col" className="px-3 py-1.5 font-medium">Status</th>
            <th scope="col" className="px-3 py-1.5 font-medium">Started</th>
            <th scope="col" className="px-3 py-1.5 text-right font-medium">Duration</th>
            <th scope="col" className="px-3 py-1.5 text-right font-medium">Events</th>
            <th scope="col" className="px-3 py-1.5 text-right font-medium">AI</th>
          </tr>
        </thead>

        <tbody className="divide-y divide-line">
          {incidents.map((incident) => {
            const durationSeconds =
              (new Date(incident.lastSeenAt).getTime() - new Date(incident.firstSeenAt).getTime()) / 1000

            return (
              <tr
                key={incident.id}
                // The row is clickable for speed, but the title is a real link
                // so the row is reachable by keyboard and openable in a new tab.
                onClick={() => navigate(`/incidents/${incident.id}`)}
                className="cursor-pointer transition-quick hover:bg-raised"
              >
                <td className="whitespace-nowrap px-3 py-1.5">
                  <SeverityBadge severity={incident.severity} />
                </td>
                <td className="whitespace-nowrap px-3 py-1.5 font-mono text-ink-muted">
                  {incident.service}
                </td>
                <td className="max-w-[320px] px-3 py-1.5">
                  <Link
                    to={`/incidents/${incident.id}`}
                    onClick={(event) => event.stopPropagation()}
                    className="block truncate text-ink hover:text-accent hover:underline"
                    title={incident.title}
                  >
                    {incident.title}
                  </Link>
                  {incident.suspectedDeploymentVersion && (
                    <span className="mt-0.5 inline-block">
                      <Tag mono>after {incident.suspectedDeploymentVersion}</Tag>
                    </span>
                  )}
                </td>
                <td className="whitespace-nowrap px-3 py-1.5">
                  <StatusBadge status={incident.status} />
                </td>
                <td className="whitespace-nowrap px-3 py-1.5 text-ink-muted">
                  {formatRelative(incident.firstSeenAt)}
                </td>
                <td className="whitespace-nowrap px-3 py-1.5 text-right tabular text-ink-muted">
                  {formatDuration(durationSeconds)}
                </td>
                <td className="whitespace-nowrap px-3 py-1.5 text-right tabular text-ink-muted">
                  {formatCount(incident.occurrenceCount)}
                </td>
                <td className="whitespace-nowrap px-3 py-1.5 text-right">
                  {incident.hasAnalysis && incident.analysisConfidence != null ? (
                    <ConfidencePill confidence={incident.analysisConfidence} />
                  ) : (
                    <span className="text-[10px] text-ink-subtle">analysing…</span>
                  )}
                </td>
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}

/**
 * Confidence as a bar plus a number.
 *
 * 87% and 34% look structurally identical when read quickly, so the bar
 * carries the meaning and the number is the detail. Low confidence is toned
 * down rather than hidden - the reader should see that it is weak.
 */
function ConfidencePill({ confidence }: { confidence: number }) {
  const percent = Math.round(confidence * 100)
  const strength = percent >= 70 ? 'high' : percent >= 40 ? 'medium' : 'low'

  return (
    <span className="inline-flex items-center gap-1.5" title={`AI confidence ${percent}%`}>
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
        className={cn(
          'tabular text-[11px]',
          strength === 'low' ? 'text-ink-subtle' : 'text-ink-muted',
        )}
      >
        {percent}%
      </span>
    </span>
  )
}
