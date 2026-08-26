import { Suspense, useCallback, useEffect, useState } from 'react'
import { Outlet } from 'react-router'

import { SkeletonTable } from '../ui/Skeleton'
import { CommandPalette } from './CommandPalette'
import { Sidebar } from './Sidebar'
import { TopBar } from './TopBar'

/**
 * The persistent frame. Only the outlet changes between routes, so navigation
 * never costs a full re-render of the chrome and the sidebar keeps its scroll
 * position.
 */
export function AppShell() {
  const [paletteOpen, setPaletteOpen] = useState(false)
  const openPalette = useCallback(() => setPaletteOpen(true), [])

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'k') {
        event.preventDefault()
        setPaletteOpen((open) => !open)
      }
    }

    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [])

  return (
    <div className="flex h-screen overflow-hidden bg-canvas">
      <Sidebar />

      <div className="flex min-w-0 flex-1 flex-col">
        <TopBar onOpenSearch={openPalette} />

        <main className="flex-1 overflow-y-auto px-5 py-4">
          {/* Routes are lazy; the fallback is shaped like a table because most
              of them are one. */}
          <Suspense fallback={<SkeletonTable rows={10} />}>
            <Outlet />
          </Suspense>
        </main>
      </div>

      <CommandPalette open={paletteOpen} onClose={() => setPaletteOpen(false)} />
    </div>
  )
}
