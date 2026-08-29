import { ChevronsLeft, ChevronsRight, CircleHelp, Zap } from 'lucide-react'
import { useSyncExternalStore } from 'react'
import { NavLink } from 'react-router'

import { NAV_GROUPS } from '../../app/navigation'
import { useSession } from '../../app/session'
import { useCurrentSession } from '../../lib/api/queries'
import { cn } from '../../lib/cn'

/** Counts shown against nav items. Wired to live data in a later phase. */
export type NavBadges = Partial<Record<'activeIncidents', number>>

/**
 * Below this the expanded sidebar costs more than it gives: 224px of a 375px
 * screen is most of the viewport, and it squeezes an incident into a column too
 * narrow to read. Matches Tailwind's md.
 */
const NARROW = '(max-width: 767px)'

/**
 * Tracks a media query without an effect, so the first paint is already
 * correct - an effect-based version renders expanded, then snaps.
 */
function useMatchMedia(query: string): boolean {
  return useSyncExternalStore(
    (onChange) => {
      const list = window.matchMedia(query)
      list.addEventListener('change', onChange)
      return () => list.removeEventListener('change', onChange)
    },
    () => window.matchMedia(query).matches,
    // Server-rendered HTML has no viewport to measure; assume the wide case.
    () => false,
  )
}

