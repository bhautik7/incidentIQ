import { ENVIRONMENTS, TIME_RANGES, useSession } from '../app/session'
import { PageHeader } from '../components/ui/PageHeader'
import { PhasePlaceholder } from '../components/ui/PhasePlaceholder'

export default function OverviewPage() {
  const { environment, timeRange } = useSession()

  // Read from the session rather than hardcoded: a page headed "Production
  // Overview" while the selector says Staging is worse than no heading at all.
  const environmentLabel = ENVIRONMENTS.find((option) => option.key === environment)?.label ?? environment
  const rangeLabel = TIME_RANGES.find((option) => option.key === timeRange)?.label ?? timeRange

  return (
    <>
      <PageHeader
        title={`${environmentLabel} Overview`}
        description={`${rangeLabel.replace('Last ', 'Last ')} · real-time health across every monitored service`}
      />
      <PhasePlaceholder phase={2} />
    </>
  )
}
