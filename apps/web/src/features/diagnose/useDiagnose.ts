import { useQueryClient } from '@tanstack/react-query'
import { useCallback, useRef, useState } from 'react'

import { ApiError, apiPost } from '../../lib/api/client'
import type { DiagnoseResult } from '../../types/api'
import type { ParsedEvent } from './parseLog'
import { sendToIngestion, toApiEvents, type IngestionOutcome } from './upload'

/** Where the request has got to, in the user's terms rather than the pipeline's. */
export type DiagnoseStage = 'idle' | 'uploading' | 'processing' | 'done' | 'error'

/**
 * How long to keep asking before giving up.
 *
 * The pipeline between "accepted" and "there is a pattern to open an incident
 * for" is Kafka, the processor and a batched write, which is normally two or
 * three seconds. Ninety is long enough that a cold consumer or a slow write is
 * not reported as a failure, and short enough that a genuinely stuck pipeline
 * is not left spinning forever - at which point the page says what was
 * uploaded, so nothing the user did is lost.
 */
const POLL_TIMEOUT_MS = 90_000
const POLL_INTERVAL_MS = 1_500

export interface DiagnoseState {
  stage: DiagnoseStage
  /** Events sent so far, for the progress line on a large file. */
  sent: number
  total: number
  ingestion: IngestionOutcome | null
  result: DiagnoseResult | null
  error: Error | null
}

const IDLE: DiagnoseState = {
  stage: 'idle', sent: 0, total: 0, ingestion: null, result: null, error: null,
}

/**
 * Upload, then wait for the pipeline to catch up, then open an incident.
 *
 * The waiting is the awkward part and cannot be designed away: ingestion
 * answers as soon as the batch is on Kafka, which is deliberately before
 * anything has been normalised or fingerprinted, so there is a window in which
 * the upload has succeeded and there is still nothing to diagnose. Rather than
 * guess a delay, the diagnose endpoint answers "pending" until it has patterns
 * to choose from and this polls it - so a slow pipeline shows as a slightly
 * longer wait rather than as an empty result.
 */
export function useDiagnose() {
  const [state, setState] = useState<DiagnoseState>(IDLE)
  const queryClient = useQueryClient()

  // Guards against a second submission while one is in flight - the button is
  // disabled, but a double-press on a slow machine can still land twice, and
  // this one would upload the log a second time.
  const running = useRef(false)

  const reset = useCallback(() => {
    running.current = false
    setState(IDLE)
  }, [])

  const start = useCallback(
    async (
      events: ParsedEvent[],
      service: string,
      environment: string,
      shiftMs: number,
      windowStart: Date,
    ) => {
      if (running.current) return null

      running.current = true
      const payload = toApiEvents(events, service, environment, shiftMs)

      setState({ ...IDLE, stage: 'uploading', total: payload.length })

      try {
        // A minute of slack before the oldest line the upload will produce.
        // Ingestion timestamps events from the file, so the rows this upload
        // creates are already in the past by the time the request completes -
        // a window starting "now" would exclude every one of them and the
        // diagnosis would wait forever for patterns that already exist.
        const since = new Date(Math.min(windowStart.getTime(), Date.now()) - 60_000)
        const ingestion = await sendToIngestion(payload, (sent, total) =>
          setState((current) => ({ ...current, sent, total })),
        )

        setState((current) => ({ ...current, stage: 'processing', ingestion }))

        if (ingestion.accepted === 0) {
          // Nothing reached the pipeline, so there is nothing to wait for. The
          // per-event errors ingestion returned say why, and are shown verbatim.
          throw new ApiError(
            'Ingestion accepted none of these events, so there is nothing to diagnose.',
            400,
          )
        }

        const deadline = Date.now() + POLL_TIMEOUT_MS
        let result: DiagnoseResult | null = null

        while (Date.now() < deadline) {
          result = await apiPost<DiagnoseResult>('/api/v1/diagnose', {
            service,
            environment,
            // Bounds the endpoint's search to the span this upload occupies,
            // rather than to whatever this service has been logging all day.
            since: since.toISOString(),
          })

          if (result.status !== 'pending') break

          await new Promise((resolve) => setTimeout(resolve, POLL_INTERVAL_MS))
        }

        if (!result || result.status === 'pending') {
          throw new ApiError(
            `The upload was accepted (${ingestion.accepted} events) but no error pattern had been `
              + 'processed after 90 seconds. The event processor may be behind or stuck; the logs are '
              + 'not lost and will appear once it catches up.',
            504,
          )
        }

        // An incident now exists, so every list that counts incidents is wrong
        // until it refetches.
        void queryClient.invalidateQueries({ queryKey: ['incidents'] })
        void queryClient.invalidateQueries({ queryKey: ['overview'] })

        setState((current) => ({ ...current, stage: 'done', result }))
        running.current = false

        return result
      } catch (error) {
        setState((current) => ({ ...current, stage: 'error', error: error as Error }))
        running.current = false

        return null
      }
    },
    [queryClient],
  )

  return { state, start, reset }
}
