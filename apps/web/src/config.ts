export type PlatformConfig = {
  /**
   * Where the API lives, as seen by the browser.
   *
   * Empty by default, and that is the whole point: the page and everything it
   * talks to are served from one origin, and the proxy in front adds the API
   * key on the way through. Nothing here is a secret, because nothing here can
   * authenticate anybody.
   *
   * It was not always so - the key used to be delivered to the browser in this
   * object, which meant a tenant's full read/write credential was readable by
   * anyone who opened devtools.
   */
  apiBaseUrl: string
  ingestionBaseUrl: string
}

declare global {
  interface Window {
    __INCIDENTIQ_CONFIG__?: Partial<PlatformConfig>
  }
}

const runtime = window.__INCIDENTIQ_CONFIG__ ?? {}

/**
 * Resolution order: runtime config written by the container entrypoint, then
 * build-time Vite variables, then same-origin relative paths.
 *
 * The relative default is what `npm run dev` uses too - vite.config.ts proxies
 * the same three prefixes to the same services, so development and the
 * container disagree about ports and about nothing else.
 */
export const config: PlatformConfig = {
  apiBaseUrl: runtime.apiBaseUrl ?? import.meta.env.VITE_API_BASE_URL ?? '',
  ingestionBaseUrl: runtime.ingestionBaseUrl ?? import.meta.env.VITE_INGESTION_BASE_URL ?? '/ingest',
}
