import { QueryClientProvider } from '@tanstack/react-query'
import { RouterProvider } from 'react-router'

import { RealtimeProvider } from '../lib/realtime'
import { queryClient } from './queryClient'
import { router } from './router'
import { SessionProvider } from './session'

export function App() {
  return (
    <QueryClientProvider client={queryClient}>
      {/* Inside the query client, because the hub's whole job is invalidating
          its cache. Outside the router, because one connection has to survive
          navigation - reconnecting per page would drop events in the gap. */}
      <RealtimeProvider>
        <SessionProvider>
          <RouterProvider router={router} />
        </SessionProvider>
      </RealtimeProvider>
    </QueryClientProvider>
  )
}
