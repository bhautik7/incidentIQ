import { ArrowRight, FileUp, Stethoscope, Upload } from 'lucide-react'
import { useCallback, useEffect, useMemo, useRef, useState, type DragEvent } from 'react'
import { Link, useNavigate } from 'react-router'

import { ENVIRONMENTS, useSession } from '../app/session'
import { Button } from '../components/ui/Button'
import { PageHeader } from '../components/ui/PageHeader'
import { Select } from '../components/ui/Select'
import { ErrorState } from '../components/ui/States'
import { ParsedPreview } from '../features/diagnose/ParsedPreview'
import { parseLog } from '../features/diagnose/parseLog'
import { summarize } from '../features/diagnose/upload'
import { useDiagnose } from '../features/diagnose/useDiagnose'
import { cn } from '../lib/cn'
import { useServices } from '../lib/api/queries'

/** Beyond this the textarea stops being a text editor and starts being a hazard. */
const MAX_CHARS = 5_000_000

/**
 * Diagnose a log.
 *
 * Someone with a broken application pastes what it printed and is told what is
 * wrong with it. Three steps, and the middle one is the point: the parsed
 * result is shown *before* anything is sent, so the user can see a wall of text
 * turn into four grouped errors and check that it grouped them correctly.
 * Trust in the answer at the end is built here or not at all.
 *
 * The answer itself is the existing incident detail page, not a second results
 * screen. Everything worth showing about a diagnosis - probable cause,
 * confidence, deployment correlation, similar past incidents, the raw lines -
 * already lives there, and a parallel view of the same data is the one that
 * quietly stops matching.
 */
