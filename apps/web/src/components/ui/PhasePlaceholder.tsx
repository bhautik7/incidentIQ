import { Construction } from 'lucide-react'

import { EmptyState } from './States'

/**
 * Marks a page whose implementation phase has not been reached.
 *
 * Deliberately not a mock screen: a convincing fake makes it impossible to
 * tell what actually works, and every stakeholder who sees one assumes the
 * feature exists.
 */
export function PhasePlaceholder({ phase }: { phase: number }) {
  return (
    <EmptyState
      icon={<Construction size={20} aria-hidden />}
      title={`Scheduled for UI phase ${phase}`}
      description="The application shell, routing and design system are in place. This page is built in a later phase rather than stubbed with mock data."
    />
  )
}
