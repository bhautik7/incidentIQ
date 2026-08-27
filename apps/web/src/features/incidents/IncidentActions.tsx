import { AlertCircle, Ban, CheckCircle2, MessageSquarePlus, RotateCcw, Wrench } from 'lucide-react'
import { useState } from 'react'

import { Button } from '../../components/ui/Button'
import { Select } from '../../components/ui/Select'
import { cn } from '../../lib/cn'
import type { ApiError } from '../../lib/api/client'
import type { IncidentAction, IncidentOwner, OrganizationMember } from '../../types/api'

type Pending =
  | { action: 'acknowledge' }
  | { action: 'resolve'; resolutionNotes?: string }
  | { action: 'ignore'; reason: string }
  | { action: 'reopen'; reason: string }
  | { action: 'assign'; userId: string }
  | { action: 'notes'; note: string }
  | { action: 'analyze' }

/**
 * The things a person can do to this incident.
 *
 * Which buttons exist comes from the server's availableActions rather than
 * from status logic repeated here. The rules live in one place - the domain
 * service - and a second copy in the UI is how a dashboard ends up offering a
 * transition the API will refuse.
 *
 * Actions that destroy information or contradict an earlier decision - ignore,
 * reopen - require a reason, and resolve invites one. That prompt is not
 * ceremony: resolution notes are fed into the similarity search, so what you
 * type here is what the next person to hit this error gets shown.
 */
