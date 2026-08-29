import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { TIME_RANGES, type EnvironmentKey, type TimeRangeKey } from '../../app/session'
import type {
  CurrentSession,
  IncidentDetail,
  IncidentListItem,
  OrganizationMember,
  OverviewResponse,
  PagedResult,
  LogSearchResult,
  ServiceHealth,
  ServiceSummary,
} from '../../types/api'
import { useIsLive } from '../realtime'
import { apiGet, apiPost, toQuery } from './client'

/**
 * How often to poll, given whether the hub is delivering.
 *
 * Polling is the fallback, not the mechanism. While the hub is live the server
 * says when something changed and a timer adds nothing but load; when it is not,
 * a dashboard that quietly stopped updating during an outage would be worse
 * than a slightly noisy one, so the timer comes back.
 */
function useRefreshInterval(): number | false {
  return useIsLive() ? false : 15_000
}

export function windowMinutesFor(range: TimeRangeKey): number {
  return TIME_RANGES.find((option) => option.key === range)?.minutes ?? 1440
}

/**
 * Query keys, built in one place.
 *
 * Every key starts with its resource and ends with the parameters that change
 * the result, so invalidating a resource is one call and a stale response can
 * never be served for a different environment.
 */
export const queryKeys = {
  overview: (environment: EnvironmentKey, range: TimeRangeKey) =>
    ['overview', environment, range] as const,
  serviceHealth: (environment: EnvironmentKey, range: TimeRangeKey) =>
    ['services', 'health', environment, range] as const,
  incidents: (environment: EnvironmentKey, filters: object) =>
    ['incidents', environment, filters] as const,
  incident: (id: string) => ['incident', id] as const,
  services: () => ['services'] as const,
  logs: (environment: EnvironmentKey, filters: object) => ['logs', environment, filters] as const,
  members: () => ['members'] as const,
  session: () => ['session'] as const,
}

/**
 * The service list, used to populate filter pickers.
 *
 * Services are created by ingestion and change on the order of weeks, so this
 * is cached hard rather than polled - re-fetching it every 15 seconds
 * alongside the incident list would be pure noise.
 */
export function useServices() {
  return useQuery({
    queryKey: queryKeys.services(),
    queryFn: ({ signal }) => apiGet<ServiceSummary[]>('/api/v1/services', signal),
    staleTime: 5 * 60_000,
  })
}

export function useOverview(environment: EnvironmentKey, range: TimeRangeKey) {
  return useQuery({
    queryKey: queryKeys.overview(environment, range),
    queryFn: ({ signal }) =>
      apiGet<OverviewResponse>(
        `/api/v1/overview${toQuery({ windowMinutes: windowMinutesFor(range), environment })}`,
        signal,
      ),
    // The dashboard is watched during an outage; a minute-old count is worse
    // than a brief loading shimmer.
    refetchInterval: useRefreshInterval(),
  })
}

export function useServiceHealth(environment: EnvironmentKey, range: TimeRangeKey) {
  return useQuery({
    queryKey: queryKeys.serviceHealth(environment, range),
    queryFn: ({ signal }) =>
      apiGet<ServiceHealth[]>(
        `/api/v1/services/health${toQuery({ windowMinutes: windowMinutesFor(range), environment })}`,
        signal,
      ),
    refetchInterval: useRefreshInterval(),
  })
}

/** The parameters the incident list endpoint accepts, as the UI holds them. */
export interface IncidentQuery {
  status: string
  severity: string
  service: string
  search: string
  sort: string
  direction: string
  page: number
  pageSize: number
}

/**
 * The incident list.
 *
 * placeholderData keeps the previous page on screen while the next one loads,
 * so paging and re-sorting do not blink the table back to a skeleton. The
 * caller is told it is refetching and dims instead.
 */
export function useIncidents(environment: EnvironmentKey, query: IncidentQuery) {
  return useQuery({
    queryKey: queryKeys.incidents(environment, query),
    queryFn: ({ signal }) =>
      apiGet<PagedResult<IncidentListItem>>(
        `/api/v1/incidents${toQuery({
          environment,
          status: query.status,
          severity: query.severity,
          service: query.service,
          search: query.search,
          sort: query.sort,
          direction: query.direction,
          page: query.page,
          pageSize: query.pageSize,
        })}`,
        signal,
      ),
    placeholderData: (previous) => previous,
    refetchInterval: useRefreshInterval(),
  })
}

