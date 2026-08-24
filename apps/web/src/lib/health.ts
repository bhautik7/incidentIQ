import { config } from '../config'

export type HealthCheck = {
  name: string
  status: string
  description?: string | null
  error?: string | null
  durationMs?: number
}

export type HealthReport = {
  status: string
  service?: string
  version?: string
  environment?: string
  totalDurationMs?: number
  checks?: HealthCheck[]
}

export type ServiceProbe = {
  key: string
  label: string
  baseUrl: string
  state: 'loading' | 'healthy' | 'unhealthy' | 'unreachable'
  report?: HealthReport
  error?: string
  latencyMs?: number
}

export const services: ReadonlyArray<Pick<ServiceProbe, 'key' | 'label' | 'baseUrl'>> = [
  { key: 'api', label: 'API', baseUrl: config.apiBaseUrl },
  { key: 'ingestion', label: 'Ingestion', baseUrl: config.ingestionBaseUrl },
  { key: 'event-processor', label: 'Event Processor', baseUrl: config.eventProcessorBaseUrl },
  { key: 'ai-analysis', label: 'AI Analysis', baseUrl: config.aiAnalysisBaseUrl },
]

/**
 * Reads a service's readiness endpoint. A non-200 response is still a useful
 * answer - it carries the report explaining which dependency is down - so only
 * a transport failure counts as "unreachable".
 */
export async function probe(
  service: Pick<ServiceProbe, 'key' | 'label' | 'baseUrl'>,
  signal?: AbortSignal,
): Promise<ServiceProbe> {
  const startedAt = performance.now()

  try {
    const response = await fetch(`${service.baseUrl}/health/ready`, { signal })
    const report = (await response.json()) as HealthReport

    return {
      ...service,
      state: report.status === 'Healthy' ? 'healthy' : 'unhealthy',
      report,
      latencyMs: Math.round(performance.now() - startedAt),
    }
  } catch (error) {
    return {
      ...service,
      state: 'unreachable',
      error: error instanceof Error ? error.message : String(error),
      latencyMs: Math.round(performance.now() - startedAt),
    }
  }
}
