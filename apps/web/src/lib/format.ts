/** Formatting shared by every table and card, so units never drift between pages. */

/** "12m", "1h 51m", "3d" - durations are compared, so they stay short. */
export function formatDuration(seconds: number): string {
  if (seconds < 60) return `${Math.floor(seconds)}s`
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m`

  if (seconds < 86400) {
    const hours = Math.floor(seconds / 3600)
    const minutes = Math.floor((seconds % 3600) / 60)
    return minutes ? `${hours}h ${minutes}m` : `${hours}h`
  }

  return `${Math.floor(seconds / 86400)}d`
}

export function formatRelative(iso: string): string {
  return formatDuration(Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000)) + ' ago'
}

/** 18,428 -> "18.4K". Column width matters more than the last three digits. */
export function formatCount(value: number): string {
  if (value < 1000) return String(value)
  if (value < 1_000_000) return `${(value / 1000).toFixed(value < 10_000 ? 1 : 0)}K`
  return `${(value / 1_000_000).toFixed(1)}M`
}

export function formatPercent(value: number, digits = 1): string {
  return `${(value * 100).toFixed(digits)}%`
}

export function formatClock(iso: string): string {
  return new Date(iso).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
}
