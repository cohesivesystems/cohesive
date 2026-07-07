import type { ReactNode } from 'react'
import type { PresentationBadgeVariant } from './presentation-component-groups'

import type {
  PresentationShadcnComponentSystem as PresentationComponentSystem,
} from './presentation-shadcn-component-system'
import type { PresentationDesignSystem } from '@cohesive/presentation-tailwind'
import type {
  ResolvedPresentationBadge,
} from '@cohesive/presentation-core'

export interface ProjectedPresentationBadgeItem
  extends Omit<ResolvedPresentationBadge, 'label'> {
  readonly className?: string
  readonly label: ReactNode
}

export interface ProjectedPresentationBadgeProps {
  readonly badge: ProjectedPresentationBadgeItem
  readonly className?: string
  readonly componentSystem: PresentationComponentSystem
  readonly designSystem: PresentationDesignSystem
  readonly variant?: PresentationBadgeVariant
}

export interface ProjectedPresentationBadgesProps {
  readonly badgeClassName?: string
  readonly badges: readonly ProjectedPresentationBadgeItem[]
  readonly className?: string
  readonly componentSystem: PresentationComponentSystem
  readonly designSystem: PresentationDesignSystem
  readonly variant?: PresentationBadgeVariant
}

/**
 * Standard badge interpreter for resolved presentation badge semantics.
 *
 * The badge model carries semantic tone and content only; this component is
 * where the target component system and design system decide how that becomes
 * concrete UI.
 */
export function ProjectedPresentationBadge({
  badge,
  className,
  componentSystem,
  designSystem,
  variant = 'outline',
}: ProjectedPresentationBadgeProps) {
  const BadgeComponent = componentSystem.badges.Badge

  return (
    <BadgeComponent
      className={cn(
        designSystem.classNames.badge.tone({ tone: badge.tone }),
        className,
        badge.className,
      )}
      variant={variant}
    >
      {badge.label}
    </BadgeComponent>
  )
}

export function ProjectedPresentationBadges({
  badgeClassName,
  badges,
  className,
  componentSystem,
  designSystem,
  variant = 'outline',
}: ProjectedPresentationBadgesProps) {
  if (badges.length === 0) {
    return null
  }

  return (
    <div className={cn('flex flex-wrap items-center gap-2', className)}>
      {badges.map((badge, index) => (
        <ProjectedPresentationBadge
          badge={badge}
          className={badgeClassName}
          componentSystem={componentSystem}
          designSystem={designSystem}
          key={badge.id ?? index}
          variant={variant}
        />
      ))}
    </div>
  )
}

function cn(...values: readonly (false | null | string | undefined)[]) {
  return values.filter(Boolean).join(' ')
}
