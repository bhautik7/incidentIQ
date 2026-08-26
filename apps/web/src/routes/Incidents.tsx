import { Filter, ShieldCheck } from 'lucide-react'
import { useCallback, useMemo, useState } from 'react'
import { useNavigate } from 'react-router'

import { ENVIRONMENTS, useSession } from '../app/session'
import { Button } from '../components/ui/Button'
import { DataTable, TablePagination, type SortState } from '../components/ui/DataTable'
import { FilterBar, FilterSummary, SearchInput } from '../components/ui/FilterBar'
import { PageHeader } from '../components/ui/PageHeader'
import { Select } from '../components/ui/Select'
import { EmptyState } from '../components/ui/States'
import { incidentColumns } from '../features/incidents/columns'
import type { ApiError } from '../lib/api/client'
import { useIncidents, useServices } from '../lib/api/queries'
import { useQueryParams } from '../lib/useQueryParams'

const PAGE_SIZE = 25

/**
 * Filter defaults.
 *
 * These are the values that do *not* appear in the URL, so they double as the
 * definition of "unfiltered". Status defaults to active because the queue's
 * standing question is what is broken now, not what ever broke.
 */
const DEFAULTS = {
  q: '',
  status: 'active',
  severity: '',
  service: '',
  sort: 'lastSeen',
  dir: 'desc',
  page: '1',
}

const STATUS_OPTIONS = [
  { value: 'active', label: 'Active' },
  { value: 'all', label: 'Any status' },
  { value: 'Detected', label: 'Detected' },
  { value: 'Investigating', label: 'Investigating' },
  { value: 'Resolved', label: 'Resolved' },
  { value: 'Ignored', label: 'Ignored' },
]

const SEVERITY_OPTIONS = [
  { value: '', label: 'Any severity' },
  { value: 'Critical', label: 'Critical' },
  { value: 'High', label: 'High' },
  { value: 'Medium', label: 'Medium' },
  { value: 'Low', label: 'Low' },
]

