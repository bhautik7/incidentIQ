import { ArrowDown, ArrowUp, Minus } from 'lucide-react'

import { cn } from '../../lib/cn'
import { Skeleton } from '../../components/ui/Skeleton'
import type { MetricSummary } from '../../types/api'
import { Sparkline } from './Sparkline'

/**
 * A KPI tile.
 *
 * "7 active incidents" is not information on its own - it is reassuring after
 * 20 and alarming after 2. The delta supplies that context, and the sparkline
 * says whether it is still moving.
 */
export function MetricCard({
  label,
  metric,
  format = (value) => String(Math.round(value)),
  context,
  /** True when a rise is bad. Error counts rise badly; resolutions rise well. */
  higherIsWorse = true,
  accentClassName = 'text-ink-subtle',
  loading,
}: {
  label: string
  metric?: MetricSummary
  format?: (value: number) => string
  context?: string
  higherIsWorse?: boolean
  accentClassName?: string
  loading?: boolean
}) {
  if (loading || !metric) {
    return (
      <div className="rounded-panel border border-line bg-surface p-3">
        <Skeleton className="mb-2.5 h-2 w-24" />
        <Skeleton className="mb-2 h-7 w-14" />
        <Skeleton className="h-2 w-20" />
      </div>
    )
  }

  const change = metric.changePercent
  const rising = change !== null && change > 0
  const flat = change === null || Math.abs(change) < 0.5

  // Direction and sentiment are different things: fewer incidents is good,
  // fewer resolutions is not.
  const sentiment = flat ? 'flat' : rising === higherIsWorse ? 'bad' : 'good'
  const DeltaIcon = flat ? Minus : rising ? ArrowUp : ArrowDown

  return (
    <div className="rounded-panel border border-line bg-surface p-3">
      <p className="mb-1.5 text-[10px] font-semibold uppercase tracking-[0.06em] text-ink-subtle">
        {label}
      </p>

      <div className="flex items-end justify-between gap-2">
        <span className="text-[26px] font-semibold leading-none tabular text-ink">
          {format(metric.value)}
        </span>
        {/* Omitted rather than drawn flat when there is no series. A flat line
            reads as "this metric is steady", which is a different claim from
            "this metric has no meaningful trend". */}
        {metric.series.length > 1 && <Sparkline values={metric.series} className={accentClassName} />}
      </div>

      <div className="mt-2 flex items-center gap-1 text-[11px]">
        {change === null ? (
          // No previous window to compare against. Saying "+100%" here would be
          // a lie dressed as precision.
          <span className="text-ink-subtle">{context ?? 'no prior data'}</span>
        ) : (
          <>
            <DeltaIcon
              size={11}
              aria-hidden
              className={cn(
                sentiment === 'bad' && 'text-sev-critical',
                sentiment === 'good' && 'text-state-ok',
                sentiment === 'flat' && 'text-ink-subtle',
              )}
            />
            <span
              className={cn(
                'tabular',
                sentiment === 'bad' && 'text-sev-critical',
                sentiment === 'good' && 'text-state-ok',
                sentiment === 'flat' && 'text-ink-subtle',
              )}
            >
              {flat ? 'no change' : `${Math.abs(change).toFixed(0)}%`}
            </span>
            {context && <span className="truncate text-ink-subtle">· {context}</span>}
          </>
        )}
      </div>
    </div>
  )
}
