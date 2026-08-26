import type { ReactNode } from 'react'

/**
 * One per page. Capped in height on purpose: a hero band would push the first
 * table row below the fold, and the user is already inside the product - there
 * is nothing left to sell them.
 */
export function PageHeader({
  title,
  description,
  actions,
}: {
  title: string
  description?: ReactNode
  actions?: ReactNode
}) {
  return (
    <header className="mb-4 flex flex-wrap items-start justify-between gap-3">
      <div className="min-w-0">
        <h1 className="text-[18px] font-semibold leading-tight text-ink">{title}</h1>
        {description && <p className="mt-0.5 text-[12px] text-ink-muted">{description}</p>}
      </div>
      {actions && <div className="flex flex-wrap items-center gap-2">{actions}</div>}
    </header>
  )
}
