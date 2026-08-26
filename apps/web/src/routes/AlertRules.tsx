import { PageHeader } from '../components/ui/PageHeader'
import { PhasePlaceholder } from '../components/ui/PhasePlaceholder'

export default function AlertRulesPage() {
  return (
    <>
      <PageHeader title="Alert Rules" description="Thresholds that open incidents." />
      <PhasePlaceholder phase={10} />
    </>
  )
}