export default function IncidentsPage() {
  const { environment } = useSession()
  const navigate = useNavigate()

  const [params, setParams] = useQueryParams(DEFAULTS)
  const [selected, setSelected] = useState<Set<string>>(new Set())

  const services = useServices()

  const page = Math.max(1, Number.parseInt(params.page, 10) || 1)

  const incidents = useIncidents(environment, {
    status: params.status,
    severity: params.severity,
    service: params.service,
    search: params.q,
    sort: params.sort,
    direction: params.dir,
    page,
    pageSize: PAGE_SIZE,
  })

  /**
   * Any filter change returns to page one.
   *
   * Staying on page 4 after narrowing to three results shows an empty table
   * that reads as "nothing matched" when in fact everything did.
   */
  const setFilter = useCallback(
    (patch: Partial<typeof DEFAULTS>, options?: { replace?: boolean }) => {
      setParams({ ...patch, page: '1' }, options)
      // The selection referred to rows that are about to be replaced.
      setSelected(new Set())
    },
    [setParams],
  )

  const sort = useMemo<SortState>(
    () => ({ key: params.sort, direction: params.dir === 'asc' ? 'asc' : 'desc' }),
    [params.sort, params.dir],
  )

  const activeFilters = useMemo(() => {
    const chips: { label: string; value: string; onRemove: () => void }[] = []

    if (params.q) {
      chips.push({ label: 'search', value: params.q, onRemove: () => setFilter({ q: '' }) })
    }
    if (params.status !== DEFAULTS.status) {
      chips.push({
        label: 'status',
        value: STATUS_OPTIONS.find((option) => option.value === params.status)?.label ?? params.status,
        onRemove: () => setFilter({ status: DEFAULTS.status }),
      })
    }
    if (params.severity) {
      chips.push({
        label: 'severity',
        value: params.severity,
        onRemove: () => setFilter({ severity: '' }),
      })
    }
    if (params.service) {
      chips.push({
        label: 'service',
        value: params.service,
        onRemove: () => setFilter({ service: '' }),
      })
    }

    return chips
  }, [params, setFilter])

  const rows = incidents.data?.items ?? []
  const totalCount = incidents.data?.totalCount ?? 0
  const environmentLabel =
    ENVIRONMENTS.find((option) => option.key === environment)?.label ?? environment

  return (
    <>
      <PageHeader
        title="Incidents"
        description={`${environmentLabel} · every detected incident, filterable and linkable`}
        actions={
          // Neither endpoint exists yet. Disabled with a reason says the product
          // intends them; wiring them to nothing would be worse than omitting
          // them entirely.
          <>
            <Button disabled title="Export arrives with the reporting endpoints">
              Export
            </Button>
            <Button disabled title="Alert rules arrive in a later phase">
              + Alert rule
            </Button>
          </>
        }
      />

      <FilterBar className="mb-2">
        <SearchInput
          label="Search incident titles"
          placeholder="Search titles…"
          value={params.q}
          // Replace rather than push: one history entry per keystroke would
          // bury the page the user arrived from.
          onChange={(value) => setFilter({ q: value }, { replace: true })}
        />

        <Select
          label="Severity"
          value={params.severity}
          onChange={(event) => setFilter({ severity: event.target.value })}
        >
          {SEVERITY_OPTIONS.map((option) => (
            <option key={option.value} value={option.value}>
              {option.label}
            </option>
          ))}
        </Select>

        <Select
          label="Status"
          value={params.status}
          onChange={(event) => setFilter({ status: event.target.value })}
        >
          {STATUS_OPTIONS.map((option) => (
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
      </FilterBar>

      <div className="mb-2 flex flex-wrap items-center justify-between gap-2">
        <FilterSummary
          filtered={totalCount}
          // The unfiltered total would cost a second request for a number that
          // adds little beside the chips, so it is left unstated rather than
          // guessed at.
          total={null}
          noun="incident"
          activeFilters={activeFilters}
          onClear={() => setFilter(DEFAULTS)}
        />

        {selected.size > 0 && (
          <SelectionActions count={selected.size} onClear={() => setSelected(new Set())} />
        )}
      </div>

      <DataTable
        rows={rows}
        columns={incidentColumns}
        getRowId={(incident) => incident.id}
        caption="Incidents, filterable and sortable"
        // The title is the column people read; below this width it truncates
        // to the point of being unusable, so the table scrolls inside its
        // panel rather than squeezing it further.
        minWidthClassName="min-w-[1080px]"
        sort={sort}
        onSortChange={(next) => setFilter({ sort: next.key, dir: next.direction })}
        selectedIds={selected}
        onSelectionChange={setSelected}
        onRowActivate={(incident) => navigate(`/incidents/${incident.id}`)}
        isLoading={incidents.isPending}
        isPaused={incidents.isPaused}
        error={incidents.isError ? incidents.error : undefined}
        errorTitle="Unable to load incidents"
        errorDescription={(incidents.error as ApiError)?.message}
        onRetry={() => void incidents.refetch()}
        skeletonRows={12}
        emptyState={
          activeFilters.length > 0 ? (
            <EmptyState
              icon={<Filter size={20} aria-hidden />}
              title="No incidents match these filters"
              description="The filters are narrower than the data. Widen one, or clear them to see the whole queue."
              action={
                <Button variant="primary" onClick={() => setFilter(DEFAULTS)}>
                  Clear all filters
                </Button>
              }
            />
          ) : (
            <EmptyState
              icon={<ShieldCheck size={20} className="text-state-ok" aria-hidden />}
              title="No active incidents"
              description={`Every monitored service in ${environmentLabel} is currently healthy. Detected incidents appear here as the rules open them.`}
            />
          )
        }
      />

      <TablePagination
        page={page}
        pageSize={PAGE_SIZE}
        totalCount={totalCount}
        onPageChange={(next) => {
          setParams({ page: String(next) })
          setSelected(new Set())
        }}
        disabled={incidents.isPending}
      />
    </>
  )
}

/**
 * What is selected, and what can be done with it.
 *
 * The lifecycle endpoints do not exist yet - IncidentLifecycleService lives in
 * the event processor and is not exposed over HTTP - so the actions are
 * disabled and say why. Selection still earns its place: it is how someone
 * holds and counts a set of rows while reading them.
 */
function SelectionActions({ count, onClear }: { count: number; onClear: () => void }) {
  return (
    <div className="flex items-center gap-2 text-[11px]" role="status">
      <span className="tabular text-ink-muted">{count} selected</span>

      <Button size="sm" disabled title="Acknowledging arrives with the incident lifecycle endpoints">
        Acknowledge
      </Button>
      <Button size="sm" disabled title="Resolving arrives with the incident lifecycle endpoints">
        Resolve
      </Button>
      <Button size="sm" variant="ghost" onClick={onClear}>
        Clear
      </Button>
    </div>
  )
}
