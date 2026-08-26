import type { ButtonHTMLAttributes, ReactNode } from 'react'

import { cn } from '../../lib/cn'

type Variant = 'primary' | 'default' | 'ghost' | 'danger'
type Size = 'sm' | 'md'

const VARIANTS: Record<Variant, string> = {
  primary: 'bg-accent text-white border-accent hover:bg-accent/90',
  default: 'bg-raised text-ink border-line hover:border-line-strong hover:bg-overlay',
  ghost: 'bg-transparent text-ink-muted border-transparent hover:text-ink hover:bg-raised',
  danger: 'bg-transparent text-sev-critical border-sev-critical/40 hover:bg-sev-critical/10',
}

const SIZES: Record<Size, string> = {
  sm: 'h-6 px-2 text-[11px] gap-1',
  md: 'h-7 px-2.5 text-[12px] gap-1.5',
}

export function Button({
  variant = 'default',
  size = 'md',
  icon,
  className,
  children,
  ...rest
}: ButtonHTMLAttributes<HTMLButtonElement> & {
  variant?: Variant
  size?: Size
  icon?: ReactNode
}) {
  return (
    <button
      type="button"
      className={cn(
        'inline-flex items-center justify-center rounded-[4px] border font-medium',
        'transition-quick disabled:cursor-not-allowed disabled:opacity-40',
        VARIANTS[variant],
        SIZES[size],
        className,
      )}
      {...rest}
    >
      {icon}
      {children}
    </button>
  )
}
