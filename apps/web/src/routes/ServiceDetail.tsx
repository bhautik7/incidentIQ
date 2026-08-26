import { PageHeader } from '../components/ui/PageHeader'
import { PhasePlaceholder } from '../components/ui/PhasePlaceholder'

export default function ServiceDetailPage() {
  return (
    <>
      <PageHeader title="Service" description="Volume, latency, incidents, deployments and anomalies." />
      <PhasePlaceholder phase={6} />
    </>
  )
}
