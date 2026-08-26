import { QueryClient } from '@tanstack/react-query'

/**
 * Server state lives here, not in React state.
 *
 * The defaults are tuned for an operational tool: data goes stale quickly
 * because an incident list five minutes old is misleading, and refetching on
 * window focus matters because the tab has usually been in the background
 * while the user was somewhere else.
 */
export const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 15_000,
      gcTime: 5 * 60_000,
      refetchOnWindowFocus: true,
      retry: (failureCount, error) => {
        // A 4xx will not become a 2xx by asking again; retrying it just
        // delays the error the user needs to see.
        const status = (error as { status?: number })?.status
        if (status && status >= 400 && status < 500) return false
        return failureCount < 2
      },
      retryDelay: (attempt) => Math.min(1000 * 2 ** attempt, 8000),
    },
  },
})