export function useActiveIncidents(environment: EnvironmentKey, pageSize = 8) {
  return useQuery({
    queryKey: queryKeys.incidents(environment, { status: 'active', pageSize }),
    queryFn: ({ signal }) =>
      apiGet<PagedResult<IncidentListItem>>(
        `/api/v1/incidents${toQuery({ status: 'active', environment, pageSize })}`,
        signal,
      ),
    refetchInterval: useRefreshInterval(),
  })
}

/**
 * One incident, with everything the detail page renders.
 *
 * Polled like the rest of the dashboard: an incident detail left open on a
 * second monitor during an outage is exactly the screen that must not go
 * stale, and it is where a newly completed AI analysis appears.
 */
export function useIncident(id: string) {
  return useQuery({
    queryKey: queryKeys.incident(id),
    queryFn: ({ signal }) => apiGet<IncidentDetail>(`/api/v1/incidents/${id}`, signal),
    refetchInterval: useRefreshInterval(),
  })
}

/**
 * Who this dashboard is acting as.
 *
 * Cached hard: it is fixed by the API key the container was started with and
 * cannot change without a restart. It is fetched at all because the sidebar
 * used to state an organization and a person as literals, and named someone
 * other than the user its actions were recorded against.
 */
export function useCurrentSession() {
  return useQuery({
    queryKey: queryKeys.session(),
    queryFn: ({ signal }) => apiGet<CurrentSession>('/api/v1/me', signal),
    staleTime: Infinity,
  })
}

/** Organization members, for the assignment picker. */
export function useMembers() {
  return useQuery({
    queryKey: queryKeys.members(),
    queryFn: ({ signal }) => apiGet<OrganizationMember[]>('/api/v1/users', signal),
    staleTime: 5 * 60_000,
  })
}

type ActionVariables =
  | { action: 'acknowledge' }
  | { action: 'resolve'; resolutionNotes?: string }
  | { action: 'ignore'; reason: string }
  | { action: 'reopen'; reason: string }
  | { action: 'assign'; userId: string }
  | { action: 'notes'; note: string }
  | { action: 'analyze' }

/**
 * Every incident action, as one mutation.
 *
 * One hook rather than seven because they share everything that matters: the
 * same URL shape, the same error handling, and the same invalidation. On
 * success the incident and any list that might contain it are both refetched -
 * resolving an incident changes its row in the queue as well as this page, and
 * leaving the list showing it as active is how a UI starts lying.
 *
 * Deliberately not optimistic. These transitions can be legitimately refused -
 * somebody else may have resolved it a second earlier - and showing the new
 * state before the server agrees would make a 409 look like a bug.
 */
export function useIncidentAction(id: string) {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: (variables: ActionVariables) => {
      const { action, ...body } = variables
      const path = `/api/v1/incidents/${id}/${action}`

      return action === 'acknowledge' || action === 'analyze'
        ? apiPost<unknown>(path)
        : apiPost<unknown>(path, body)
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: queryKeys.incident(id) })
      void queryClient.invalidateQueries({ queryKey: ['incidents'] })
      void queryClient.invalidateQueries({ queryKey: ['overview'] })
    },
  })
}

/** The parameters the log endpoint accepts, as the UI holds them. */
export interface LogQuery {
  service: string
  level: string
  search: string
  traceId: string
  fingerprint: string
  windowMinutes: number
}

const LOG_PAGE_SIZE = 100

/**
 * Log search, paged by cursor.
 *
 * An infinite query rather than a paged one, because a log explorer is read by
 * scrolling: pages accumulate into one list and the virtualiser renders a
 * window of it. Asking for "page 4" of a stream that grows while it is read is
 * not a question with a stable answer, which is why the API returns a cursor
 * and no total.
 */
export function useLogs(environment: EnvironmentKey, query: LogQuery) {
  return useInfiniteQuery({
    queryKey: queryKeys.logs(environment, query),
    initialPageParam: null as string | null,
    queryFn: ({ pageParam, signal }) =>
      apiGet<LogSearchResult>(
        `/api/v1/logs${toQuery({
          environment,
          service: query.service,
          level: query.level,
          search: query.search,
          traceId: query.traceId,
          fingerprint: query.fingerprint,
          windowMinutes: query.windowMinutes,
          pageSize: LOG_PAGE_SIZE,
          cursor: pageParam ?? undefined,
        })}`,
        signal,
      ),
    getNextPageParam: (lastPage) => lastPage.page.nextCursor,
    // Not polled. Logs are read by scrolling back through what happened, and a
    // refetch that reorders the list under a reader mid-scroll is worse than
    // stale data. Live tail will push new lines instead, once the hub exists.
    refetchInterval: false,
    staleTime: 30_000,
  })
}
