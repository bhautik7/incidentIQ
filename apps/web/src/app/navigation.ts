import {
  Activity,
  BarChart3,
  Bell,
  Boxes,
  Cpu,
  LayoutDashboard,
  type LucideIcon,
  Rocket,
  ScrollText,
  Settings,
  Siren,
  Stethoscope,
  Users,
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
export const NAV_GROUPS: NavGroup[] = [
  {
    label: 'Monitor',
    items: [
      { to: '/', label: 'Overview', icon: LayoutDashboard },
      { to: '/incidents', label: 'Incidents', icon: Siren, badgeKey: 'activeIncidents' },
      { to: '/services', label: 'Services', icon: Boxes },
      { to: '/logs', label: 'Logs', icon: ScrollText },
      { to: '/diagnose', label: 'Diagnose a log', icon: Stethoscope },
    ],
  },
  {
    label: 'Change',
    items: [{ to: '/deployments', label: 'Deployments', icon: Rocket }],
  },
  {
    label: 'Intelligence',
    items: [
      { to: '/ai-investigations', label: 'AI Investigations', icon: Cpu },
      { to: '/analytics', label: 'Analytics', icon: BarChart3 },
    ],
  },
  {
    label: 'Configure',
    items: [
      { to: '/alert-rules', label: 'Alert Rules', icon: Bell },
      { to: '/team', label: 'Team', icon: Users },
      { to: '/settings', label: 'Settings', icon: Settings },
    ],
  },
]

export const STATUS_ICON = Activity
