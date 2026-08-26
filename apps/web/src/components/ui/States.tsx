import { AlertTriangle, RefreshCw } from 'lucide-react'
import type { ReactNode } from 'react'

import { Button } from './Button'

/**
 * An empty state is a moment where the user is already looking. Spending it on
 * "No data." wastes it - the useful version says what the emptiness means and
 * what to do next.
 */
export function EmptyState({
  icon,
  title,
  description,
  action,
}: {
  icon?: ReactNode
  title: string
  description?: string
  action?: ReactNode
}) {
  return (
    <div className="flex flex-col items-center justify-center rounded-panel border border-dashed border-line px-6 py-12 text-center">
      {icon && <div className="mb-3 text-ink-subtle">{icon}</div>}
      <p className="text-[13px] font-medium text-ink">{title}</p>
      {description && <p className="mt-1 max-w-sm text-[12px] text-ink-muted">{description}</p>}
      {action && <div className="mt-4">{action}</div>}
    </div>
  )
}

/**
 * Failures name the thing that failed and offer the retry, because "Something
 * went wrong" gives an on-call engineer nothing to act on.
 */
export function ErrorState({
  title = 'Unable to load data',
  description,
  onRetry,
}: {
  title?: string
  description?: string
  onRetry?: () => void
}) {
  return (
    <div
      role="alert"
      className="flex flex-col items-center justify-center rounded-panel border border-sev-critical/25 bg-sev-critical/5 px-6 py-10 text-center"
    >
      <AlertTriangle size={18} className="mb-3 text-sev-critical" aria-hidden />
      <p className="text-[13px] font-medium text-ink">{title}</p>
      {description && <p className="mt-1 max-w-md text-[12px] text-ink-muted">{description}</p>}
      {onRetry && (
        <Button className="mt-4" icon={<RefreshCw size={12} aria-hidden />} onClick={onRetry}>
          Retry
        </Button>
      )}
    </div>
  )
}
