import type { ReactNode } from 'react'

import type {
  PresentationShadcnComponentSystem as PresentationComponentSystem,
} from './presentation-shadcn-component-system'
import type { ProjectedActivityState } from '@cohesive/presentation-core'

export interface ProjectedActivityStateBoundaryProps {
  readonly children: ReactNode
  readonly componentSystem?: PresentationComponentSystem
  readonly state: ProjectedActivityState
}

export function ProjectedActivityStateBoundary({
  children,
  componentSystem,
  state,
}: ProjectedActivityStateBoundaryProps) {
  if (state.kind === 'ready') {
    return children
  }

  return (
    <ProjectedStatusBlock
      componentSystem={componentSystem}
      label={state.label}
      tone={state.kind === 'error' ? 'error' : 'default'}
    />
  )
}

export interface ProjectedStatusBlockProps {
  readonly className?: string
  readonly componentSystem?: PresentationComponentSystem
  readonly label: ReactNode
  readonly tone?: 'default' | 'error'
}

export function ProjectedStatusBlock({
  className,
  componentSystem,
  label,
  tone = 'default',
}: ProjectedStatusBlockProps) {
  if (componentSystem) {
    return componentSystem.feedback.StatusBlock({ className, label, tone })
  }

  const toneClassName = tone === 'error'
    ? 'border-red-200 bg-red-50 text-red-800'
    : 'border-slate-200 bg-slate-50 text-slate-700'
  const resolvedClassName = [
    'rounded-md border px-3 py-2 text-sm',
    toneClassName,
    className,
  ].filter(Boolean).join(' ')

  return (
    <div className={resolvedClassName} role={tone === 'error' ? 'alert' : 'status'}>
      {label}
    </div>
  )
}
