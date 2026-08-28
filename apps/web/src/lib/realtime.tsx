import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from '@microsoft/signalr'
import { useQueryClient } from '@tanstack/react-query'
import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'

import { config } from '../config'

/**
 * The event names the hub sends. Must match RealtimeEvents in the API - a typo
 * here is a message that is simply never delivered, with no error anywhere.
 */
const EVENTS = {
  incidentDetected: 'incidentDetected',
  incidentChanged: 'incidentChanged',
  analysisCompleted: 'analysisCompleted',
} as const

export interface IncidentDetectedNotification {
  incidentId: string
  service: string
  environment: string
  severity: string
  title: string
  detectedAt: string
}

export interface AnalysisCompletedNotification {
  incidentId: string
  confidence: number | null
  completedAt: string
}

export type RealtimeStatus = 'connecting' | 'live' | 'reconnecting' | 'offline'

interface Realtime {
  status: RealtimeStatus
  /** Subscribe to newly detected incidents. Returns an unsubscribe function. */
  onIncidentDetected: (handler: (notification: IncidentDetectedNotification) => void) => () => void
}

const RealtimeContext = createContext<Realtime | null>(null)

/**
 * One hub connection for the whole application.
 *
 * A connection per page would reconnect on every navigation and drop events in
 * the gap. It lives at the shell instead, and pages subscribe to it.
 *
 * What arrives is treated as a *hint*, not as data. Every event invalidates the
 * relevant query and lets TanStack refetch through the same typed endpoints the
 * pages already use. Writing pushed payloads straight into the cache would mean
 * two code paths producing the same screen, and the one that only runs during a
 * live update is the one that silently rots.
 */
export function RealtimeProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient()
  const [status, setStatus] = useState<RealtimeStatus>('connecting')
  const [connection, setConnection] = useState<HubConnection | null>(null)

  useEffect(() => {
    // No token on the handshake. The API accepts one in the query string
    // because a browser cannot set a header on a WebSocket upgrade - but the
    // proxy this connection goes through can, and it does, so the client has
    // nothing to send and nothing to leak.
    const hub = new HubConnectionBuilder()
      .withUrl(`${config.apiBaseUrl}/hubs/incidents`, {
        // No cookies either, so the tighter simple-CORS request is enough.
        withCredentials: false,
      })
      // Backoff rather than a fixed interval: a hub that is down is usually
      // down because something larger is wrong, and a wall of reconnecting
      // dashboards is not what that moment needs.
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(LogLevel.Warning)
      .build()

    hub.onreconnecting(() => setStatus('reconnecting'))

    hub.onreconnected(() => {
      setStatus('live')

      // The gap is the point. Anything that happened while disconnected was
      // missed, so everything is refetched rather than assumed unchanged.
      void queryClient.invalidateQueries()
    })

    hub.onclose(() => setStatus('offline'))

    hub.start()
      .then(() => {
        setStatus('live')
        setConnection(hub)
      })
      .catch(() => {
        // Not fatal, and deliberately quiet. Every page still works by
        // fetching; the status indicator is what tells the user it is stale.
        setStatus('offline')
      })

    return () => {
      void hub.stop()
    }
  }, [queryClient])

  // Cache invalidation lives here so that a page which never subscribes still
  // stays fresh just by being open.
  useEffect(() => {
    if (!connection) return

    const refreshLists = () => {
      void queryClient.invalidateQueries({ queryKey: ['incidents'] })
      void queryClient.invalidateQueries({ queryKey: ['overview'] })
      void queryClient.invalidateQueries({ queryKey: ['services'] })
    }

    const onDetected = () => refreshLists()

    const onAnalysis = (notification: AnalysisCompletedNotification) => {
      // The incident whose page may be open on "analysing…", plus the lists
      // that show a confidence column.
      void queryClient.invalidateQueries({ queryKey: ['incident', notification.incidentId] })
      refreshLists()
    }

    connection.on(EVENTS.incidentDetected, onDetected)
    connection.on(EVENTS.incidentChanged, refreshLists)
    connection.on(EVENTS.analysisCompleted, onAnalysis)

    return () => {
      connection.off(EVENTS.incidentDetected, onDetected)
      connection.off(EVENTS.incidentChanged, refreshLists)
      connection.off(EVENTS.analysisCompleted, onAnalysis)
    }
  }, [connection, queryClient])

  const value = useMemo<Realtime>(
    () => ({
      status,
      onIncidentDetected: (handler) => {
        if (!connection) return () => {}

        connection.on(EVENTS.incidentDetected, handler)
        return () => connection.off(EVENTS.incidentDetected, handler)
      },
    }),
    [status, connection],
  )

  return <RealtimeContext.Provider value={value}>{children}</RealtimeContext.Provider>
}

export function useRealtime(): Realtime {
  const context = useContext(RealtimeContext)

  if (!context) {
    // A page that assumed live updates and silently got none would be worse
    // than a page that fails loudly during development.
    throw new Error('useRealtime must be used inside RealtimeProvider')
  }

  return context
}

/** True while the hub is delivering. Used to decide whether to poll instead. */
export function useIsLive(): boolean {
  return useRealtime().status === 'live'
}

export { HubConnectionState }
