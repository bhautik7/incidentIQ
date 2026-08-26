import { PageHeader } from '../components/ui/PageHeader'
import { PhasePlaceholder } from '../components/ui/PhasePlaceholder'

export default function DeploymentsPage() {
  return (
    <>
      <PageHeader title="Deployments" description="Releases, correlated with the incidents that followed them." />
      <PhasePlaceholder phase={7} />
    </>
  )
}
