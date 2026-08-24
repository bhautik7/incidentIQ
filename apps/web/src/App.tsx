import { useCallback, useEffect, useState } from 'react'
import { probe, services, type ServiceProbe } from './lib/health'

const REFRESH_INTERVAL_MS = 10_000

const stateLabel: Record<ServiceProbe['state'], string> = {
  loading: 'Checking',
  healthy: 'Healthy',
  unhealthy: 'Not ready',
  unreachable: 'Unreachable',
}

export default function App() {
  const [probes, setProbes] = useState<ServiceProbe[]>(() =>
    services.map((service) => ({ ...service, state: 'loading' })),
  )
  const [lastChecked, setLastChecked] = useState<Date | null>(null)

  const refresh = useCallback(async (signal?: AbortSignal) => {
    const results = await Promise.all(services.map((service) => probe(service, signal)))
    if (signal?.aborted) return
    setProbes(results)
    setLastChecked(new Date())
  }, [])

  useEffect(() => {
    const controller = new AbortController()
    void refresh(controller.signal)

    const timer = setInterval(() => void refresh(controller.signal), REFRESH_INTERVAL_MS)
    return () => {
      controller.abort()
      clearInterval(timer)
    }
  }, [refresh])

  return (
    <main className="page">
      <header className="page__header">
        <div>
          <h1>IncidentIQ</h1>
          <p className="subtitle">Phase 2 &middot; platform foundation</p>
        </div>
        <div className="page__meta">
          <button type="button" onClick={() => void refresh()}>
            Refresh
          </button>
          <span className="muted">
            {lastChecked ? `Last checked ${lastChecked.toLocaleTimeString()}` : 'Checking…'}
          </span>
        </div>
      </header>

      <section className="grid">
        {probes.map((service) => (
          <article key={service.key} className={`card card--${service.state}`}>
            <div className="card__top">
              <h2>{service.label}</h2>
              <span className={`badge badge--${service.state}`}>{stateLabel[service.state]}</span>
            </div>

            <dl className="card__meta">
              <div>
                <dt>Endpoint</dt>
                <dd>
                  <code>{service.baseUrl}</code>
                </dd>
              </div>
              <div>
                <dt>Version</dt>
                <dd>{service.report?.version ?? '—'}</dd>
              </div>
              <div>
                <dt>Latency</dt>
                <dd>{service.latencyMs !== undefined ? `${service.latencyMs} ms` : '—'}</dd>
              </div>
            </dl>

            {service.error && <p className="card__error">{service.error}</p>}

            {service.report?.checks?.length ? (
              <ul className="checks">
                {service.report.checks.map((check) => (
                  <li key={check.name}>
                    <span className={`dot dot--${check.status === 'Healthy' ? 'ok' : 'bad'}`} />
                    <span className="checks__name">{check.name}</span>
                    <span className="checks__detail">{check.error ?? check.description ?? check.status}</span>
                  </li>
                ))}
              </ul>
            ) : null}
          </article>
        ))}
      </section>

      <footer className="footer muted">
        No business features yet. This page only verifies that every container is reachable and reporting
        its own readiness.
      </footer>
    </main>
  )
}
