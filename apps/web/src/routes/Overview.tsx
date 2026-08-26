import { Link } from 'react-router'

import { ENVIRONMENTS, TIME_RANGES, useSession } from '../app/session'
import { PageHeader } from '../components/ui/PageHeader'
import { Skeleton, SkeletonTable } from '../components/ui/Skeleton'
import { ErrorState } from '../components/ui/States'
import { ActiveIncidentsTable } from '../features/overview/ActiveIncidentsTable'
import { HealthTimeline } from '../features/overview/HealthTimeline'
import { MetricCard } from '../features/overview/MetricCard'
import { ServiceHealthTable } from '../features/overview/ServiceHealthTable'
import type { ApiError } from '../lib/api/client'
import { useActiveIncidents, useOverview, useServiceHealth } from '../lib/api/queries'
import { formatCount, formatDuration } from '../lib/format'

export default function OverviewPage() {
  const { environment, timeRange } = useSession()

  // Three independent queries rather than one. Each section renders as soon as
  // its own data lands, so a slow aggregation does not hold up the incident
  // table - the part someone is actually waiting for.
  const overview = useOverview(environment, timeRange)
  const services = useServiceHealth(environment, timeRange)
  const incidents = useActiveIncidents(environment)

  const environmentLabel =
    ENVIRONMENTS.find((option) => option.key === environment)?.label ?? environment
  const rangeLabel = TIME_RANGES.find((option) => option.key === timeRange)?.label ?? timeRange
  // "Last 24 hours" -> "last 24 hours", so it reads inside a sentence.
  const inlineRange = rangeLabel.replace('Last ', 'last ')

  const data = overview.data

  return (
    <>
      <PageHeader
        title={`${environmentLabel} Overview`}
        description={
          data
            ? `${rangeLabel} · ${data.totalServices} service${data.totalServices === 1 ? '' : 's'} monitored`
            : rangeLabel
        }
      />

      {overview.isError ? (
        <ErrorState
          title="Unable to load the overview"
          description={(overview.error as ApiError)?.message ?? 'The Incident API could not be reached.'}
          onRetry={() => void overview.refetch()}
        />
      ) : (
        <section aria-label="Key metrics" className="mb-3 grid gap-2.5 sm:grid-cols-2 xl:grid-cols-5">
          <MetricCard
            label="Active incidents"
            metric={data?.activeIncidents}
            loading={overview.isPending}
            accentClassName="text-sev-critical"
            context={
              data ? `${data.servicesAffected.value} of ${data.totalServices} services` : undefined
            }
          />
          <MetricCard
            label="Error events"
            metric={data?.errorEvents}
            loading={overview.isPending}
            format={formatCount}
            accentClassName="text-sev-high"
            context={`in the ${inlineRange}`}
          />
          <MetricCard
            label="Services affected"
            metric={data?.servicesAffected}
            loading={overview.isPending}
            accentClassName="text-sev-medium"
            context={data ? `of ${data.totalServices} monitored` : undefined}
          />
          <MetricCard
            label="Mean time to resolve"
            metric={data?.meanTimeToResolutionMinutes}
            loading={overview.isPending}
            format={(value) => (value > 0 ? formatDuration(value * 60) : '—')}
            accentClassName="text-state-ok"
            context="resolved in window"
          />
          <MetricCard
            label="AI investigations"
            metric={data?.aiInvestigations}
            loading={overview.isPending}
            // More completed analyses is good, so a rise is not a warning here.
            higherIsWorse={false}
            accentClassName="text-accent"
            context="completed"
          />
        </section>
      )}

      <section aria-label="System health" className="mb-4">
        {overview.isPending ? (
          <div className="rounded-panel border border-line bg-surface p-3">
            <Skeleton className="mb-3 h-2.5 w-28" />
            <Skeleton className="h-52 w-full" />
          </div>
        ) : overview.isError ? null : (
          <HealthTimeline
            points={data?.timeline ?? []}
            markers={data?.markers ?? []}
            bucketMinutes={data?.bucketMinutes ?? 15}
          />
        )}
      </section>

      <section aria-label="Active incidents" className="mb-4">
        <div className="mb-2 flex items-center justify-between">
          <h2 className="text-[11px] font-semibold uppercase tracking-[0.06em] text-ink-muted">
            Active incidents
          </h2>
          <Link
            to="/incidents"
            className="text-[11px] text-ink-subtle transition-quick hover:text-accent"
          >
            View all →
          </Link>
        </div>

        {incidents.isPending ? (
          <div className="rounded-panel border border-line bg-surface">
            <SkeletonTable rows={4} columns={7} />
          </div>
        ) : incidents.isError ? (
          <ErrorState
            title="Unable to load incidents"
            description={(incidents.error as ApiError)?.message}
            onRetry={() => void incidents.refetch()}
          />
        ) : (
          <ActiveIncidentsTable incidents={incidents.data?.items ?? []} />
        )}
      </section>

      <section aria-label="Service health">
        <h2 className="mb-2 text-[11px] font-semibold uppercase tracking-[0.06em] text-ink-muted">
          Service health
        </h2>

        {services.isPending ? (
          <div className="rounded-panel border border-line bg-surface">
            <SkeletonTable rows={4} columns={5} />
          </div>
        ) : services.isError ? (
          <ErrorState
            title="Unable to load service health"
            description={(services.error as ApiError)?.message}
            onRetry={() => void services.refetch()}
          />
        ) : (
          <ServiceHealthTable services={services.data ?? []} />
        )}
      </section>
    </>
  )
}
