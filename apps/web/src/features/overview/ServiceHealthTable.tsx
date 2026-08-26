import { ArrowDown, ArrowUp } from 'lucide-react'
import { useMemo, useState } from 'react'
import { Link } from 'react-router'

import { HealthDot } from '../../components/ui/Badge'
import { cn } from '../../lib/cn'
import { formatCount, formatRelative } from '../../lib/format'
import type { ServiceHealth } from '../../types/api'

type SortKey = 'key' | 'health' | 'activeIncidents' | 'errorEvents' | 'distinctErrorPatterns'

const HEALTH_RANK: Record<string, number> = { Critical: 0, Degraded: 1, Unknown: 2, Healthy: 3 }

const COLUMNS: { key: SortKey; label: string; numeric?: boolean }[] = [
  { key: 'key', label: 'Service' },
  { key: 'health', label: 'Health' },
  { key: 'activeIncidents', label: 'Active incidents', numeric: true },
  { key: 'errorEvents', label: 'Error events', numeric: true },
  { key: 'distinctErrorPatterns', label: 'Error patterns', numeric: true },
]

export function ServiceHealthTable({ services }: { services: ServiceHealth[] }) {
  const [sortKey, setSortKey] = useState<SortKey>('health')
  const [ascending, setAscending] = useState(true)

  const sorted = useMemo(() => {
    const rows = [...services]

    rows.sort((a, b) => {
      // Health sorts by severity, not alphabetically - "Critical" before
      // "Degraded" before "Healthy" is the only order anyone wants.
      const left = sortKey === 'health' ? HEALTH_RANK[a.health] ?? 9 : a[sortKey]
      const right = sortKey === 'health' ? HEALTH_RANK[b.health] ?? 9 : b[sortKey]

      if (typeof left === 'string' && typeof right === 'string') {
        return ascending ? left.localeCompare(right) : right.localeCompare(left)
      }

      return ascending ? Number(left) - Number(right) : Number(right) - Number(left)
    })

    return rows
  }, [services, sortKey, ascending])

  const toggle = (key: SortKey) => {
    if (key === sortKey) {
      setAscending((value) => !value)
      return
    }
    setSortKey(key)
    // Numeric columns are most useful highest-first; names read A-Z.
    setAscending(key === 'key' || key === 'health')
  }

  return (
    <div className="overflow-x-auto rounded-panel border border-line bg-surface">
      <table className="w-full min-w-[640px] border-collapse text-[12px]">
        <caption className="sr-only">Service health, sortable</caption>
        <thead>
          <tr className="border-b border-line text-left text-[10px] uppercase tracking-[0.05em] text-ink-subtle">
            {COLUMNS.map((column) => (
              <th
                key={column.key}
                scope="col"
                className={cn('px-3 py-1.5 font-medium', column.numeric && 'text-right')}
                aria-sort={
                  sortKey === column.key ? (ascending ? 'ascending' : 'descending') : 'none'
                }
              >
                <button
                  type="button"
                  onClick={() => toggle(column.key)}
                  className={cn(
                    'inline-flex items-center gap-1 uppercase tracking-[0.05em] transition-quick hover:text-ink',
                    sortKey === column.key && 'text-ink',
                  )}
                >
                  {column.label}
                  {sortKey === column.key &&
                    (ascending ? (
                      <ArrowUp size={10} aria-hidden />
                    ) : (
                      <ArrowDown size={10} aria-hidden />
                    ))}
                </button>
              </th>
            ))}
            <th scope="col" className="px-3 py-1.5 text-right font-medium">Last incident</th>
          </tr>
        </thead>

        <tbody className="divide-y divide-line">
          {sorted.map((service) => (
            <tr key={service.key} className="transition-quick hover:bg-raised">
              <td className="px-3 py-1.5">
                <Link
                  to={`/services/${service.key}`}
                  className="font-mono text-ink hover:text-accent hover:underline"
                >
                  {service.key}
                </Link>
                {service.ownerTeam && (
                  <span className="ml-2 text-[10px] text-ink-subtle">{service.ownerTeam}</span>
                )}
              </td>
              <td className="whitespace-nowrap px-3 py-1.5">
                <HealthDot health={service.health} />
              </td>
              <td className="px-3 py-1.5 text-right tabular">
                <span className={service.activeIncidents > 0 ? 'text-ink' : 'text-ink-subtle'}>
                  {service.activeIncidents}
                </span>
              </td>
              <td className="px-3 py-1.5 text-right tabular text-ink-muted">
                {formatCount(service.errorEvents)}
              </td>
              <td className="px-3 py-1.5 text-right tabular text-ink-muted">
                {service.distinctErrorPatterns}
              </td>
              <td className="whitespace-nowrap px-3 py-1.5 text-right text-ink-subtle">
                {service.lastIncidentAt ? formatRelative(service.lastIncidentAt) : '—'}
              </td>
            </tr>
          ))}
        </tbody>
      </table>

      <p className="border-t border-line px-3 py-1.5 text-[10px] text-ink-subtle">
        Request rate and latency need a metrics pipeline; IncidentIQ ingests logs only.
      </p>
    </div>
  )
}
