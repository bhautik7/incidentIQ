import { useVirtualizer } from '@tanstack/react-virtual'
import { useCallback, useEffect, useRef, useState } from 'react'

import { Skeleton } from '../../components/ui/Skeleton'
import { cn } from '../../lib/cn'
import type { LogEntry } from '../../types/api'
import { LogRow } from './LogRow'

/**
 * The log list, windowed.
 *
 * Only the rows near the viewport are in the DOM. That is not an optimisation
 * for its own sake: a retention window holds far more lines than a browser can
 * lay out, and the difference between rendering a window and rendering the list
 * is the difference between a usable page and a locked tab.
 *
 * Rows measure themselves rather than being assumed a fixed height, because an
 * expanded row is many times taller than a collapsed one. Guessing would make
 * the scrollbar lie and the list jump as it scrolled.
 *
 * Scrolling near the end asks for the next page. A log explorer is read by
 * scrolling backwards through time, so a "load more" button would sit in the
 * way of the only interaction the page has.
 */
export function LogTable({
  rows,
  isLoading,
  hasNextPage,
  isFetchingNextPage,
  onLoadMore,
  onFilterBy,
  emptyState,
}: {
  rows: LogEntry[]
  isLoading: boolean
  hasNextPage: boolean
  isFetchingNextPage: boolean
  onLoadMore: () => void
  onFilterBy: (field: 'service' | 'level' | 'traceId' | 'fingerprint', value: string) => void
  emptyState: React.ReactNode
}) {
  const scrollRef = useRef<HTMLDivElement>(null)
  const [expanded, setExpanded] = useState<Set<number>>(new Set())

  const virtualizer = useVirtualizer({
    count: rows.length,
    getScrollElement: () => scrollRef.current,
    // A collapsed row. Expanded rows correct themselves through measurement on
    // their first render, so this only has to be right for the common case.
    estimateSize: () => 26,
    overscan: 12,
    getItemKey: (index) => rows[index]?.id ?? index,
  })

  const toggle = useCallback((id: number) => {
    setExpanded((current) => {
      const next = new Set(current)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })
  }, [])

  // Fetch the next page as the end comes into view rather than when it is
  // reached, so scrolling does not stop to wait for the network.
  const items = virtualizer.getVirtualItems()
  const lastVisible = items.at(-1)?.index ?? 0

  useEffect(() => {
    if (hasNextPage && !isFetchingNextPage && lastVisible >= rows.length - 20) {
      onLoadMore()
    }
  }, [hasNextPage, isFetchingNextPage, lastVisible, rows.length, onLoadMore])

  if (isLoading) {
    return (
      <div className="rounded-panel border border-line bg-surface">
        <LogHeader />
        <div role="status" aria-label="Loading logs" className="divide-y divide-line/50">
          {Array.from({ length: 18 }).map((_, index) => (
            <div key={index} className="flex h-[26px] items-center gap-3 px-2">
              <Skeleton className="h-2 w-20" style={{ animationDelay: `${index * 20}ms` }} />
              <Skeleton className="h-2 w-10" />
              <Skeleton className="h-2 w-24" />
              <Skeleton className="h-2 flex-1" />
            </div>
          ))}
        </div>
      </div>
    )
  }

  if (rows.length === 0) {
    return (
      <div className="rounded-panel border border-line bg-surface">
        <LogHeader />
        <div className="p-3">{emptyState}</div>
      </div>
    )
  }

  return (
    <div className="rounded-panel border border-line bg-surface">
      <LogHeader />

      <div
        ref={scrollRef}
        // A fixed viewport is what makes windowing possible at all: the list
        // scrolls inside the panel rather than growing the page.
        // Inline styles, not utility classes, and deliberately so. Both of
        // these are load-bearing for correctness rather than appearance: if the
        // height is not a real constraint the container auto-sizes to its
        // content, and if the overflow is not a real scroller the list never
        // scrolls. Either one missing leaves the virtualiser rendering every
        // row - windowing that silently does nothing, which looks fine until
        // the window holds a million lines. A plain style cannot be dropped by
        // a class-generation step that has not rescanned this file.
        style={{ height: 'calc(100vh - 320px)', minHeight: '320px', overflow: 'auto' }}
        tabIndex={0}
        aria-label="Log lines"
      >
        <div className="relative w-full" style={{ height: `${virtualizer.getTotalSize()}px` }}>
          {items.map((item) => {
            const row = rows[item.index]

            return (
              <div
                key={item.key}
                ref={virtualizer.measureElement}
                data-index={item.index}
                className="absolute left-0 top-0 w-full"
                style={{ transform: `translateY(${item.start}px)` }}
              >
                <LogRow
                  entry={row}
                  expanded={expanded.has(row.id)}
                  onToggle={() => toggle(row.id)}
                  onFilterBy={onFilterBy}
                />
              </div>
            )
          })}
        </div>

        {/* Inside the scroller, so it appears where the reader is looking
            rather than pinned somewhere they cannot see. */}
        {isFetchingNextPage && (
          <p className="py-2 text-center text-[11px] text-ink-subtle" role="status">
            Loading older lines…
          </p>
        )}

        {!hasNextPage && rows.length > 0 && (
          <p className="py-2 text-center text-[11px] text-ink-subtle">
            End of the retention window.
          </p>
        )}
      </div>
    </div>
  )
}

function LogHeader() {
  return (
    <div
      className={cn(
        'flex items-center gap-3 border-b border-line px-2 py-1.5',
        'text-[10px] uppercase tracking-[0.05em] text-ink-subtle',
      )}
    >
      <span className="w-3" aria-hidden />
      <span className="w-[92px]">Time</span>
      <span className="w-[52px]">Level</span>
      <span className="w-[132px]">Service</span>
      <span className="flex-1">Message</span>
      <span className="w-[104px]">Trace</span>
      <span className="w-[64px]">Incident</span>
    </div>
  )
}
