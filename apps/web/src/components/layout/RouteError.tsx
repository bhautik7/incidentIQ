import { useNavigate, useRouteError } from 'react-router'

import { Button } from '../ui/Button'
import { ErrorState } from '../ui/States'

/**
 * The boundary of last resort.
 *
 * A blank screen during an incident is worse than an error message, so this
 * always renders something actionable - and names the failure rather than
 * saying "something went wrong".
 */
export function RouteError({ notFound }: { notFound?: boolean }) {
  const error = useRouteError() as Error | undefined
  const navigate = useNavigate()

  if (notFound) {
    return (
      <ErrorState
        title="Page not found"
        description="That route does not exist. It may have been renamed, or the link may be truncated."
        onRetry={() => navigate('/')}
      />
    )
  }

  return (
    <div className="p-6">
      <ErrorState
        title="This page failed to render"
        description={error?.message ?? 'An unexpected error occurred while rendering the page.'}
        onRetry={() => window.location.reload()}
      />
      <div className="mt-3 flex justify-center">
        <Button variant="ghost" onClick={() => navigate('/')}>
          Back to overview
        </Button>
      </div>
    </div>
  )
}
