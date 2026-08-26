import { useQuery } from '@tanstack/react-query'

import { TIME_RANGES, type EnvironmentKey, type TimeRangeKey } from '../../app/session'
import type {
  IncidentListItem,
  OverviewResponse,
  PagedResult,
  ServiceHealth,
  ServiceSummary,
} from '../../types/api'
import { apiGet, toQuery } from './client'

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
  services: () => ['services'] as const,
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
    refetchInterval: 15_000,
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
    refetchInterval: 15_000,
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
    refetchInterval: 15_000,
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
    refetchInterval: 15_000,
  })
}
