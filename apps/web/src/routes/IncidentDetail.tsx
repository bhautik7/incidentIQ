import { PageHeader } from '../components/ui/PageHeader'
import { PhasePlaceholder } from '../components/ui/PhasePlaceholder'

export default function IncidentDetailPage() {
  return (
    <>
      <PageHeader title="Incident" description="Timeline, AI analysis, deployment correlation and error patterns." />
      <PhasePlaceholder phase={4} />
    </>
  )
}
