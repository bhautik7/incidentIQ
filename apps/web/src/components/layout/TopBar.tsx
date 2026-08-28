import { Bell, Search } from 'lucide-react'

import { ENVIRONMENTS, TIME_RANGES, useSession } from '../../app/session'
import type { EnvironmentKey, TimeRangeKey } from '../../app/session'
import { cn } from '../../lib/cn'
import { useRealtime, type RealtimeStatus } from '../../lib/realtime'
import { HealthDot, type Health } from '../ui/Badge'
import { Select } from '../ui/Select'

export function TopBar({
  unreadNotifications = 0,
  onOpenSearch,
}: {
  unreadNotifications?: number
  onOpenSearch: () => void
}) {
  const { environment, setEnvironment, timeRange, setTimeRange } = useSession()

  return (
    <header className="flex h-12 shrink-0 items-center gap-2 border-b border-line bg-surface px-3">
      {/* A button rather than an input: the real search is the command
          palette, and pretending otherwise costs a click and a focus trap. */}
      <button
        type="button"
        onClick={onOpenSearch}
        className={cn(
          'flex h-7 min-w-0 flex-1 max-w-md items-center gap-2 rounded-[4px] border border-line',
          'bg-raised px-2 text-left text-[12px] text-ink-subtle transition-quick',
          'hover:border-line-strong hover:text-ink-muted',
        )}
      >
        <Search size={13} aria-hidden />
        <span className="flex-1 truncate">Search incidents, services, traces…</span>
        <kbd className="rounded-[3px] border border-line bg-surface px-1 font-mono text-[10px] text-ink-subtle">
          ⌘K
        </kbd>
      </button>

      <div className="ml-auto flex items-center gap-2">
        <Select
          label="Environment"
          value={environment}
          onChange={(event) => setEnvironment(event.target.value as EnvironmentKey)}
        >
          {ENVIRONMENTS.map((option) => (
            <option key={option.key} value={option.key}>
              {option.label}
            </option>
          ))}
        </Select>

        <Select
          label="Time range"
          value={timeRange}
          onChange={(event) => setTimeRange(event.target.value as TimeRangeKey)}
        >
          {TIME_RANGES.map((option) => (
            <option key={option.key} value={option.key}>
              {option.label}
            </option>
          ))}
        </Select>

        <div className="mx-1 h-5 w-px bg-line" aria-hidden />

        {/* What this dot means changed with the hub: it used to be a
            placeholder reading "Unknown". It now reports whether the dashboard
            is receiving live updates, which is the thing a reader needs to know
            before trusting what is on screen. */}
        <RealtimeIndicator />

        <button
          type="button"
          className="relative grid h-7 w-7 place-items-center rounded-[4px] text-ink-muted transition-quick hover:bg-raised hover:text-ink"
          aria-label={
            unreadNotifications > 0
              ? `Notifications, ${unreadNotifications} unread`
              : 'Notifications'
          }
        >
          <Bell size={14} aria-hidden />
          {unreadNotifications > 0 && (
            <span className="absolute right-1 top-1 h-1.5 w-1.5 rounded-full bg-sev-critical" />
          )}
        </button>

        <button
          type="button"
          className="grid h-7 w-7 shrink-0 place-items-center rounded-full bg-raised text-[10px] font-medium text-ink-muted transition-quick hover:text-ink"
          aria-label="Account menu"
        >
          BS
        </button>
      </div>
    </header>
  )
}


const REALTIME_LABEL: Record<RealtimeStatus, { health: Health; text: string; title: string }> = {
  live: { health: 'Healthy', text: 'Live', title: 'Receiving live updates.' },
  connecting: { health: 'Unknown', text: 'Connecting', title: 'Opening the live connection.' },
  reconnecting: {
    health: 'Degraded',
    text: 'Reconnecting',
    title: 'The live connection dropped. Reconnecting; the page is polling meanwhile.',
  },
  offline: {
    health: 'Critical',
    text: 'Not live',
    title: 'No live connection. The page is polling every 15 seconds instead.',
  },
}

/**
 * Whether what is on screen is arriving live.
 *
 * Worth a permanent slot in the top bar rather than a transient toast: during an
 * incident, "is this current?" is a question someone asks of every number they
 * are looking at, and a dashboard that quietly stopped updating is the failure
 * this is here to make visible.
 */
function RealtimeIndicator() {
  const { status } = useRealtime()
  const label = REALTIME_LABEL[status]

  return (
    <span className="hidden items-center gap-1.5 text-[11px] sm:inline-flex" title={label.title}>
      <HealthDot health={label.health} label={false} />
      <span className="text-ink-muted">{label.text}</span>
    </span>
  )
}