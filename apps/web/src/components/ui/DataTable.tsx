import { ArrowDown, ArrowUp, ChevronsUpDown } from 'lucide-react'
import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type KeyboardEvent,
  type ReactNode,
} from 'react'

import { cn } from '../../lib/cn'
import { Skeleton } from './Skeleton'
import { ErrorState } from './States'

/**
 * The table Incidents, Logs, Deployments and AI Investigations all share.
 *
 * Two rules keep it reusable rather than merely shared:
 *
 * 1. It is fully controlled. It never sorts, filters, pages or fetches. It
 *    renders the rows it is given and reports intent. Sorting a page of 25 in
 *    the browser would silently mean "sort the rows you happen to be looking
 *    at", which in a work queue is worse than not sorting at all - so the
 *    server owns the order and this component owns only the affordance.
 *
 * 2. It owns the non-happy states - loading, empty, failed, and waiting on an
 *    unreachable API. These are where dense tables usually drift apart, and
 *    rendering them inside the table body keeps the header and column widths
 *    fixed so the layout never jumps.
 *
 * Pagination lives in a sibling component rather than here, because the four
 * pages do not agree on it: incidents page by offset, logs will page by cursor.
 */

export type SortDirection = 'asc' | 'desc'

export interface SortState {
  key: string
  direction: SortDirection
}

export interface Column<Row> {
  /** Stable identity for React keys. */
  id: string
  header: ReactNode
  /**
   * The value the server sorts on. Omit to make the column unsortable - which
   * is the honest default for anything the API cannot order by.
   */
  sortKey?: string
  /** Which way the first click sorts. Should mirror the API's own default. */
  defaultDirection?: SortDirection
  align?: 'left' | 'right'
  /** Width and truncation classes for both the header and its cells. */
  className?: string
  cell: (row: Row) => ReactNode
  /** Accessible label when the header is a glyph or abbreviation. */
  headerLabel?: string
}

interface DataTableProps<Row> {
  rows: Row[]
  columns: Column<Row>[]
  getRowId: (row: Row) => string
  /** Read by screen readers in place of a visible table title. */
  caption: string

  sort?: SortState
  onSortChange?: (sort: SortState) => void

  selectedIds?: ReadonlySet<string>
  onSelectionChange?: (ids: Set<string>) => void

  /** Enter, or a click anywhere on the row that is not a link or a control. */
  onRowActivate?: (row: Row) => void

  isLoading?: boolean
  /**
   * The query wanted to fetch and could not, and is waiting rather than
   * failing. Distinct from loading, and it must not render as loading.
   */
  isPaused?: boolean
  error?: unknown
  onRetry?: () => void
  errorTitle?: string
  errorDescription?: string
  emptyState?: ReactNode

  /** Below this the table scrolls horizontally rather than crushing columns. */
  minWidthClassName?: string
  skeletonRows?: number
}

