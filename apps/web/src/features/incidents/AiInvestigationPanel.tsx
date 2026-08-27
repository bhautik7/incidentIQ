import { Sparkles, TriangleAlert } from 'lucide-react'

import { AIConfidence } from '../../components/ui/AIConfidence'
import { Tag } from '../../components/ui/Badge'
import { Button } from '../../components/ui/Button'
import { EmptyState } from '../../components/ui/States'
import type { IncidentAnalysis } from '../../types/api'

/**
 * What the model thinks happened, presented as evidence rather than a verdict.
 *
 * The ordering here is deliberate and is the whole argument of the panel:
 * confidence first, then the reasoning, then what it was reasoning *from*, then
 * what to do, then the warning. A tool that leads with a conclusion and buries
 * the evidence trains people to accept the conclusion; one that shows its
 * working lets them disagree with it.
 *
 * Below 40% the confidence component says so in words. That matters more here
 * than anywhere else in the product: this is the panel someone acts on at 03:00.
 */
export function AiInvestigationPanel({
  analysis,
  isAnalysing,
  canAnalyse,
  onAnalyse,
}: {
  analysis: IncidentAnalysis | null
  isAnalysing: boolean
  canAnalyse: boolean
  onAnalyse: () => void
}) {
  if (!analysis) {
    return (
      <EmptyState
        icon={<Sparkles size={20} aria-hidden />}
        title="No AI analysis yet"
        description="An investigation is requested automatically when an incident opens. If none arrived, the worker may be behind - you can ask for one."
        action={
          canAnalyse ? (
            <Button variant="primary" onClick={onAnalyse} disabled={isAnalysing}>
              {isAnalysing ? 'Requesting…' : 'Run AI analysis'}
            </Button>
          ) : undefined
        }
      />
    )
  }

  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <AIConfidence confidence={analysis.confidence} showLabel />

        <div className="flex items-center gap-1.5">
          {/* Which model, plainly. An analysis whose author is unstated is not
              auditable, and Phase 9 exists to audit these. */}
          <Tag mono>{analysis.modelName ?? analysis.modelProvider}</Tag>
          <time
            dateTime={analysis.createdAt}
            className="text-[10px] text-ink-subtle"
            title={new Date(analysis.createdAt).toLocaleString()}
          >
            {new Date(analysis.createdAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
          </time>
        </div>
      </div>

      {analysis.probableCause && (
        <section>
          <h3 className="mb-1 text-[11px] font-semibold uppercase tracking-[0.06em] text-ink-muted">
            Probable cause
          </h3>
          <p className="text-[12px] leading-relaxed text-ink">{analysis.probableCause}</p>
        </section>
      )}

      {analysis.summary && (
        <section>
          <h3 className="mb-1 text-[11px] font-semibold uppercase tracking-[0.06em] text-ink-muted">
            What was observed
          </h3>
          <p className="text-[12px] leading-relaxed text-ink-muted">{analysis.summary}</p>
        </section>
      )}

      {analysis.suggestedActions.length > 0 && (
        <section>
          <h3 className="mb-1.5 text-[11px] font-semibold uppercase tracking-[0.06em] text-ink-muted">
            Recommended actions
          </h3>
          {/* Numbered, because the model orders these by what is cheapest to
              check first, and that ordering is part of the recommendation. */}
          <ol className="space-y-1.5">
            {analysis.suggestedActions.map((action, index) => (
              <li key={index} className="flex gap-2 text-[12px] leading-snug text-ink">
                <span className="tabular mt-px shrink-0 text-[11px] text-ink-subtle">{index + 1}.</span>
                <span>{action}</span>
              </li>
            ))}
          </ol>
        </section>
      )}

      <p className="flex items-start gap-1.5 rounded-[4px] border border-sev-high/25 bg-sev-high/5 px-2 py-1.5 text-[11px] leading-snug text-ink-muted">
        <TriangleAlert size={12} aria-hidden className="mt-px shrink-0 text-sev-high" />
        AI-generated analysis. Verify before taking production action.
      </p>

      {canAnalyse && (
        <Button onClick={onAnalyse} disabled={isAnalysing} icon={<Sparkles size={12} aria-hidden />}>
          {isAnalysing ? 'Requesting…' : 'Re-run analysis'}
        </Button>
      )}
    </div>
  )
}
