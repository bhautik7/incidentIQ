import { PageHeader } from '../components/ui/PageHeader'
import { PhasePlaceholder } from '../components/ui/PhasePlaceholder'

export default function ServicesPage() {
  return (
    <>
      <PageHeader title="Services" description="Service catalogue with health, latency and error rate." />
      <PhasePlaceholder phase={6} />
    </>
  )
}