export default function DiagnosePage() {
  const navigate = useNavigate()
  const session = useSession()
  const services = useServices()
  const { state, start, reset } = useDiagnose()

  const [text, setText] = useState('')
  const [fileName, setFileName] = useState<string | null>(null)
  const [service, setService] = useState('')
  const [environment, setEnvironment] = useState<string>(session.environment)
  const [dragging, setDragging] = useState(false)
  const [readError, setReadError] = useState<string | null>(null)
  const fileInput = useRef<HTMLInputElement>(null)
  const answer = useRef<HTMLDivElement>(null)

  const events = useMemo(() => (text.trim() ? parseLog(text) : []), [text])

  const summary = useMemo(
    () => (events.length > 0 ? summarize(events, text.split(/\r?\n/).length) : null),
    [events, text],
  )

  const busy = state.stage === 'uploading' || state.stage === 'processing'
  const canSubmit = events.length > 0 && service.trim().length > 0 && !busy

  // A freshly opened incident has nothing more to say here than its own page
  // says better, so the user is taken there. An incident that was *already*
  // open is a different answer - it means somebody is likely working on this
  // already - and that sentence would be lost in a redirect, so it is shown.
  useEffect(() => {
    if (state.result?.status === 'opened' && state.result.incidentId) {
      void navigate(`/incidents/${state.result.incidentId}`)
      return
    }

    // The answer that does not redirect has to come and find the reader
    // instead. On a wide viewport it appears in the right-hand column, which
    // is not where someone who just pressed a button on the left is looking -
    // and an answer nobody sees is indistinguishable from no answer at all.
    if (state.result?.status === 'existing' || state.stage === 'error') {
      answer.current?.scrollIntoView({ behavior: 'smooth', block: 'nearest' })
    }
  }, [state.result, state.stage, navigate])

  const readFile = useCallback((file: File) => {
    setReadError(null)

    file
      .text()
      .then((contents) => {
        if (contents.length > MAX_CHARS) {
          setReadError(
            `${file.name} is ${(contents.length / 1_000_000).toFixed(1)} MB, which is more than this `
              + 'page will parse in the browser. Use scripts/upload-log.py for a file this size.',
          )
          return
        }

        setText(contents)
        setFileName(file.name)

        // A log file is almost always named after what produced it, which is a
        // better first guess at the service than an empty box.
        if (!service) {
          const guess = file.name.replace(/\.(log|txt|json|ndjson)$/i, '').trim()
          if (guess) setService(guess)
        }
      })
      .catch(() => setReadError(`${file.name} could not be read.`))
  }, [service])

  const onDrop = useCallback(
    (event: DragEvent<HTMLDivElement>) => {
      event.preventDefault()
      setDragging(false)

      const file = event.dataTransfer.files?.[0]
      if (file) readFile(file)
    },
    [readFile],
  )

  return (
    <>
      <PageHeader
        title="Diagnose a log"
        description="Paste a log or drop a file. It is parsed here, in this browser, and shown to you before anything is sent."
        actions={
          text ? (
            <Button
              onClick={() => {
                setText('')
                setFileName(null)
                setReadError(null)
                reset()
              }}
              disabled={busy}
            >
              Clear
            </Button>
          ) : undefined
        }
      />

      <div className="grid gap-3 xl:grid-cols-2">
        <section className="space-y-2">
          <div
            onDragOver={(event) => {
              event.preventDefault()
              setDragging(true)
            }}
            onDragLeave={() => setDragging(false)}
            onDrop={onDrop}
            className={cn(
              'rounded-panel border transition-quick',
              dragging ? 'border-accent bg-accent/5' : 'border-line bg-surface',
            )}
          >
            <div className="flex items-center justify-between gap-2 border-b border-line px-3 py-1.5">
              <h2 className="text-[11px] font-semibold uppercase tracking-[0.06em] text-ink-muted">
                {fileName ?? 'Log'}
              </h2>

              <div className="flex items-center gap-2">
                <span className="text-[11px] text-ink-subtle">
                  {events.length > 0
                    ? `${events.length} event(s) parsed`
                    : 'or drop a file anywhere here'}
                </span>

                <Button
                  size="sm"
                  icon={<FileUp size={11} aria-hidden />}
                  onClick={() => fileInput.current?.click()}
                  disabled={busy}
                >
                  Choose file
                </Button>

                <input
                  ref={fileInput}
                  type="file"
                  accept=".log,.txt,.json,.ndjson,text/plain,application/json"
                  className="hidden"
                  onChange={(event) => {
                    const file = event.target.files?.[0]
                    if (file) readFile(file)
                    event.target.value = ''
                  }}
                />
              </div>
            </div>

            <textarea
              value={text}
              onChange={(event) => {
                setText(event.target.value)
                setFileName(null)
              }}
              disabled={busy}
              spellCheck={false}
              aria-label="Log contents"
              placeholder={PLACEHOLDER}
              className="h-[26rem] w-full resize-y bg-transparent p-3 font-mono text-[11px] leading-relaxed text-ink outline-none placeholder:text-ink-subtle"
            />
          </div>

          {readError && <p className="text-[12px] text-sev-critical">{readError}</p>}

          <div className="flex flex-wrap items-end gap-2 rounded-panel border border-line bg-surface p-3">
            <label className="flex flex-col gap-1">
              <span className="text-[10px] uppercase tracking-[0.05em] text-ink-subtle">Service</span>
              <input
                value={service}
                onChange={(event) => setService(event.target.value)}
                list="diagnose-services"
                disabled={busy}
                // "payments-api" alone reads as a value that is already
                // filled in, and a required field that looks answered is how a
                // disabled button becomes "I clicked it and nothing happened".
                placeholder="e.g. payments-api"
                className="h-7 w-52 rounded-[4px] border border-line bg-raised px-2 font-mono text-[12px] text-ink outline-none transition-quick hover:border-line-strong focus:border-accent"
              />
              {/* Free text with suggestions rather than a picker: the first log
                  someone diagnoses is usually from a service the platform has
                  never seen, and a dropdown of known services would make that
                  impossible to express. */}
              <datalist id="diagnose-services">
                {(services.data ?? []).map((known) => (
                  <option key={known.key} value={known.key}>
                    {known.displayName}
                  </option>
                ))}
              </datalist>
            </label>

            <label className="flex flex-col gap-1">
              <span className="text-[10px] uppercase tracking-[0.05em] text-ink-subtle">
                Environment
              </span>
              <Select
                label="Environment"
                value={environment}
                disabled={busy}
                onChange={(event) => setEnvironment(event.target.value)}
              >
                {ENVIRONMENTS.map((option) => (
                  <option key={option.key} value={option.key}>
                    {option.label}
                  </option>
                ))}
              </Select>
            </label>

            <div className="ml-auto flex items-center gap-2">
              {/* A disabled button that does nothing when clicked is
                  indistinguishable from a broken one. The requirement is said
                  out loud, next to the control that is refusing, rather than
                  hidden in a tooltip nobody hovers. */}
              {!busy && events.length > 0 && !service.trim() && (
                <span className="text-[11px] text-sev-high">
                  Name the service this log came from →
                </span>
              )}

              {busy && (
                <span className="text-[11px] text-ink-muted">
                  {state.stage === 'uploading'
                    ? `Uploading ${state.sent}/${state.total}…`
                    : 'Waiting for the pipeline…'}
                </span>
              )}

              <Button
                variant="primary"
                icon={<Stethoscope size={12} aria-hidden />}
                disabled={!canSubmit}
                onClick={() => {
                  if (!summary) return
                  void start(events, service.trim(), environment, summary.shiftMs, summary.windowStart)
                }}
                title={
                  service.trim()
                    ? undefined
                    : 'Name the service this log came from. Patterns are scoped to a service and an environment.'
                }
              >
                Diagnose
              </Button>
            </div>
          </div>

          {/* The environment is session state used by every other page, so an
              upload into one the user is not looking at would appear to have
              done nothing at all. */}
          {environment !== session.environment && (
            <p className="text-[11px] text-ink-subtle">
              You are uploading into {environment}, but the dashboard is currently showing{' '}
              {session.environment}. Switch environments in the top bar to see this afterwards.
            </p>
          )}
        </section>

        <section className="space-y-3">
          <div ref={answer} />

          {state.stage === 'error' && state.error ? (
            <ErrorState
              title="The diagnosis could not be completed"
              description={state.error.message}
              onRetry={() => reset()}
            />
          ) : null}

          {state.result?.status === 'existing' && state.result.incidentId ? (
            <AlreadyOpen
              incidentId={state.result.incidentId}
              title={state.result.title}
              message={state.result.message}
              fingerprint={state.result.fingerprint}
            />
          ) : null}

          {state.ingestion && state.ingestion.rejected > 0 && (
            <div className="rounded-panel border border-sev-high/30 bg-sev-high/5 p-3">
              <p className="text-[12px] text-ink">
                Ingestion rejected {state.ingestion.rejected} of {state.total} event(s). The rest
                were accepted and are being processed.
              </p>
              <ul className="mt-1.5 space-y-0.5">
                {state.ingestion.errors.map((error) => (
                  <li key={error.index} className="font-mono text-[11px] text-ink-muted">
                    line {error.index + 1}: {error.field} — {error.message}
                  </li>
                ))}
              </ul>
            </div>
          )}

          <div className="rounded-panel border border-line bg-surface">
            <h2 className="border-b border-line px-3 py-1.5 text-[11px] font-semibold uppercase tracking-[0.06em] text-ink-muted">
              What was found
            </h2>
            <div className="p-3">
              {summary ? (
                <ParsedPreview summary={summary} />
              ) : (
                <div className="flex flex-col items-center justify-center px-4 py-12 text-center">
                  <Upload size={20} className="mb-3 text-ink-subtle" aria-hidden />
                  <p className="text-[13px] font-medium text-ink">Nothing to read yet</p>
                  <p className="mt-1 max-w-sm text-[12px] text-ink-muted">
                    Paste a log on the left, or drop a file onto it. Stack traces are folded into the
                    line above them, and JSON, Serilog compact and plain text are all understood.
                  </p>
                </div>
              )}
            </div>
          </div>
        </section>
      </div>
    </>
  )
}

