import {
  Activity,
  LayoutDashboard,
  type LucideIcon,
  ScrollText,
  Siren,
  Stethoscope,
} from 'lucide-react'

export type NavItem = {
  to: string
  label: string
  icon: LucideIcon
  /** Rendered as a count on the item, e.g. active incidents. */
  badgeKey?: 'activeIncidents'
}

export type NavGroup = {
  label: string
  items: NavItem[]
}

/**
 * Ten items in one flat list is something to read; four groups is something to
 * recognise. The grouping follows the investigation - what is broken, what
 * changed, what does the system think, and configuration - rather than the
 * data model.
 */
/**
 * Only pages that do something.
 *
 * The sidebar previously listed ten items, nine of which rendered a
 * placeholder. A visitor clicking around therefore hit a dead end far more
 * often than not, which makes a product that works read as one that is broken -
 * an empty room is worse than a smaller building. The routes still exist for
 * anyone holding a link; nothing advertises them until they are real.
 *
 * Diagnosing a log comes first because it is the one thing someone arriving
 * here can do immediately, without having connected anything.
 */
export const NAV_GROUPS: NavGroup[] = [
  {
    label: 'Diagnose',
    items: [{ to: '/', label: 'Diagnose a log', icon: Stethoscope }],
  },
  {
    label: 'Monitor',
    items: [
      { to: '/overview', label: 'Overview', icon: LayoutDashboard },
      { to: '/incidents', label: 'Incidents', icon: Siren, badgeKey: 'activeIncidents' },
      { to: '/logs', label: 'Logs', icon: ScrollText },
    ],
  },
]

export const STATUS_ICON = Activity
