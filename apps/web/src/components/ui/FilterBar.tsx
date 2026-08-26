import { Search, X } from 'lucide-react'
import { useEffect, useState, type ReactNode } from 'react'

import { cn } from '../../lib/cn'

/**
 * The row of controls above a DataTable.
 *
 * Layout only - it holds no filter state and knows no filter names. The page
 * owns the filters, because the page is what binds them to the URL.
 */
export function FilterBar({ children, className }: { children: ReactNode; className?: string }) {
  return (
    <div
      className={cn(
        'flex flex-wrap items-center gap-2 rounded-panel border border-line bg-surface px-2.5 py-2',
        className,
      )}
    >
      {children}
    </div>
  )
}

/**
 * Search that reports on a pause rather than a keystroke.
 *
 * Every change re-queries the server and rewrites the URL, so committing per
 * character would issue a request per character. 250ms is long enough to hold
 * a burst of typing and short enough that it still feels immediate.
 */
export function SearchInput({
  value,
  onChange,
  placeholder = 'Search…',
  label,
  delay = 250,
}: {
  value: string
  onChange: (value: string) => void
  placeholder?: string
  label: string
  delay?: number
}) {
  const [draft, setDraft] = useState(value)
  const [committed, setCommitted] = useState(value)

  // The URL can change without the input doing so - the back button, a pasted
  // link, or Clear all - and the box must follow it. Adjusting during render
  // rather than in an effect means the input never paints a stale value first.
  if (value !== committed) {
    setCommitted(value)
    setDraft(value)
  }

  useEffect(() => {
    if (draft === value) return

    const timer = window.setTimeout(() => onChange(draft), delay)
    return () => window.clearTimeout(timer)
  }, [draft, value, onChange, delay])

  return (
    <div className="relative flex min-w-[180px] flex-1 items-center">
      <Search size={12} aria-hidden className="pointer-events-none absolute left-2 text-ink-subtle" />

      <input
        type="search"
        value={draft}
        aria-label={label}
        placeholder={placeholder}
        onChange={(event) => setDraft(event.target.value)}
        onKeyDown={(event) => {
          // Enter commits immediately rather than waiting out the debounce.
          if (event.key === 'Enter') onChange(draft)
          if (event.key === 'Escape') {
            setDraft('')
            onChange('')
          }
        }}
        className={cn(
          'h-7 w-full rounded-[4px] border border-line bg-raised pl-7 pr-7 text-[12px] text-ink',
          'placeholder:text-ink-subtle transition-quick hover:border-line-strong',
          '[&::-webkit-search-cancel-button]:appearance-none',
        )}
      />

      {draft && (
        <button
          type="button"
          aria-label="Clear search"
          onClick={() => {
            setDraft('')
            onChange('')
          }}
          className="absolute right-1.5 text-ink-subtle transition-quick hover:text-ink"
        >
          <X size={12} aria-hidden />
        </button>
      )}
    </div>
  )
}

/**
 * What the current filters are doing to the result count, and the way out.
 *
 * "3 of 47" is the useful sentence: it says both what is shown and that
 * something is being hidden. Without it an over-filtered empty table reads as
 * an outage.
 */
export function FilterSummary({
  filtered,
  total,
  noun,
  activeFilters,
  onClear,
}: {
  filtered: number
  total: number | null
  /** Singular; pluralised with an s. */
  noun: string
  activeFilters: { label: string; value: string; onRemove: () => void }[]
  onClear: () => void
}) {
  if (activeFilters.length === 0) {
    return (
      <p className="px-0.5 text-[11px] text-ink-subtle">
        <span className="tabular">{filtered}</span> {noun}
        {filtered === 1 ? '' : 's'}
      </p>
    )
  }

  return (
    <div className="flex flex-wrap items-center gap-x-2 gap-y-1 px-0.5 text-[11px] text-ink-subtle">
      <span className="tabular">
        {filtered}
        {total !== null && total !== filtered ? ` of ${total}` : ''} matching
      </span>

      {activeFilters.map((filter) => (
        <button
          key={filter.label}
          type="button"
          onClick={filter.onRemove}
          aria-label={`Remove ${filter.label} filter`}
          className={cn(
            'inline-flex items-center gap-1 rounded-[4px] border border-line bg-raised px-1.5 py-px',
            'text-ink-muted transition-quick hover:border-line-strong hover:text-ink',
          )}
        >
          <span className="text-ink-subtle">{filter.label}:</span>
          {filter.value}
          <X size={10} aria-hidden />
        </button>
      ))}

      <button
        type="button"
        onClick={onClear}
        className="text-accent transition-quick hover:underline"
      >
        Clear all
      </button>
    </div>
  )
}
