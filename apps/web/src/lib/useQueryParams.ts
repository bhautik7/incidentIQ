import { useCallback, useMemo } from 'react'
import { useSearchParams } from 'react-router'

/**
 * A typed view over the query string.
 *
 * The URL is the source of truth for filters rather than a copy of it. That is
 * what makes a filtered view pasteable into an incident channel, and it comes
 * with two properties worth keeping: the back button undoes a filter, and a
 * reload does not lose one.
 *
 * Values equal to their default are removed rather than written, so the common
 * view has a clean URL and only the filters someone actually changed appear in
 * the link they paste.
 */
export function useQueryParams<Shape extends Record<string, string>>(defaults: Shape) {
  const [searchParams, setSearchParams] = useSearchParams()

  const values = useMemo(() => {
    const result = { ...defaults }

    for (const key of Object.keys(defaults) as (keyof Shape)[]) {
      const raw = searchParams.get(key as string)
      if (raw !== null && raw !== '') {
        result[key] = raw as Shape[keyof Shape]
      }
    }

    return result
  }, [searchParams, defaults])

  const setValues = useCallback(
    (patch: Partial<Shape>, options?: { replace?: boolean }) => {
      setSearchParams(
        (current) => {
          const next = new URLSearchParams(current)

          for (const [key, value] of Object.entries(patch)) {
            if (value === undefined) continue

            if (value === '' || value === defaults[key]) next.delete(key)
            else next.set(key, String(value))
          }

          return next
        },
        // Typing in a search box would otherwise put one history entry per
        // keystroke between the user and the page they arrived from.
        { replace: options?.replace ?? false },
      )
    },
    [setSearchParams, defaults],
  )

  return [values, setValues] as const
}
