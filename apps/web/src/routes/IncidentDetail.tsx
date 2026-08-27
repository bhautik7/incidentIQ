import { ArrowLeft, FileSearch, Rocket, Search } from 'lucide-react'
import type { ReactNode } from 'react'
import { Link, useParams } from 'react-router'

import { SeverityBadge, StatusBadge, Tag } from '../components/ui/Badge'
import { Skeleton } from '../components/ui/Skeleton'
import { EmptyState, ErrorState } from '../components/ui/States'
import { AiInvestigationPanel } from '../features/incidents/AiInvestigationPanel'
import { DeploymentCorrelation } from '../features/incidents/DeploymentCorrelation'
import { ErrorPattern } from '../features/incidents/ErrorPattern'
import { IncidentActions } from '../features/incidents/IncidentActions'
import { Timeline } from '../features/incidents/Timeline'
import type { ApiError } from '../lib/api/client'
import { useIncident, useIncidentAction, useMembers } from '../lib/api/queries'
import { formatCount, formatDuration } from '../lib/format'
import type { SimilarIncident } from '../types/api'

/**
 * The incident page. Everything else in the product exists to get someone here
 * faster, or to answer a question this page raised.
 *
 * Two columns, and the split is the argument: the left answers "what does the
 * system think happened", the right answers "what actually happened, and in
 * what order". Those two questions are asked together during an outage, and
 * forcing a scroll between them is the difference between a tool and a report.
 *
 * On a narrow viewport the columns stack with the timeline second, because the
 * cause is what someone opens this page for.
 */
export default function IncidentDetailPage() {
  const { incidentId = '' } = useParams()
  const query = useIncident(incidentId)
  const members = useMembers()
  const action = useIncidentAction(incidentId)

  // A query that wanted to fetch and could not is paused, not failed: it stays
  // pending, so without this the page shimmers indefinitely and never says why.
  // Same reasoning as DataTable - a skeleton promises data is on its way.
  if (query.isPaused && query.data === undefined) {
    return (
      <>
        <BackLink />
        <ErrorState
          title="Waiting to reach the API"
          description="The request could not be sent and is queued for retry. The API may be restarting, or this machine may have lost its connection."
          onRetry={() => void query.refetch()}
        />
      </>
    )
  }

  if (query.isError) {
    const error = query.error as ApiError

    return (
      <>
        <BackLink />
        {error?.status === 404 ? (
          <EmptyState
            icon={<Search size={20} aria-hidden />}
            title="No such incident"
            description="It may have been from another organization, or the link may be wrong."
            action={
              <Link to="/incidents" className="text-[12px] text-accent hover:underline">
                Back to incidents
              </Link>
            }
          />
        ) : (
          <ErrorState
            title="Unable to load this incident"
            description={error?.message}
            onRetry={() => void query.refetch()}
          />
        )}
      </>
    )
  }

  if (query.isPending) return <DetailSkeleton />

  const detail = query.data
  const { incident } = detail
  const durationSeconds =
    (new Date(incident.lastSeenAt).getTime() - new Date(incident.firstSeenAt).getTime()) / 1000

  return (
    <>
      <BackLink />

      <header className="mb-4">
        <div className="mb-1.5 flex flex-wrap items-center gap-2">
          <SeverityBadge severity={incident.severity} />
          <StatusBadge status={incident.status} />
          <Tag>{incident.environment}</Tag>
          <Tag mono>{incident.detectionRule}</Tag>
        </div>

        <h1 className="text-[18px] font-semibold leading-tight text-ink">{incident.title}</h1>

        {/* The metadata line an engineer reads before anything else: which
            service, since when, for how long, and who has it. */}
        <dl className="mt-1.5 flex flex-wrap items-center gap-x-3 gap-y-1 text-[12px] text-ink-muted">
          <Meta label="Service">
            <span className="font-mono">{incident.service}</span>
          </Meta>
          <Meta label="Started">
            <time
              dateTime={incident.firstSeenAt}
              title={new Date(incident.firstSeenAt).toLocaleString()}
            >
              {new Date(incident.firstSeenAt).toLocaleTimeString([], {
                hour: '2-digit',
                minute: '2-digit',
              })}
            </time>
          </Meta>
          <Meta label="Duration">{formatDuration(durationSeconds)}</Meta>
          <Meta label="Events">{formatCount(incident.occurrenceCount)}</Meta>
          <Meta label="Owner">{detail.owner?.displayName ?? 'Unassigned'}</Meta>
        </dl>
      </header>

      <div className="mb-4">
        <IncidentActions
          availableActions={detail.availableActions}
          owner={detail.owner}
          members={members.data ?? []}
          isPending={action.isPending}
          error={action.error}
          onAct={(pending) => action.mutate(pending)}
        />
      </div>

      <div className="grid gap-3 xl:grid-cols-2">
        <div className="space-y-3">
          <Panel title="AI investigation">
            <AiInvestigationPanel
              analysis={detail.analysis}
              isAnalysing={action.isPending}
              canAnalyse={detail.availableActions.includes('analyze')}
              onAnalyse={() => action.mutate({ action: 'analyze' })}
            />
          </Panel>

          {detail.pattern && (
            <Panel title="Error pattern">
              <ErrorPattern pattern={detail.pattern} samples={detail.samples} />
            </Panel>
          )}
        </div>

        <div className="space-y-3">
          <Panel title="Timeline">
            <Timeline entries={detail.timeline} />
          </Panel>

          <Panel title="Related deployment">
            {detail.deployment ? (
              <DeploymentCorrelation deployment={detail.deployment} />
            ) : (
              <p className="text-[12px] text-ink-muted">
                No deployment was correlated with this incident. Either nothing shipped in the window
                before it, or the error predates the last release.
              </p>
            )}
          </Panel>

          <Panel title="Similar incidents">
            <SimilarIncidents incidents={detail.analysis?.similarIncidents ?? []} />
          </Panel>
        </div>
      </div>
    </>
  )
}

