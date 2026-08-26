import { Link } from 'react-router'

import { AIConfidence, AIPending } from '../../components/ui/AIConfidence'
import { SeverityBadge, StatusBadge, Tag } from '../../components/ui/Badge'
import type { Column } from '../../components/ui/DataTable'
import { formatCount, formatDuration, formatRelative } from '../../lib/format'
import type { IncidentListItem } from '../../types/api'

/**
 * The incident list's columns.
 *
 * Kept apart from the page because they are a description of the data, not of
 * the screen: the same definitions drive whatever else needs to show incidents
 * in a table. Every sortKey here is a column the API can actually order by -
 * an unsortable column simply omits it rather than pretending.
 */
export const incidentColumns: Column<IncidentListItem>[] = [
  {
    id: 'severity',
    header: 'Sev',
    sortKey: 'severity',
    defaultDirection: 'desc',
    className: 'w-[92px] whitespace-nowrap',
    headerLabel: 'Sort by severity',
    cell: (incident) => <SeverityBadge severity={incident.severity} />,
  },
  {
    id: 'title',
    header: 'Title',
    sortKey: 'title',
    defaultDirection: 'asc',
    className: 'max-w-0',
    cell: (incident) => (
      <>
        <Link
          to={`/incidents/${incident.id}`}
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
      </>
    ),
  },
  {
    id: 'service',
    header: 'Service',
    sortKey: 'service',
    defaultDirection: 'asc',
    className: 'w-[150px] whitespace-nowrap',
    cell: (incident) => <span className="font-mono text-ink-muted">{incident.service}</span>,
  },
  {
    id: 'environment',
    header: 'Env',
    className: 'w-[90px] whitespace-nowrap',
    cell: (incident) => <Tag>{incident.environment}</Tag>,
  },
  {
    id: 'status',
    header: 'Status',
    sortKey: 'status',
    defaultDirection: 'desc',
    className: 'w-[112px] whitespace-nowrap',
    cell: (incident) => <StatusBadge status={incident.status} />,
  },
  {
    id: 'started',
    header: 'Started',
    sortKey: 'firstSeen',
    defaultDirection: 'desc',
    className: 'w-[96px] whitespace-nowrap',
    cell: (incident) => (
      <span className="text-ink-muted" title={new Date(incident.firstSeenAt).toLocaleString()}>
        {formatRelative(incident.firstSeenAt)}
      </span>
    ),
  },
  {
    id: 'duration',
    header: 'Dur',
    sortKey: 'lastSeen',
    defaultDirection: 'desc',
    align: 'right',
    className: 'w-[72px] whitespace-nowrap',
    headerLabel: 'Sort by most recently active',
    cell: (incident) => (
      <span
        className="text-ink-muted"
        title={`Last seen ${new Date(incident.lastSeenAt).toLocaleString()}`}
      >
        {formatDuration(
          (new Date(incident.lastSeenAt).getTime() - new Date(incident.firstSeenAt).getTime()) / 1000,
        )}
      </span>
    ),
  },
  {
    id: 'events',
    header: 'Events',
    sortKey: 'occurrences',
    defaultDirection: 'desc',
    align: 'right',
    className: 'w-[80px] whitespace-nowrap',
    cell: (incident) => (
      <span className="text-ink-muted" title={`${incident.occurrenceCount.toLocaleString()} occurrences`}>
        {formatCount(incident.occurrenceCount)}
      </span>
    ),
  },
  {
    id: 'ai',
    header: 'AI',
    // No sortKey: the confidence lives on the latest completed analysis, which
    // the list endpoint reads through a subquery and cannot order by.
    align: 'right',
    className: 'w-[96px] whitespace-nowrap',
    cell: (incident) =>
      incident.hasAnalysis && incident.analysisConfidence != null ? (
        <AIConfidence confidence={incident.analysisConfidence} />
      ) : (
        <AIPending />
      ),
  },
]
