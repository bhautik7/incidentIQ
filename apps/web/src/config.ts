export type PlatformConfig = {
  apiBaseUrl: string
  /**
   * API key used for read requests.
   *
   * Injected at container start rather than built in, but note that anything
   * delivered to a browser is readable by whoever loads the page. That is
   * acceptable for a single-tenant local dashboard and is NOT acceptable in
   * production, where this should become a short-lived token issued by a login
   * flow. Tracked as such; see the README.
   */
  apiKey: string
  ingestionBaseUrl: string
  eventProcessorBaseUrl: string
  aiAnalysisBaseUrl: string
}

declare global {
  interface Window {
    __INCIDENTIQ_CONFIG__?: Partial<PlatformConfig>
  }
}

const runtime = window.__INCIDENTIQ_CONFIG__ ?? {}

/**
 * Resolution order: runtime config injected by the container entrypoint, then
 * build-time Vite variables, then localhost defaults for `npm run dev`.
 * The browser runs on the host, so these are always published host ports -
 * never Docker service names, which the host cannot resolve.
 */
export const config: PlatformConfig = {
  apiBaseUrl: runtime.apiBaseUrl ?? import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:5080',
  apiKey: runtime.apiKey ?? import.meta.env.VITE_API_KEY ?? '',
  ingestionBaseUrl:
    runtime.ingestionBaseUrl ?? import.meta.env.VITE_INGESTION_BASE_URL ?? 'http://localhost:5081',
  eventProcessorBaseUrl:
    runtime.eventProcessorBaseUrl ??
    import.meta.env.VITE_EVENT_PROCESSOR_BASE_URL ??
    'http://localhost:5082',
  aiAnalysisBaseUrl:
    runtime.aiAnalysisBaseUrl ?? import.meta.env.VITE_AI_ANALYSIS_BASE_URL ?? 'http://localhost:5083',
}
