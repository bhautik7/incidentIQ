import { PageHeader } from '../components/ui/PageHeader'
import { PhasePlaceholder } from '../components/ui/PhasePlaceholder'

export default function SettingsPage() {
  return (
    <>
      <PageHeader title="Settings" description="Organization, environments, API keys and retention." />
      <PhasePlaceholder phase={10} />
    </>
  )
}
