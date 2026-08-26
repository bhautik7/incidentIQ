import { QueryClientProvider } from '@tanstack/react-query'
import { RouterProvider } from 'react-router'

import { queryClient } from './queryClient'
import { router } from './router'
import { SessionProvider } from './session'

export function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <SessionProvider>
        <RouterProvider router={router} />
      </SessionProvider>
    </QueryClientProvider>
  )
}