export function Sidebar({ badges = {} }: { badges?: NavBadges }) {
  const { sidebarCollapsed, toggleSidebar } = useSession()
  const isNarrow = useMatchMedia(NARROW)

  // The stored preference still applies on a wide screen; a narrow one simply
  // has no room for the expanded state, whatever was chosen earlier.
  const collapsed = sidebarCollapsed || isNarrow

  const session = useCurrentSession()

  // Falls back to the key's own name rather than to a person's. Inventing a
  // name here is what this replaced.
  const organizationName = session.data?.organization?.name ?? session.data?.apiKeyName ?? '—'

  const actorFallback = session.isPending ? '…' : 'No user bound to this key'

  return (
    <nav
      aria-label="Main"
      className={cn(
        'flex shrink-0 flex-col border-r border-line bg-surface transition-[width] duration-150',
        collapsed ? 'w-14' : 'w-56',
      )}
    >
      <div className="flex h-12 items-center gap-2 border-b border-line px-3">
        <Zap size={16} className="shrink-0 text-accent" aria-hidden />
        {!collapsed && (
          <span className="truncate text-[13px] font-semibold tracking-tight">IncidentIQ</span>
        )}
      </div>

      <div className="flex-1 overflow-y-auto py-2">
        {NAV_GROUPS.map((group) => (
          <div key={group.label} className="mb-1">
            {!collapsed && (
              <p className="px-3 pb-1 pt-2 text-[10px] font-semibold uppercase tracking-[0.08em] text-ink-subtle">
                {group.label}
              </p>
            )}

            <ul className="px-1.5">
              {group.items.map((item) => {
                const Icon = item.icon
                const badge = item.badgeKey ? badges[item.badgeKey] : undefined

                return (
                  <li key={item.to}>
                    <NavLink
                      to={item.to}
                      // Only Overview needs exact matching; the rest should stay
                      // active while on their detail pages.
                      end={item.to === '/'}
                      title={collapsed ? item.label : undefined}
                      className={({ isActive }) =>
                        cn(
                          'group relative flex h-7 items-center gap-2.5 rounded-[4px] px-1.5',
                          'text-[12px] transition-quick',
                          isActive
                            ? 'bg-raised font-medium text-ink'
                            : 'text-ink-muted hover:bg-raised/60 hover:text-ink',
                          collapsed && 'justify-center px-0',
                        )
                      }
                    >
                      {({ isActive }) => (
                        <>
                          {/* An accent bar rather than colour alone, so the
                              active item survives a greyscale screenshot. */}
                          {isActive && (
                            <span
                              aria-hidden
                              className="absolute left-0 top-1/2 h-4 w-0.5 -translate-y-1/2 rounded-r bg-accent"
                            />
                          )}
                          <Icon size={14} className="shrink-0" aria-hidden />
                          {!collapsed && <span className="flex-1 truncate">{item.label}</span>}
                          {!collapsed && badge !== undefined && badge > 0 && (
                            <span className="rounded-full bg-sev-critical/15 px-1.5 text-[10px] font-semibold text-sev-critical tabular">
                              {badge}
                            </span>
                          )}
                        </>
                      )}
                    </NavLink>
                  </li>
                )
              })}
            </ul>
          </div>
        ))}
      </div>

      <div className="border-t border-line p-1.5">
        <button
          type="button"
          className={cn(
            'mb-1 flex w-full items-center gap-2 rounded-[4px] px-1.5 py-1.5 text-left',
            'transition-quick hover:bg-raised',
            collapsed && 'justify-center',
          )}
          aria-label="Organization"
          title={organizationName}
        >
          <span className="grid h-5 w-5 shrink-0 place-items-center rounded-[3px] bg-accent-soft text-[10px] font-semibold text-accent">
            {initials(organizationName).slice(0, 1)}
          </span>
          {!collapsed && (
            <span className="min-w-0 flex-1">
              <span className="block truncate text-[12px] text-ink">{organizationName}</span>
              <span className="block truncate text-[10px] text-ink-subtle">Organization</span>
            </span>
          )}
        </button>

        <div
          className={cn(
            'flex items-center gap-1',
            collapsed ? 'flex-col' : 'justify-between',
          )}
        >
          {/* The person actions are recorded against, not whoever is holding
              the keyboard. There is no login: the API key decides, and naming
              anybody else here would mean the page says one name while the
              incident timeline says another. */}
          <div
            className={cn('flex min-w-0 items-center gap-2 px-1.5', collapsed && 'px-0')}
            title={
              session.data?.actor
                ? `${session.data.actor.displayName} · ${session.data.actor.email}. Actions are recorded against this user.`
                : 'This API key is not bound to a user, so incident actions will be refused.'
            }
          >
            <span
              className={cn(
                'grid h-5 w-5 shrink-0 place-items-center rounded-full text-[10px] font-medium',
                session.data?.actor
                  ? 'bg-raised text-ink-muted'
                  : 'bg-sev-high/15 text-sev-high',
              )}
            >
              {session.data?.actor ? initials(session.data.actor.displayName) : '?'}
            </span>
            {!collapsed && (
              <span className="truncate text-[11px] text-ink-muted">
                {session.data?.actor?.displayName ?? actorFallback}
              </span>
            )}
          </div>

          <div className="flex items-center">
            <button
              type="button"
              className="grid h-6 w-6 place-items-center rounded-[4px] text-ink-subtle transition-quick hover:bg-raised hover:text-ink"
              aria-label="Help and documentation"
            >
              <CircleHelp size={13} aria-hidden />
            </button>
            <button
              type="button"
              onClick={toggleSidebar}
              className="grid h-6 w-6 place-items-center rounded-[4px] text-ink-subtle transition-quick hover:bg-raised hover:text-ink"
              aria-label={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
              aria-pressed={sidebarCollapsed}
            >
              {collapsed ? <ChevronsRight size={13} aria-hidden /> : <ChevronsLeft size={13} aria-hidden />}
            </button>
          </div>
        </div>
      </div>
    </nav>
  )
}

/**
 * Initials from a display name.
 *
 * Two letters where there are two words, one where there is one. Deliberately
 * naive: a name is whatever the users table says, and guessing harder at how
 * to abbreviate somebody's name is a good way to get it wrong.
 */
function initials(name: string): string {
  const words = name.trim().split(/\s+/).filter(Boolean)

  if (words.length === 0) return '?'
  if (words.length === 1) return words[0].slice(0, 2).toUpperCase()

  return (words[0][0] + words[words.length - 1][0]).toUpperCase()
}
