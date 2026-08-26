import { useId } from 'react'

/**
 * Inline SVG rather than a chart component.
 *
 * A sparkline has no axes, no legend and no tooltip - it is a shape. Pulling a
 * charting library into five KPI cards to draw five polylines would cost more
 * than the whole dashboard renders in.
 */
export function Sparkline({
  values,
  className = 'text-ink-subtle',
  width = 72,
  height = 20,
}: {
  values: number[]
  className?: string
  width?: number
  height?: number
}) {
  const gradientId = useId()

  if (values.length < 2) {
    // A single point is not a trend. Render the baseline rather than a dot
    // that implies one.
    return (
      <svg width={width} height={height} aria-hidden className={className}>
        <line
          x1={0}
          y1={height - 1}
          x2={width}
          y2={height - 1}
          stroke="currentColor"
          strokeWidth={1}
          opacity={0.3}
        />
      </svg>
    )
  }

  const max = Math.max(...values)
  const min = Math.min(...values)
  // A flat series would divide by zero; drawing it mid-height reads correctly
  // as "no change".
  const range = max - min || 1

  const points = values.map((value, index) => {
    const x = (index / (values.length - 1)) * width
    const y = height - 1 - ((value - min) / range) * (height - 2)
    return [x, y] as const
  })

  const line = points.map(([x, y]) => `${x.toFixed(1)},${y.toFixed(1)}`).join(' ')
  const area = `${line} ${width},${height} 0,${height}`

  return (
    <svg
      width={width}
      height={height}
      viewBox={`0 0 ${width} ${height}`}
      aria-hidden
      className={className}
      preserveAspectRatio="none"
    >
      <defs>
        <linearGradient id={gradientId} x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stopColor="currentColor" stopOpacity={0.22} />
          <stop offset="100%" stopColor="currentColor" stopOpacity={0} />
        </linearGradient>
      </defs>
      <polygon points={area} fill={`url(#${gradientId})`} />
      <polyline
        points={line}
        fill="none"
        stroke="currentColor"
        strokeWidth={1.25}
        strokeLinejoin="round"
        strokeLinecap="round"
      />
    </svg>
  )
}
