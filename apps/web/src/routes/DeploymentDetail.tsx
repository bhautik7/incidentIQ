import { PageHeader } from '../components/ui/PageHeader'
import { PhasePlaceholder } from '../components/ui/PhasePlaceholder'

export default function DeploymentDetailPage() {
  return (
    <>
      <PageHeader title="Deployment" description="Commit, timings and before/after health." />
      <PhasePlaceholder phase={8} />
    </>
  )
}