export function IncidentActions({
  availableActions,
  owner,
  members,
  isPending,
  error,
  onAct,
}: {
  availableActions: IncidentAction[]
  owner: IncidentOwner | null
  members: OrganizationMember[]
  isPending: boolean
  error: unknown
  onAct: (pending: Pending) => void
}) {
  const [prompt, setPrompt] = useState<'resolve' | 'ignore' | 'reopen' | 'notes' | null>(null)
  const [text, setText] = useState('')

  const can = (action: IncidentAction) => availableActions.includes(action)

  const openPrompt = (next: typeof prompt) => {
    setPrompt(next)
    setText('')
  }

  const submit = () => {
    const trimmed = text.trim()

    if (prompt === 'resolve') onAct({ action: 'resolve', resolutionNotes: trimmed || undefined })
    if (prompt === 'ignore' && trimmed) onAct({ action: 'ignore', reason: trimmed })
    if (prompt === 'reopen' && trimmed) onAct({ action: 'reopen', reason: trimmed })
    if (prompt === 'notes' && trimmed) onAct({ action: 'notes', note: trimmed })

    setPrompt(null)
    setText('')
  }

  // Ignore and reopen are refused without a reason; resolve is not.
  const reasonRequired = prompt === 'ignore' || prompt === 'reopen' || prompt === 'notes'
  const canSubmit = !reasonRequired || text.trim().length > 0

  return (
    <div className="space-y-2">
      <div className="flex flex-wrap items-center gap-2">
        {can('acknowledge') && (
          <Button
            variant="primary"
            disabled={isPending}
            onClick={() => onAct({ action: 'acknowledge' })}
            icon={<Wrench size={12} aria-hidden />}
          >
            Acknowledge
          </Button>
        )}

        {can('assign') && (
          <Select
            label="Assign to"
            value={owner?.userId ?? ''}
            disabled={isPending}
            onChange={(event) => {
              if (event.target.value) onAct({ action: 'assign', userId: event.target.value })
            }}
          >
            <option value="">Assign to…</option>
            {members.map((member) => (
              <option key={member.userId} value={member.userId}>
                {member.displayName}
              </option>
            ))}
          </Select>
        )}

        {can('resolve') && (
          <Button
            disabled={isPending}
            onClick={() => openPrompt('resolve')}
            icon={<CheckCircle2 size={12} aria-hidden />}
          >
            Resolve
          </Button>
        )}

        {can('reopen') && (
          <Button
            disabled={isPending}
            onClick={() => openPrompt('reopen')}
            icon={<RotateCcw size={12} aria-hidden />}
          >
            Reopen
          </Button>
        )}

        {can('ignore') && (
          <Button
            variant="danger"
            disabled={isPending}
            onClick={() => openPrompt('ignore')}
            icon={<Ban size={12} aria-hidden />}
          >
            Ignore
          </Button>
        )}

        {can('notes') && (
          <Button
            variant="ghost"
            disabled={isPending}
            onClick={() => openPrompt('notes')}
            icon={<MessageSquarePlus size={12} aria-hidden />}
          >
            Add note
          </Button>
        )}
      </div>

      {/* An inline panel rather than a modal. During an incident the context
          behind the dialog is the reason you are typing at all. */}
      {prompt && (
        <div className="rounded-panel border border-line bg-surface p-2.5">
          <label
            htmlFor="incident-action-text"
            className="mb-1 block text-[11px] font-medium text-ink"
          >
            {PROMPT_LABEL[prompt]}
          </label>

          <textarea
            id="incident-action-text"
            autoFocus
            rows={2}
            value={text}
            onChange={(event) => setText(event.target.value)}
            onKeyDown={(event) => {
              if (event.key === 'Escape') openPrompt(null)
              // Enter submits; newline needs a modifier, because these are one
              // or two sentences and reaching for a button breaks the flow.
              if (event.key === 'Enter' && !event.shiftKey && canSubmit) {
                event.preventDefault()
                submit()
              }
            }}
            placeholder={PROMPT_PLACEHOLDER[prompt]}
            className={cn(
              'w-full resize-y rounded-[4px] border border-line bg-raised px-2 py-1.5',
              'text-[12px] text-ink placeholder:text-ink-subtle',
              'transition-quick hover:border-line-strong',
            )}
          />

          <div className="mt-1.5 flex items-center gap-2">
            <Button variant="primary" size="sm" disabled={!canSubmit || isPending} onClick={submit}>
              {PROMPT_CONFIRM[prompt]}
            </Button>
            <Button variant="ghost" size="sm" onClick={() => openPrompt(null)}>
              Cancel
            </Button>
            {reasonRequired && !canSubmit && (
              <span className="text-[10px] text-ink-subtle">{PROMPT_REQUIREMENT[prompt]}</span>
            )}
          </div>
        </div>
      )}

      {/* A refusal is shown where the buttons are, not as a toast. 409 usually
          means somebody else got there first, and the message names both
          states - that is what the second person needs to read. */}
      {error != null && (
        <p
          role="alert"
          className="flex items-start gap-1.5 rounded-[4px] border border-sev-critical/25 bg-sev-critical/5 px-2 py-1.5 text-[11px] leading-snug text-ink"
        >
          <AlertCircle size={12} aria-hidden className="mt-px shrink-0 text-sev-critical" />
          {(error as ApiError)?.message ?? 'That action could not be completed.'}
        </p>
      )}
    </div>
  )
}

const PROMPT_LABEL = {
  resolve: 'What fixed it?',
  ignore: 'Why is this being ignored?',
  reopen: 'What was missed?',
  notes: 'Add a note to the timeline',
} as const

const PROMPT_PLACEHOLDER = {
  // Named as what it is used for, not as an empty formality: this text is
  // surfaced to whoever hits the same error next.
  resolve: 'Optional, and read by the next person to hit this error.',
  ignore: 'Required. Ignoring records a decision rather than a fix.',
  reopen: 'Required. This contradicts an earlier resolution.',
  notes: 'What you checked, what you found, what you tried.',
} as const

/** Why the field is not optional, in the words of the thing being done. */
const PROMPT_REQUIREMENT = {
  resolve: '',
  ignore: 'A reason is required.',
  reopen: 'A reason is required.',
  notes: 'A note needs some text.',
} as const

const PROMPT_CONFIRM = {
  resolve: 'Resolve incident',
  ignore: 'Ignore incident',
  reopen: 'Reopen incident',
  notes: 'Add note',
} as const