export function DataTable<Row>({
  rows,
  columns,
  getRowId,
  caption,
  sort,
  onSortChange,
  selectedIds,
  onSelectionChange,
  onRowActivate,
  isLoading = false,
  isPaused = false,
  error,
  onRetry,
  errorTitle = 'Unable to load data',
  errorDescription,
  emptyState,
  minWidthClassName = 'min-w-[960px]',
  skeletonRows = 12,
}: DataTableProps<Row>) {
  const selectable = Boolean(onSelectionChange)
  const columnCount = columns.length + (selectable ? 1 : 0)

  // Roving tabindex: the table is one tab stop, and the arrow keys move within
  // it. Making every row tabbable would put 25 stops between the filter bar and
  // the pagination controls.
  const [rawFocusedIndex, setFocusedIndex] = useState(0)
  const rowRefs = useRef<(HTMLTableRowElement | null)[]>([])
  // Anchor for shift-click range selection.
  const lastToggledIndex = useRef<number | null>(null)

  const rowIds = useMemo(() => rows.map(getRowId), [rows, getRowId])

  // A shorter page must not leave the focused index past the end. Clamping as
  // it is read, rather than correcting it in an effect, means there is never a
  // render in which the index is out of range.
  const focusedIndex = Math.min(rawFocusedIndex, Math.max(0, rows.length - 1))

  // A new page of rows is a new set of rows, so the shift-selection anchor no
  // longer points at anything. This mutates a ref rather than setting state,
  // so it schedules no render of its own.
  useEffect(() => {
    lastToggledIndex.current = null
  }, [rows])

  const focusRow = useCallback((index: number) => {
    setFocusedIndex(index)
    rowRefs.current[index]?.focus()
  }, [])

  const toggle = useCallback(
    (index: number, extendRange: boolean) => {
      if (!onSelectionChange) return

      const next = new Set(selectedIds ?? [])
      const anchor = lastToggledIndex.current

      // Shift-click selects the span rather than the single row, which is what
      // "acknowledge these six" actually looks like.
      if (extendRange && anchor !== null) {
        const [from, to] = anchor < index ? [anchor, index] : [index, anchor]
        const selecting = !next.has(rowIds[index])

        for (let i = from; i <= to; i++) {
          if (selecting) next.add(rowIds[i])
          else next.delete(rowIds[i])
        }
      } else {
        const id = rowIds[index]
        if (next.has(id)) next.delete(id)
        else next.add(id)
        lastToggledIndex.current = index
      }

      onSelectionChange(next)
    },
    [onSelectionChange, selectedIds, rowIds],
  )

  const handleSort = useCallback(
    (column: Column<Row>) => {
      if (!column.sortKey || !onSortChange) return

      const isCurrent = sort?.key === column.sortKey

      onSortChange({
        key: column.sortKey,
        // Re-clicking the active column flips it; a new column starts at its
        // own natural direction rather than always ascending.
        direction: isCurrent
          ? sort.direction === 'asc'
            ? 'desc'
            : 'asc'
          : (column.defaultDirection ?? 'desc'),
      })
    },
    [onSortChange, sort],
  )

  const onKeyDown = useCallback(
    (event: KeyboardEvent<HTMLTableSectionElement>) => {
      if (rows.length === 0) return

      switch (event.key) {
        case 'ArrowDown':
          event.preventDefault()
          focusRow(Math.min(focusedIndex + 1, rows.length - 1))
          break
        case 'ArrowUp':
          event.preventDefault()
          focusRow(Math.max(focusedIndex - 1, 0))
          break
        case 'Home':
          event.preventDefault()
          focusRow(0)
          break
        case 'End':
          event.preventDefault()
          focusRow(rows.length - 1)
          break
        case 'Enter':
          if (onRowActivate) {
            event.preventDefault()
            onRowActivate(rows[focusedIndex])
          }
          break
        case ' ':
          if (selectable) {
            // Space would otherwise scroll the page out from under the row.
            event.preventDefault()
            toggle(focusedIndex, event.shiftKey)
          }
          break
        default:
          break
      }
    },
    [rows, focusedIndex, focusRow, onRowActivate, selectable, toggle],
  )

  const allOnPageSelected = rows.length > 0 && rowIds.every((id) => selectedIds?.has(id))
  const someOnPageSelected = rowIds.some((id) => selectedIds?.has(id))

  const toggleAllOnPage = () => {
    if (!onSelectionChange) return

    const next = new Set(selectedIds ?? [])
    // Scoped to the page on purpose: selecting rows the user has not seen, on
    // pages they have not loaded, is not something a checkbox should imply.
    if (allOnPageSelected) rowIds.forEach((id) => next.delete(id))
    else rowIds.forEach((id) => next.add(id))

    onSelectionChange(next)
  }

  return (
    <div className="overflow-x-auto rounded-panel border border-line bg-surface">
      <table className={cn('w-full border-collapse text-[12px]', minWidthClassName)}>
        <caption className="sr-only">{caption}</caption>

        <thead>
          <tr className="border-b border-line text-left text-[10px] uppercase tracking-[0.05em] text-ink-subtle">
            {selectable && (
              <th scope="col" className="w-8 px-3 py-1.5">
                <SelectAllCheckbox
                  checked={allOnPageSelected}
                  indeterminate={!allOnPageSelected && someOnPageSelected}
                  disabled={rows.length === 0}
                  onChange={toggleAllOnPage}
                />
              </th>
            )}

            {columns.map((column) => {
              const active = Boolean(column.sortKey) && sort?.key === column.sortKey

              return (
                <th
                  key={column.id}
                  scope="col"
                  // aria-sort is what tells a screen reader the table is
                  // ordered, and by which column.
                  aria-sort={active ? (sort?.direction === 'asc' ? 'ascending' : 'descending') : undefined}
                  className={cn(
                    'px-3 py-1.5 font-medium',
                    column.align === 'right' && 'text-right',
                    column.className,
                  )}
                >
                  {column.sortKey && onSortChange ? (
                    <button
                      type="button"
                      onClick={() => handleSort(column)}
                      className={cn(
                        'group inline-flex items-center gap-1 uppercase tracking-[0.05em]',
                        'transition-quick hover:text-ink-muted',
                        active && 'text-ink-muted',
                        column.align === 'right' && 'flex-row-reverse',
                      )}
                      aria-label={
                        column.headerLabel ??
                        (typeof column.header === 'string' ? `Sort by ${column.header}` : undefined)
                      }
                    >
                      {column.header}
                      <SortGlyph active={active} direction={sort?.direction} />
                    </button>
                  ) : (
                    <span aria-label={column.headerLabel}>{column.header}</span>
                  )}
                </th>
              )
            })}
          </tr>
        </thead>

        <tbody className="divide-y divide-line" onKeyDown={onKeyDown}>
          {/*
            Error is tested before loading on purpose. A query that keeps
            retrying can report itself as pending and failed at the same time,
            and a skeleton is a promise that data is on its way - showing one
            over a failure leaves the table shimmering forever while the reason
            it is empty sits unread.
          */}
          {error ? (
            <tr>
              <td colSpan={columnCount} className="p-3">
                <ErrorState title={errorTitle} description={errorDescription} onRetry={onRetry} />
              </td>
            </tr>
          ) : isPaused && rows.length === 0 ? (
            <tr>
              <td colSpan={columnCount} className="p-3">
                <ErrorState
                  title="Waiting to reach the API"
                  description="The request could not be sent and is queued for retry. The API may be restarting, or this machine may have lost its connection."
                  onRetry={onRetry}
                />
              </td>
            </tr>
          ) : isLoading ? (
            <SkeletonRows rows={skeletonRows} columns={columnCount} />
          ) : rows.length === 0 ? (
            <tr>
              <td colSpan={columnCount} className="p-3">
                {emptyState}
              </td>
            </tr>
          ) : (
            rows.map((row, index) => {
              const id = rowIds[index]
              const selected = selectedIds?.has(id) ?? false

              return (
                <tr
                  key={id}
                  ref={(element) => {
                    rowRefs.current[index] = element
                  }}
                  tabIndex={index === focusedIndex ? 0 : -1}
                  aria-selected={selectable ? selected : undefined}
                  onFocus={() => setFocusedIndex(index)}
                  onClick={(event) => {
                    // A click that landed on a link or a checkbox already did
                    // something; activating the row as well would fight it.
                    if ((event.target as HTMLElement).closest('a,button,input,label')) return
                    onRowActivate?.(row)
                  }}
                  className={cn(
                    'h-[34px] transition-quick',
                    onRowActivate && 'cursor-pointer',
                    selected ? 'bg-accent-soft/25' : 'hover:bg-raised',
                    'focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-accent',
                  )}
                >
                  {selectable && (
                    <td className="px-3 py-1.5">
                      <RowCheckbox
                        checked={selected}
                        label={`Select row ${index + 1}`}
                        onToggle={(extendRange) => toggle(index, extendRange)}
                      />
                    </td>
                  )}

                  {columns.map((column) => (
                    <td
                      key={column.id}
                      className={cn(
                        'px-3 py-1.5',
                        column.align === 'right' && 'text-right tabular',
                        column.className,
                      )}
                    >
                      {column.cell(row)}
                    </td>
                  ))}
                </tr>
              )
            })
          )}
        </tbody>
      </table>
    </div>
  )
}

