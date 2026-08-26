import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from 'react'

export type EnvironmentKey = 'production' | 'staging' | 'development'
export type TimeRangeKey = '15m' | '1h' | '6h' | '24h' | '7d' | '30d'

export const ENVIRONMENTS: { key: EnvironmentKey; label: string }[] = [
  { key: 'production', label: 'Production' },
  { key: 'staging', label: 'Staging' },
  { key: 'development', label: 'Development' },
]

export const TIME_RANGES: { key: TimeRangeKey; label: string; minutes: number }[] = [
  { key: '15m', label: 'Last 15 minutes', minutes: 15 },
  { key: '1h', label: 'Last hour', minutes: 60 },
  { key: '6h', label: 'Last 6 hours', minutes: 360 },
  { key: '24h', label: 'Last 24 hours', minutes: 1440 },
  { key: '7d', label: 'Last 7 days', minutes: 10080 },
  { key: '30d', label: 'Last 30 days', minutes: 43200 },
]

type Session = {
  environment: EnvironmentKey
  timeRange: TimeRangeKey
  sidebarCollapsed: boolean
  setEnvironment: (value: EnvironmentKey) => void
  setTimeRange: (value: TimeRangeKey) => void
  toggleSidebar: () => void
}

const SessionContext = createContext<Session | null>(null)

const STORAGE_KEY = 'incidentiq.session'

function readStored<T>(field: string, fallback: T): T {
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY)
    return raw ? ((JSON.parse(raw)[field] as T) ?? fallback) : fallback
  } catch {
    // Private windows and blocked site data both throw here. A missing
    // preference is not worth failing a render over.
    return fallback
  }
}

/**
 * Environment and time range are *session* state, not page state.
 *
 * They apply across every page, so switching to Production on Incidents and
 * finding it reset on Services would make the product feel broken. They are
 * kept here and persisted, and pages read them rather than owning them.
 */
export function SessionProvider({ children }: { children: ReactNode }) {
  const [environment, setEnvironmentState] = useState<EnvironmentKey>(() =>
    readStored('environment', 'production'),
  )
  const [timeRange, setTimeRangeState] = useState<TimeRangeKey>(() => readStored('timeRange', '24h'))
  const [sidebarCollapsed, setCollapsed] = useState<boolean>(() => readStored('sidebarCollapsed', false))

  const persist = useCallback((patch: Record<string, unknown>) => {
    try {
      const raw = window.localStorage.getItem(STORAGE_KEY)
      const current = raw ? JSON.parse(raw) : {}
      window.localStorage.setItem(STORAGE_KEY, JSON.stringify({ ...current, ...patch }))
    } catch {
      // Preference not saved; the in-memory value still applies for this session.
    }
  }, [])

  const value = useMemo<Session>(
    () => ({
      environment,
      timeRange,
      sidebarCollapsed,
      setEnvironment: (next) => {
        setEnvironmentState(next)
        persist({ environment: next })
      },
      setTimeRange: (next) => {
        setTimeRangeState(next)
        persist({ timeRange: next })
      },
      toggleSidebar: () =>
        setCollapsed((previous) => {
          persist({ sidebarCollapsed: !previous })
          return !previous
        }),
    }),
    [environment, timeRange, sidebarCollapsed, persist],
  )

  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>
}

export function useSession(): Session {
  const context = useContext(SessionContext)
  if (!context) throw new Error('useSession must be used inside SessionProvider')
  return context
}
