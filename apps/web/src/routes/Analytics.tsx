import { PageHeader } from '../components/ui/PageHeader'
import { PhasePlaceholder } from '../components/ui/PhasePlaceholder'

export default function AnalyticsPage() {
  return (
    <>
      <PageHeader title="Analytics" description="Incident trends, MTTR and the least stable services." />
      <PhasePlaceholder phase={9} />
    </>
  )
}