function SortGlyph({ active, direction }: { active: boolean; direction?: SortDirection }) {
  if (!active) {
    // Present but faint: the column is sortable, and it is not the sort.
    return (
      <ChevronsUpDown
        size={11}
        aria-hidden
        className="text-transparent transition-quick group-hover:text-ink-subtle"
      />
    )
  }

  return direction === 'asc' ? (
    <ArrowUp size={11} aria-hidden className="text-accent" />
  ) : (
    <ArrowDown size={11} aria-hidden className="text-accent" />
  )
}

function SkeletonRows({ rows, columns }: { rows: number; columns: number }) {
  return (
    <>
      {Array.from({ length: rows }).map((_, row) => (
        <tr key={row} className="h-[34px]">
          {Array.from({ length: columns }).map((_, column) => (
            <td key={column} className="px-3 py-1.5">
              <Skeleton
                className={cn('h-2.5', column === 1 ? 'w-full' : 'w-12')}
                // A slight stagger stops it reading as one solid loading bar.
                style={{ animationDelay: `${(row * columns + column) * 12}ms` }}
              />
            </td>
          ))}
        </tr>
      ))}
    </>
  )
}

function SelectAllCheckbox({
  checked,
  indeterminate,
  disabled,
  onChange,
}: {
  checked: boolean
  indeterminate: boolean
  disabled: boolean
  onChange: () => void
}) {
  const ref = useRef<HTMLInputElement>(null)

  // indeterminate is a property, not an attribute - React cannot set it as JSX.
  useEffect(() => {
    if (ref.current) ref.current.indeterminate = indeterminate
  }, [indeterminate])

  return (
    <input
      ref={ref}
      type="checkbox"
      checked={checked}
      disabled={disabled}
      onChange={onChange}
      aria-label={checked ? 'Clear selection on this page' : 'Select every row on this page'}
      className="size-3 cursor-pointer accent-accent disabled:cursor-not-allowed disabled:opacity-40"
    />
  )
}