function SimilarIncidents({ incidents }: { incidents: SimilarIncident[] }) {
  if (incidents.length === 0) {
    return (
      <p className="text-[12px] text-ink-muted">
        Nothing similar was retrieved. Either this error is new to the system, or too few past
        incidents have been resolved for the search to have anything to match against.
      </p>
    )
  }

  return (
    <ul className="space-y-1.5">
      {incidents.map((similar) => (
        <li key={similar.incidentId}>
          <Link
            to={`/incidents/${similar.incidentId}`}
            className="block rounded-[4px] border border-line bg-raised px-2 py-1.5 transition-quick hover:border-line-strong"
          >
            <div className="flex items-baseline justify-between gap-2">
              <span className="truncate text-[12px] text-ink">{similar.title}</span>
              <span className="tabular shrink-0 text-[11px] text-ink-muted">
                {Math.round(similar.similarity * 100)}%
              </span>
            </div>
            {/* The resolution, not the title, is why a past incident is worth
                surfacing at all. */}
            {similar.resolutionNotes && (
              <p className="mt-0.5 line-clamp-2 text-[11px] leading-snug text-ink-subtle">
                {similar.resolutionNotes}
              </p>
            )}
          </Link>
        </li>
      ))}
    </ul>
  )
}

function BackLink() {
  return (
    <Link
      to="/incidents"
      className="mb-2 inline-flex items-center gap-1 text-[11px] text-ink-muted transition-quick hover:text-accent"
    >
      <ArrowLeft size={12} aria-hidden />
      Incidents
    </Link>
  )
}

function Meta({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex items-baseline gap-1">
      <dt className="text-[10px] uppercase tracking-[0.05em] text-ink-subtle">{label}</dt>
      <dd className="text-ink-muted">{children}</dd>
    </div>
  )
}

function Panel({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="rounded-panel border border-line bg-surface">
      <h2 className="border-b border-line px-3 py-1.5 text-[11px] font-semibold uppercase tracking-[0.06em] text-ink-muted">
        {title}
      </h2>
      <div className="p-3">{children}</div>
    </section>
  )
}

/**
 * Shaped like the real page, so nothing moves when the data lands - the header
 * block, the action row, then two columns.
 */
function DetailSkeleton() {
  return (
    <>
      <BackLink />

      <div className="mb-4" role="status" aria-label="Loading incident">
        <div className="mb-2 flex gap-2">
          <Skeleton className="h-4 w-20" />
          <Skeleton className="h-4 w-24" />
          <Skeleton className="h-4 w-16" />
        </div>
        <Skeleton className="mb-2 h-5 w-2/3" />
        <Skeleton className="h-3 w-1/2" />
      </div>

      <div className="mb-4 flex gap-2">
        <Skeleton className="h-7 w-28" />
        <Skeleton className="h-7 w-24" />
        <Skeleton className="h-7 w-20" />
      </div>

      <div className="grid gap-3 xl:grid-cols-2">
        <div className="space-y-3">
          <PanelSkeleton icon={<FileSearch size={13} aria-hidden />} lines={7} />
          <PanelSkeleton lines={4} />
        </div>
        <div className="space-y-3">
          <PanelSkeleton lines={6} />
          <PanelSkeleton icon={<Rocket size={13} aria-hidden />} lines={3} />
        </div>
      </div>
    </>
  )
}

function PanelSkeleton({ lines, icon }: { lines: number; icon?: ReactNode }) {
  return (
    <section className="rounded-panel border border-line bg-surface">
      <div className="flex items-center gap-1.5 border-b border-line px-3 py-1.5 text-ink-subtle">
        {icon}
        <Skeleton className="h-2.5 w-24" />
      </div>
      <div className="space-y-2 p-3">
        {Array.from({ length: lines }).map((_, index) => (
          <Skeleton
            key={index}
            className="h-2.5"
            style={{
              width: `${100 - (index % 3) * 18}%`,
              animationDelay: `${index * 40}ms`,
            }}
          />
        ))}
      </div>
    </section>
  )
}
