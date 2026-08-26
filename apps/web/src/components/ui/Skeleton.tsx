import type { CSSProperties } from 'react'

import { cn } from '../../lib/cn'

/**
 * Placeholder shaped like the content it replaces.
 *
 * A centred spinner tells the user nothing and then shifts the layout when it
 * resolves. A skeleton that matches the final structure means the page does
 * not move, and the shape itself communicates what is coming.
 */
export function Skeleton({ className, style }: { className?: string; style?: CSSProperties }) {
  return <div style={style} className={cn('animate-pulse rounded-[3px] bg-raised', className)} />
}

export function SkeletonTable({ rows = 8, columns = 6 }: { rows?: number; columns?: number }) {
  return (
    <div role="status" aria-label="Loading" className="divide-y divide-line">
      {Array.from({ length: rows }).map((_, row) => (
        <div key={row} className="flex h-[34px] items-center gap-4 px-3">
          {Array.from({ length: columns }).map((_, column) => (
            <Skeleton
              key={column}
              className={cn('h-2.5', column === 1 ? 'flex-1' : 'w-16')}
              // Slight stagger keeps it from reading as a single loading bar.
              style={{ animationDelay: `${(row * columns + column) * 12}ms` }}
            />
          ))}
        </div>
      ))}
    </div>
  )
}

export function SkeletonCard({ className }: { className?: string }) {
  return (
    <div className={cn('rounded-panel border border-line bg-surface p-4', className)}>
      <Skeleton className="mb-3 h-2.5 w-20" />
      <Skeleton className="mb-2 h-7 w-16" />
      <Skeleton className="h-2 w-24" />
    </div>
  )
}
