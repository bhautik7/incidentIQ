import { PageHeader } from '../components/ui/PageHeader'
import { PhasePlaceholder } from '../components/ui/PhasePlaceholder'

export default function LogsPage() {
  return (
    <>
      <PageHeader title="Log Explorer" description="Search and tail raw log events." />
      <PhasePlaceholder phase={5} />
    </>
  )
}
