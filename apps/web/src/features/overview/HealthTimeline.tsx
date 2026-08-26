import { useMemo } from 'react'
import { useNavigate } from 'react-router'
import {
  Area,
  CartesianGrid,
  ComposedChart,
  ReferenceLine,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from 'recharts'

import { cn } from '../../lib/cn'
import { formatCount } from '../../lib/format'
import type { TimelineMarker, TimelinePoint } from '../../types/api'

type ChartPoint = TimelinePoint & { t: number }

const SEVERITY_COLOUR: Record<string, string> = {
  Critical: 'var(--color-sev-critical)',
  High: 'var(--color-sev-high)',
  Medium: 'var(--color-sev-medium)',
  Low: 'var(--color-sev-low)',
}

/**
 * The system health chart.
 *
 * Two series and two kinds of marker, because the question this answers is
 * "did something change, and did we ship anything near it?" - and that
 * question is unanswerable if deployments live on a different chart.
 *
 * The axis is a numeric timestamp rather than a formatted string so that
 * markers can be positioned at their real time, not snapped to the nearest
 * bucket. A deployment two minutes before a spike is the whole point; rounding
 * it into the same bucket destroys the evidence.
 */
export function HealthTimeline({
  points,
  markers,
  bucketMinutes,
}: {
  points: TimelinePoint[]
  markers: TimelineMarker[]
  bucketMinutes: number
}) {
  const navigate = useNavigate()

  const data = useMemo<ChartPoint[]>(
    () => points.map((point) => ({ ...point, t: new Date(point.bucketStart).getTime() })),
    [points],
  )

  const { deployments, incidents } = useMemo(
    () => ({
      deployments: markers.filter((marker) => marker.kind === 'deployment'),
      incidents: markers.filter((marker) => marker.kind === 'incident'),
    }),
    [markers],
  )

  const spansDays = data.length > 1 && data[data.length - 1].t - data[0].t > 36 * 3600_000

  const formatTick = (value: number) =>
    new Date(value).toLocaleTimeString([], {
      hour: '2-digit',
      minute: '2-digit',
      ...(spansDays ? { day: 'numeric', month: 'short' } : {}),
    })

  return (
    <div className="rounded-panel border border-line bg-surface">
      <div className="flex flex-wrap items-center justify-between gap-2 border-b border-line px-3 py-2">
        <h2 className="text-[11px] font-semibold uppercase tracking-[0.06em] text-ink-muted">
          System health
        </h2>

        <div className="flex flex-wrap items-center gap-3 text-[10px] text-ink-subtle">
          <Legend colour="var(--color-sev-critical)" label="Errors" />
          <Legend colour="var(--color-sev-medium)" label="Warnings" />
          <Legend colour="var(--color-accent)" label="Deployment" dashed />
          <Legend colour="var(--color-sev-critical)" label="Incident" dashed />
          <span className="text-ink-subtle">· {bucketMinutes}m buckets</span>
        </div>
      </div>

      <div className="h-56 px-1 py-2">
        <ResponsiveContainer width="100%" height="100%">
          <ComposedChart data={data} margin={{ top: 8, right: 12, bottom: 4, left: 4 }}>
            <defs>
              <linearGradient id="errorFill" x1="0" y1="0" x2="0" y2="1">
                <stop offset="0%" stopColor="var(--color-sev-critical)" stopOpacity={0.28} />
                <stop offset="100%" stopColor="var(--color-sev-critical)" stopOpacity={0.02} />
              </linearGradient>
              <linearGradient id="warnFill" x1="0" y1="0" x2="0" y2="1">
                <stop offset="0%" stopColor="var(--color-sev-medium)" stopOpacity={0.18} />
                <stop offset="100%" stopColor="var(--color-sev-medium)" stopOpacity={0.02} />
              </linearGradient>
            </defs>

            {/* Horizontal only. Vertical grid lines would compete with the
                deployment and incident markers, which carry real meaning. */}
            <CartesianGrid stroke="var(--color-line)" strokeDasharray="2 4" vertical={false} />

            <XAxis
              dataKey="t"
              type="number"
              scale="time"
              domain={['dataMin', 'dataMax']}
              tickFormatter={formatTick}
              tick={{ fill: 'var(--color-ink-subtle)', fontSize: 10 }}
              axisLine={{ stroke: 'var(--color-line)' }}
              tickLine={false}
              minTickGap={44}
            />
            <YAxis
              tick={{ fill: 'var(--color-ink-subtle)', fontSize: 10 }}
              axisLine={false}
              tickLine={false}
              width={38}
              tickFormatter={(value: number) => formatCount(value)}
              allowDecimals={false}
            />

            <Tooltip
              cursor={{ stroke: 'var(--color-line-strong)', strokeWidth: 1 }}
              content={<TimelineTooltip markers={markers} bucketMinutes={bucketMinutes} />}
            />

            <Area
              type="monotone"
              dataKey="warningEvents"
              stroke="var(--color-sev-medium)"
              strokeWidth={1}
              fill="url(#warnFill)"
              isAnimationActive={false}
            />
            <Area
              type="monotone"
              dataKey="errorEvents"
              stroke="var(--color-sev-critical)"
              strokeWidth={1.5}
              fill="url(#errorFill)"
              isAnimationActive={false}
            />

            {deployments.map((marker) => (
              <ReferenceLine
                key={`deploy-${marker.at}-${marker.label}`}
                x={new Date(marker.at).getTime()}
                stroke="var(--color-accent)"
                strokeDasharray="3 3"
                strokeWidth={1}
                label={{
                  value: `▲ ${marker.label}`,
                  position: 'insideTopLeft',
                  fill: 'var(--color-accent)',
                  fontSize: 9,
                }}
              />
            ))}

            {incidents.map((marker) => (
              <ReferenceLine
                key={`incident-${marker.at}-${marker.incidentId}`}
                x={new Date(marker.at).getTime()}
                stroke={SEVERITY_COLOUR[marker.severity ?? 'Low']}
                strokeDasharray="2 2"
                strokeWidth={1}
                label={{
                  value: '▼',
                  position: 'top',
                  fill: SEVERITY_COLOUR[marker.severity ?? 'Low'],
                  fontSize: 9,
                }}
              />
            ))}
          </ComposedChart>
        </ResponsiveContainer>
      </div>

      {incidents.length > 0 && (
        <div className="flex flex-wrap gap-1.5 border-t border-line px-3 py-2">
          {/* The markers are also listed as buttons: a 1px reference line is
              not a usable click target, and is unreachable by keyboard. */}
          {incidents.slice(0, 6).map((marker) => (
            <button
              key={marker.incidentId}
              type="button"
              onClick={() => marker.incidentId && navigate(`/incidents/${marker.incidentId}`)}
              className={cn(
                'flex max-w-[220px] items-center gap-1.5 rounded-[4px] border border-line',
                'bg-raised px-1.5 py-0.5 text-[10px] text-ink-muted transition-quick',
                'hover:border-line-strong hover:text-ink',
              )}
            >
              <span
                aria-hidden
                className="h-1.5 w-1.5 shrink-0 rounded-full"
                style={{ background: SEVERITY_COLOUR[marker.severity ?? 'Low'] }}
              />
              <span className="truncate">{marker.label}</span>
              <span className="shrink-0 font-mono text-ink-subtle">
                {new Date(marker.at).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
              </span>
            </button>
          ))}
        </div>
      )}
    </div>
  )
}

function Legend({ colour, label, dashed }: { colour: string; label: string; dashed?: boolean }) {
  return (
    <span className="flex items-center gap-1">
      <span
        aria-hidden
        className={cn('h-px w-3', dashed && 'border-t border-dashed')}
        style={dashed ? { borderColor: colour } : { background: colour }}
      />
      {label}
    </span>
  )
}

type TooltipProps = {
  active?: boolean
  payload?: { payload: ChartPoint }[]
  markers: TimelineMarker[]
  bucketMinutes: number
}

/**
 * Custom tooltip.
 *
 * Recharts' default shows series values only. The useful question at a point
 * in time is "what else happened here?", so any deployment or incident inside
 * the same bucket is folded in.
 */
function TimelineTooltip({ active, payload, markers, bucketMinutes }: TooltipProps) {
  if (!active || !payload?.length) return null

  const point = payload[0].payload
  const bucketEnd = point.t + bucketMinutes * 60_000
  const inBucket = markers.filter((marker) => {
    const at = new Date(marker.at).getTime()
    return at >= point.t && at < bucketEnd
  })

  return (
    <div className="rounded-[4px] border border-line-strong bg-overlay px-2.5 py-2 shadow-[0_8px_24px_rgba(0,0,0,0.45)]">
      <p className="mb-1.5 font-mono text-[11px] text-ink">
        {new Date(point.t).toLocaleString([], {
          month: 'short',
          day: 'numeric',
          hour: '2-digit',
          minute: '2-digit',
        })}
      </p>

      <dl className="space-y-0.5 text-[11px]">
        <Row colour="var(--color-sev-critical)" label="Errors" value={point.errorEvents} />
        <Row colour="var(--color-sev-medium)" label="Warnings" value={point.warningEvents} />
      </dl>

      {inBucket.length > 0 && (
        <div className="mt-1.5 space-y-0.5 border-t border-line pt-1.5 text-[11px]">
          {inBucket.map((marker) => (
            <p key={`${marker.kind}-${marker.at}`} className="text-ink-muted">
              <span className={marker.kind === 'deployment' ? 'text-accent' : 'text-sev-critical'}>
                {marker.kind === 'deployment' ? '▲ Deploy' : '▼ Incident'}
              </span>{' '}
              <span className="font-mono">{marker.service}</span>{' '}
              <span className="text-ink">{marker.label}</span>
            </p>
          ))}
        </div>
      )}
    </div>
  )
}

function Row({ colour, label, value }: { colour: string; label: string; value: number }) {
  return (
    <div className="flex items-center justify-between gap-4">
      <dt className="flex items-center gap-1.5 text-ink-muted">
        <span aria-hidden className="h-1.5 w-1.5 rounded-full" style={{ background: colour }} />
        {label}
      </dt>
      <dd className="tabular text-ink">{formatCount(value)}</dd>
    </div>
  )
}
