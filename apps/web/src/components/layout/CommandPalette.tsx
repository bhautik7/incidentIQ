import { CornerDownLeft, Search } from 'lucide-react'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useNavigate } from 'react-router'

import { NAV_GROUPS } from '../../app/navigation'
import { cn } from '../../lib/cn'

type Command = { label: string; group: string; to: string }

/**
 * ⌘K navigation.
 *
 * Phase 1 covers routes only. Searching incident IDs, services, trace IDs and
 * fingerprints arrives with the pages that own that data - stubbing it now
 * would mean a search box that silently finds nothing, which is worse than one
 * that does not claim to.
 */
export function CommandPalette({ open, onClose }: { open: boolean; onClose: () => void }) {
  const navigate = useNavigate()
  const inputRef = useRef<HTMLInputElement>(null)
  const [query, setQuery] = useState('')
  const [activeIndex, setActiveIndex] = useState(0)

  const commands = useMemo<Command[]>(
    () =>
      NAV_GROUPS.flatMap((group) =>
        group.items.map((item) => ({ label: item.label, group: group.label, to: item.to })),
      ),
    [],
  )

  const matches = useMemo(() => {
    const term = query.trim().toLowerCase()
    if (!term) return commands
    return commands.filter((command) => command.label.toLowerCase().includes(term))
  }, [commands, query])

  useEffect(() => {
    if (open) {
      setQuery('')
      setActiveIndex(0)
      // Focus after paint, or the input is not in the document yet.
      requestAnimationFrame(() => inputRef.current?.focus())
    }
  }, [open])

  useEffect(() => {
    setActiveIndex(0)
  }, [query])

  if (!open) return null

  const run = (command: Command | undefined) => {
    if (!command) return
    navigate(command.to)
    onClose()
  }

  return (
    <div
      className="fixed inset-0 z-50 flex items-start justify-center bg-black/60 pt-[12vh]"
      onMouseDown={onClose}
      role="presentation"
    >
      <div
        role="dialog"
        aria-modal="true"
        aria-label="Command palette"
        className="w-full max-w-lg overflow-hidden rounded-panel border border-line-strong bg-overlay shadow-[0_16px_48px_rgba(0,0,0,0.5)]"
        onMouseDown={(event) => event.stopPropagation()}
      >
        <div className="flex items-center gap-2 border-b border-line px-3">
          <Search size={14} className="text-ink-subtle" aria-hidden />
          <input
            ref={inputRef}
            value={query}
            onChange={(event) => setQuery(event.target.value)}
            onKeyDown={(event) => {
              if (event.key === 'Escape') onClose()
              if (event.key === 'ArrowDown') {
                event.preventDefault()
                setActiveIndex((index) => Math.min(index + 1, matches.length - 1))
              }
              if (event.key === 'ArrowUp') {
                event.preventDefault()
                setActiveIndex((index) => Math.max(index - 1, 0))
              }
              if (event.key === 'Enter') run(matches[activeIndex])
            }}
            placeholder="Jump to…"
            aria-label="Search commands"
            aria-activedescendant={matches[activeIndex] ? `command-${activeIndex}` : undefined}
            className="h-10 flex-1 bg-transparent text-[13px] text-ink outline-none placeholder:text-ink-subtle"
          />
          <kbd className="rounded-[3px] border border-line px-1 font-mono text-[10px] text-ink-subtle">
            esc
          </kbd>
        </div>

        <ul className="max-h-72 overflow-y-auto py-1" role="listbox" aria-label="Commands">
          {matches.length === 0 && (
            <li className="px-3 py-6 text-center text-[12px] text-ink-subtle">
              Nothing matches “{query}”.
            </li>
          )}

          {matches.map((command, index) => (
            <li key={command.to} id={`command-${index}`} role="option" aria-selected={index === activeIndex}>
              <button
                type="button"
                onMouseEnter={() => setActiveIndex(index)}
                onClick={() => run(command)}
                className={cn(
                  'flex w-full items-center gap-2 px-3 py-1.5 text-left text-[12px]',
                  index === activeIndex ? 'bg-raised text-ink' : 'text-ink-muted',
                )}
              >
                <span className="flex-1">{command.label}</span>
                <span className="text-[10px] text-ink-subtle">{command.group}</span>
                {index === activeIndex && (
                  <CornerDownLeft size={11} className="text-ink-subtle" aria-hidden />
                )}
              </button>
            </li>
          ))}
        </ul>

        <p className="border-t border-line px-3 py-1.5 text-[10px] text-ink-subtle">
          Searching incidents, services and traces arrives with those pages.
        </p>
      </div>
    </div>
  )
}
