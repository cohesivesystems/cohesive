import type { ReactNode } from 'react'

export type PresentationValueBadgeTone = 'accent' | 'danger' | 'info' | 'neutral'

export interface PresentationValueBadgeProps {
  readonly children?: ReactNode
  readonly className?: string
  readonly label?: string
  readonly tone?: PresentationValueBadgeTone
}

const presentationValueBadgeToneClass = {
  accent: 'border-teal-700/15 bg-teal-50 text-teal-700',
  danger: 'border-red-700/15 bg-red-50 text-red-700',
  info: 'border-sky-700/15 bg-sky-50 text-sky-700',
  neutral: 'border-slate-950/10 bg-slate-100 text-slate-700',
} satisfies Record<PresentationValueBadgeTone, string>

export function PresentationValueBadge({
  children,
  className,
  label,
  tone = 'neutral',
}: PresentationValueBadgeProps) {
  return (
    <span
      className={cn(
        'inline-flex items-center rounded-md border px-2 py-0.5 text-xs font-medium',
        presentationValueBadgeToneClass[tone],
        className,
      )}
    >
      {children ?? label}
    </span>
  )
}

function cn(...values: readonly (false | null | string | undefined)[]) {
  return values.filter(Boolean).join(' ')
}
