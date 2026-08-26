import { ChevronDown } from 'lucide-react'
import type { SelectHTMLAttributes } from 'react'

import { cn } from '../../lib/cn'

/**
 * Native select, styled.
 *
 * A custom listbox would need focus trapping, typeahead and virtualisation to
 * match what the platform already does correctly - and would still be worse
 * with a keyboard. The chevron is decorative; the element underneath is real.
 */
export function Select({
  label,
  className,
  children,
  ...rest
}: SelectHTMLAttributes<HTMLSelectElement> & { label?: string }) {
  return (
    <div className="relative inline-flex items-center">
      {label && <span className="sr-only">{label}</span>}
      <select
        aria-label={label}
        className={cn(
          'h-7 appearance-none rounded-[4px] border border-line bg-raised',
          'pl-2 pr-6 text-[12px] text-ink transition-quick hover:border-line-strong',
          className,
        )}
        {...rest}
      >
        {children}
      </select>
      <ChevronDown
        size={12}
        aria-hidden
        className="pointer-events-none absolute right-1.5 text-ink-subtle"
      />
    </div>
  )
}
