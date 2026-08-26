import { PageHeader } from '../components/ui/PageHeader'
import { PhasePlaceholder } from '../components/ui/PhasePlaceholder'

export default function IncidentsPage() {
  return (
    <>
      <PageHeader title="Incidents" description="Every detected incident, filterable and linkable." />
      <PhasePlaceholder phase={3} />
    </>
  )
}
