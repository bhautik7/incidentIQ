import { clsx, type ClassValue } from 'clsx'
import { twMerge } from 'tailwind-merge'

/**
 * Merge class names, resolving Tailwind conflicts.
 *
 * Without the merge step a variant prop cannot override a base class - the
 * later class does not necessarily win in CSS, only in source order.
 */
export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs))
}
