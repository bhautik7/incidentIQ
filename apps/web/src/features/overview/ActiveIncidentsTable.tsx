import { ShieldCheck } from 'lucide-react'
import { Link, useNavigate } from 'react-router'

import { AIConfidence, AIPending } from '../../components/ui/AIConfidence'
import { SeverityBadge, StatusBadge, Tag } from '../../components/ui/Badge'
import { EmptyState } from '../../components/ui/States'
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
                    <AIConfidence confidence={incident.analysisConfidence} />
                  ) : (
                    <AIPending />
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