function RowCheckbox({
  checked,
  label,
  onToggle,
}: {
  checked: boolean
  label: string
  onToggle: (extendRange: boolean) => void
}) {
  return (
    <input
      type="checkbox"
      checked={checked}
      aria-label={label}
      onClick={(event) => event.stopPropagation()}
      onChange={(event) => onToggle((event.nativeEvent as MouseEvent).shiftKey)}
      className="size-3 cursor-pointer accent-accent"
    />
  )
}

/**
 * Offset pagination, stated as a range rather than a page number.
 *
 * "1–25 of 47" answers how much is left; "page 1 of 2" does not.
 */
export function TablePagination({
  page,
  pageSize,
  totalCount,
  onPageChange,
  disabled = false,
}: {
  page: number
  pageSize: number
  totalCount: number
  onPageChange: (page: number) => void
  disabled?: boolean
}) {
  const totalPages = Math.max(1, Math.ceil(totalCount / pageSize))
  const first = totalCount === 0 ? 0 : (page - 1) * pageSize + 1
  const last = Math.min(page * pageSize, totalCount)

  return (
    <nav
      aria-label="Pagination"
      className="mt-2 flex flex-wrap items-center justify-end gap-x-3 gap-y-1 text-[11px] text-ink-muted"
    >
      <span className="tabular whitespace-nowrap" aria-live="polite">
        {totalCount === 0 ? 'No results' : `${first}–${last} of ${totalCount}`}
      </span>

      <div className="flex items-center gap-1">
        <PageButton
          onClick={() => onPageChange(page - 1)}
          disabled={disabled || page <= 1}
          label="Previous page"
        >
          ‹ Prev
        </PageButton>
        <PageButton
          onClick={() => onPageChange(page + 1)}
          disabled={disabled || page >= totalPages}
          label="Next page"
        >
          Next ›
        </PageButton>
      </div>
    </nav>
  )
}

function PageButton({
  onClick,
  disabled,
  label,
  children,
}: {
  onClick: () => void
  disabled: boolean
  label: string
  children: ReactNode
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      aria-label={label}
      className={cn(
        'rounded-[4px] border border-line bg-raised px-2 py-1 text-[11px] text-ink',
        'transition-quick hover:border-line-strong hover:bg-overlay',
        'disabled:cursor-not-allowed disabled:opacity-40 disabled:hover:bg-raised',
      )}
    >
      {children}
    </button>
  )
}
