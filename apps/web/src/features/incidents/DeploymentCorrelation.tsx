import { GitCommitHorizontal, Rocket } from 'lucide-react'

import { Tag } from '../../components/ui/Badge'
import { cn } from '../../lib/cn'
import { formatDuration } from '../../lib/format'
import type { IncidentDeployment } from '../../types/api'

/**
 * The release the detector suspects, and how well the timing actually supports
 * that suspicion.
 *
 * "v2.14 deployed 8 minutes before" is the highest-value sentence in the
 * product, which is exactly why the opposite case must not be dressed up to
 * look like it. `minutesBeforeIncident` is signed, and a negative value means
 * the deployment landed *after* the incident had already started - evidence
 * against causation, not for it. Rendering that as "-11 minutes before" would
 * be technically true and read as confirmation.
 */
export function DeploymentCorrelation({ deployment }: { deployment: IncidentDeployment }) {
  const minutes = deployment.minutesBeforeIncident
  const precedesIncident = minutes > 0
  const gap = formatDuration(Math.abs(minutes) * 60)

  return (
    <div className="space-y-2.5">
      <div className="flex flex-wrap items-center gap-2">
        <Rocket size={13} aria-hidden className="text-ink-muted" />
        <span className="font-mono text-[13px] text-ink">{deployment.version}</span>
        {deployment.commitSha && (
          <Tag mono>
            <GitCommitHorizontal size={10} aria-hidden className="mr-1" />
            {deployment.commitSha.slice(0, 8)}
          </Tag>
        )}
      </div>

      <dl className="grid grid-cols-2 gap-x-3 gap-y-1.5 text-[12px]">
        <Field label="Deployed">
          <time dateTime={deployment.deployedAt} title={new Date(deployment.deployedAt).toLocaleString()}>
            {new Date(deployment.deployedAt).toLocaleTimeString([], {
              hour: '2-digit',
              minute: '2-digit',
            })}
          </time>
        </Field>
        <Field label="Deployed by">{deployment.deployedBy ?? '—'}</Field>
      </dl>

      {/* The correlation stated as a sentence rather than a signed number,
          because the direction is the entire meaning. */}
      <p
        className={cn(
          'rounded-[4px] border px-2 py-1.5 text-[11px] leading-snug',
          precedesIncident
            ? 'border-sev-high/25 bg-sev-high/5 text-ink'
            : 'border-line bg-raised text-ink-muted',
        )}
      >
        {precedesIncident ? (
          <>
            Deployed <span className="font-medium text-ink">{gap} before</span> this incident began.
          </>
        ) : (
          <>
            Deployed <span className="font-medium text-ink">{gap} after</span> this incident began, so the
            timing does not support it as the cause.
          </>
        )}
      </p>
    </div>
  )
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="min-w-0">
      <dt className="text-[10px] uppercase tracking-[0.05em] text-ink-subtle">{label}</dt>
      <dd className="truncate text-ink-muted">{children}</dd>
    </div>
  )
}
