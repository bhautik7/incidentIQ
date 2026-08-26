import { PageHeader } from '../components/ui/PageHeader'
import { PhasePlaceholder } from '../components/ui/PhasePlaceholder'

export default function AiInvestigationsPage() {
  return (
    <>
      <PageHeader title="AI Investigations" description="Every analysis run, with the evidence it used." />
      <PhasePlaceholder phase={8} />
    </>
  )
}
