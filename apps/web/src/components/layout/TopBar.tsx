import { Bell, Search } from 'lucide-react'

import { ENVIRONMENTS, TIME_RANGES, useSession } from '../../app/session'
import type { EnvironmentKey, TimeRangeKey } from '../../app/session'
import { cn } from '../../lib/cn'
import { HealthDot, type Health } from '../ui/Badge'
import { Select } from '../ui/Select'

export function TopBar({
  systemHealth = 'Unknown',
  unreadNotifications = 0,
  onOpenSearch,
}: {
  systemHealth?: Health
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

        <span
          className="hidden items-center gap-1.5 text-[11px] sm:inline-flex"
          title="Platform status"
        >
          <HealthDot health={systemHealth} />
        </span>

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
