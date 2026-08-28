import { Filter, ScrollText } from 'lucide-react'
import { useCallback, useMemo } from 'react'

import { ENVIRONMENTS, TIME_RANGES, useSession } from '../app/session'
import { Button } from '../components/ui/Button'
import { FilterBar, FilterSummary, SearchInput } from '../components/ui/FilterBar'
import { PageHeader } from '../components/ui/PageHeader'
import { Select } from '../components/ui/Select'
import { EmptyState, ErrorState } from '../components/ui/States'
import { LogTable } from '../features/logs/LogTable'
import type { ApiError } from '../lib/api/client'
import { useLogs, useServices, windowMinutesFor } from '../lib/api/queries'
import { useQueryParams } from '../lib/useQueryParams'

/**
 * Filter defaults, which double as the definition of "unfiltered" - these are
 * the values that never appear in the URL, so a pasted link carries only what
 * was deliberately set.
 */
const DEFAULTS = {
  q: '',
  service: '',
  level: '',
  trace: '',
  fingerprint: '',
}

const LEVEL_OPTIONS = [
  { value: '', label: 'Any level' },
  { value: 'Warning', label: 'Warning and above' },
  { value: 'Error', label: 'Error and above' },
  { value: 'Fatal', label: 'Fatal only' },
]

export default function LogsPage() {
  const { environment, timeRange } = useSession()
  const [params, setParams] = useQueryParams(DEFAULTS)

  const services = useServices()

  const query = useLogs(environment, {
    service: params.service,
    level: params.level,
    search: params.q,
    traceId: params.trace,
    fingerprint: params.fingerprint,
    windowMinutes: windowMinutesFor(timeRange),
  })

  const rows = useMemo(
    () => query.data?.pages.flatMap((page) => page.page.items) ?? [],
    [query.data],
  )

  const window = query.data?.pages[0]?.window
  const environmentLabel =
    ENVIRONMENTS.find((option) => option.key === environment)?.label ?? environment
  const rangeLabel = TIME_RANGES.find((option) => option.key === timeRange)?.label ?? timeRange

  const setFilter = useCallback(
    (patch: Partial<typeof DEFAULTS>, options?: { replace?: boolean }) =>
      setParams(patch, options),
    [setParams],
  )

  /** Clicking a value inside a row is how one line becomes every line like it. */
  const filterBy = useCallback(
    (field: 'service' | 'level' | 'traceId' | 'fingerprint', value: string) => {
      if (field === 'traceId') setFilter({ trace: value })
      else if (field === 'fingerprint') setFilter({ fingerprint: value })
      else setFilter({ [field]: value })
    },
    [setFilter],
  )

  const activeFilters = useMemo(() => {
    const chips: { label: string; value: string; onRemove: () => void }[] = []

    if (params.q) chips.push({ label: 'search', value: params.q, onRemove: () => setFilter({ q: '' }) })
    if (params.service) {
      chips.push({ label: 'service', value: params.service, onRemove: () => setFilter({ service: '' }) })
    }
    if (params.level) {
      chips.push({ label: 'level', value: params.level, onRemove: () => setFilter({ level: '' }) })
    }
    if (params.trace) {
      chips.push({ label: 'trace', value: params.trace, onRemove: () => setFilter({ trace: '' }) })
    }
    if (params.fingerprint) {
      chips.push({
        label: 'fingerprint',
        value: `${params.fingerprint.slice(0, 12)}…`,
        onRemove: () => setFilter({ fingerprint: '' }),
      })
    }

    return chips
  }, [params, setFilter])

  return (
    <>
      <PageHeader
        title="Log Explorer"
        description={
          // The window is stated up front, because an explorer that silently
          // holds two days of data reads as "nothing happened" when someone
          // looks for last week.
          window
            ? `${environmentLabel} · ${rangeLabel.toLowerCase()} · ${window.retentionHours}h retained`
            : `${environmentLabel} · ${rangeLabel.toLowerCase()}`
        }
        actions={
          <Button disabled title="Live tail arrives with the SignalR hub">
            ● Live tail
          </Button>
        }
      />

      <FilterBar className="mb-2">
        <SearchInput
          label="Search log messages"
          placeholder="Search messages…"
          value={params.q}
          onChange={(value) => setFilter({ q: value }, { replace: true })}
        />

        <Select
          label="Level"
          value={params.level}
          onChange={(event) => setFilter({ level: event.target.value })}
        >
          {LEVEL_OPTIONS.map((option) => (
            <option key={option.value} value={option.value}>
              {option.label}
            </option>
          ))}
        </Select>

        <Select
          label="Service"
          value={params.service}
          onChange={(event) => setFilter({ service: event.target.value })}
        >
          <option value="">Any service</option>
          {(services.data ?? []).map((service) => (
            <option key={service.key} value={service.key}>
              {service.displayName}
            </option>
          ))}
        </Select>

        <SearchInput
          label="Filter by trace ID"
          placeholder="trace:"
          value={params.trace}
          onChange={(value) => setFilter({ trace: value }, { replace: true })}
        />
      </FilterBar>

      <div className="mb-2 flex flex-wrap items-center justify-between gap-2">
        <FilterSummary
          filtered={rows.length}
          total={null}
          noun="line"
          activeFilters={activeFilters}
          onClear={() => setFilter(DEFAULTS)}
        />

        {window?.oldestAvailableAt && (
          <p className="px-0.5 text-[11px] text-ink-subtle">
            Oldest retained: {new Date(window.oldestAvailableAt).toLocaleString()}
          </p>
        )}
      </div>

      {query.isError ? (
        <ErrorState
          title="Unable to load logs"
          description={(query.error as ApiError)?.message}
          onRetry={() => void query.refetch()}
        />
      ) : (
        <LogTable
          rows={rows}
          isLoading={query.isPending}
          hasNextPage={query.hasNextPage}
          isFetchingNextPage={query.isFetchingNextPage}
          onLoadMore={() => void query.fetchNextPage()}
          onFilterBy={filterBy}
          emptyState={
            activeFilters.length > 0 ? (
              <EmptyState
                icon={<Filter size={20} aria-hidden />}
                title="No lines match these filters"
                description="Widen a filter, or clear them to see everything in the window."
                action={
                  <Button variant="primary" onClick={() => setFilter(DEFAULTS)}>
                    Clear all filters
                  </Button>
                }
              />
            ) : (
              <EmptyState
                icon={<ScrollText size={20} aria-hidden />}
                title="No logs in this window"
                description={`Nothing was logged in ${environmentLabel} during the ${rangeLabel.toLowerCase()}. Logs are retained for ${window?.retentionHours ?? 48} hours; anything older is summarised as patterns and incidents.`}
              />
            )
          }
        />
      )}
    </>
  )
}
