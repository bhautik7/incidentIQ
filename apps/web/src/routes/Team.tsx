import { PageHeader } from '../components/ui/PageHeader'
import { PhasePlaceholder } from '../components/ui/PhasePlaceholder'

export default function TeamPage() {
  return (
    <>
      <PageHeader title="Team" description="People, roles and permissions." />
      <PhasePlaceholder phase={10} />
    </>
  )
}