/**
 * The answer when the pattern already had an incident.
 *
 * Shown rather than redirected past, because "somebody already has this" is a
 * genuinely different answer from "here is your new incident" and is usually
 * the more useful of the two.
 */
function AlreadyOpen({
  incidentId,
  title,
  message,
  fingerprint,
}: {
  incidentId: string
  title: string | null
  message: string
  fingerprint: string | null
}) {
  return (
    <div className="rounded-panel border border-accent/40 bg-accent/5 p-3">
      <p className="text-[12px] text-ink">{message}</p>
      {title && <p className="mt-1 font-mono text-[12px] text-ink-muted">{title}</p>}

      <div className="mt-2 flex flex-wrap items-center gap-3">
        <Link
          to={`/incidents/${incidentId}`}
          className="inline-flex items-center gap-1 text-[12px] text-accent hover:underline"
        >
          Open the incident
          <ArrowRight size={12} aria-hidden />
        </Link>

        {fingerprint && (
          <Link
            to={`/logs?fingerprint=${fingerprint}`}
            className="text-[12px] text-ink-muted hover:text-ink hover:underline"
          >
            Show me the raw lines
          </Link>
        )}
      </div>
    </div>
  )
}

const PLACEHOLDER = `2026-08-28 09:14:02 ERROR Microsoft.Data.SqlClient.SqlException: Invalid column name 'Status'.
   at Microsoft.Data.SqlClient.SqlCommand.ExecuteReader()
   at Orders.Repository.GetPending()
2026-08-28 09:14:02 WARN  Retrying GetPending (attempt 2 of 3)
{"@t":"2026-08-28T09:14:03Z","@l":"Error","@m":"Cannot insert the value NULL into column 'Status'"}`
