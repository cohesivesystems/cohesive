import {
  Activity,
  AlertCircle,
  ArrowLeft,
  Box,
  Braces,
  Check,
  CheckCircle2,
  ChevronLeft,
  ChevronRight,
  ChevronsLeft,
  Clock3,
  Columns2,
  Database,
  ExternalLink,
  FileCode2,
  GitBranch,
  Hammer,
  History,
  Home,
  ListChecks,
  ListTree,
  LoaderCircle,
  LogIn,
  MoreHorizontal,
  RefreshCw,
  RotateCcw,
  Save,
  Search,
  ShieldCheck,
  Square,
  SquareArrowOutUpRight,
  X,
  type LucideIcon,
} from 'lucide-react'
import type { ReactNode } from 'react'

import {
  createPresentationIconRegistry,
  defaultPresentationComponentSet,
  resolvePresentationIcon as resolveCorePresentationIcon,
  type PresentationIconModuleProjection,
  type PresentationIconRegistry as CorePresentationIconRegistry,
  type PresentationIconRenderContext,
  type PresentationIconResolution as CorePresentationIconResolution,
} from '@cohesive/presentation-core'

export {
  createPresentationIconRegistry,
}

export type {
  PresentationIconModuleProjection,
  PresentationIconRenderContext,
}

export type PresentationIconRegistry<TSubject = unknown> =
  CorePresentationIconRegistry<TSubject, ReactNode>

export type PresentationIconResolution<TSubject = unknown> =
  CorePresentationIconResolution<TSubject, ReactNode>

export function resolvePresentationIcon<TSubject = unknown>({
  componentSet = defaultPresentationComponentSet,
  icon,
  module,
  registry,
}: {
  readonly componentSet?: string
  readonly icon: string | null | undefined
  readonly module?: PresentationIconModuleProjection | null
  readonly registry: PresentationIconRegistry<TSubject> | null | undefined
}): PresentationIconResolution<TSubject> {
  return resolveCorePresentationIcon<TSubject, ReactNode>({
    componentSet,
    icon,
    module,
    registry,
  })
}

export function hasPresentationIconBinding<TSubject = unknown>({
  componentSet,
  icon,
  module,
  registry = standardLucidePresentationIconRegistry as PresentationIconRegistry<TSubject>,
}: {
  readonly componentSet?: string
  readonly icon: string | null | undefined
  readonly module?: PresentationIconModuleProjection | null
  readonly registry?: PresentationIconRegistry<TSubject> | null
}) {
  return Boolean(resolvePresentationIcon({
    componentSet,
    icon,
    module,
    registry,
  }).renderer)
}

export function renderPresentationIcon<TSubject = unknown>({
  className = 'size-3.5',
  componentSet,
  icon,
  module,
  registry = standardLucidePresentationIconRegistry as PresentationIconRegistry<TSubject>,
  subject,
}: {
  readonly className?: string
  readonly componentSet?: string
  readonly icon: string | null | undefined
  readonly module?: PresentationIconModuleProjection | null
  readonly registry?: PresentationIconRegistry<TSubject> | null
  readonly subject?: TSubject
}) {
  const resolution = resolvePresentationIcon({
    componentSet,
    icon,
    module,
    registry,
  })

  return resolution.renderer?.({
    className,
    icon: resolution.icon,
    subject,
  }) ?? null
}

export const standardLucidePresentationIconRegistry = createPresentationIconRegistry({
  byComponentKey: createLucideIconRendererMap('component-key'),
  byIconKey: createLucideIconRendererMap('icon-key'),
})

function createLucideIconRendererMap(
  keyKind: 'component-key' | 'icon-key',
) {
  const prefix = keyKind === 'component-key' ? 'lucide.' : ''
  return {
    [`${prefix}activity`]: createLucideIconRenderer(Activity),
    [`${prefix}alert-circle`]: createLucideIconRenderer(AlertCircle),
    [`${prefix}arrow-left`]: createLucideIconRenderer(ArrowLeft),
    [`${prefix}box`]: createLucideIconRenderer(Box),
    [`${prefix}braces`]: createLucideIconRenderer(Braces),
    [`${prefix}check`]: createLucideIconRenderer(Check),
    [`${prefix}check-circle-2`]: createLucideIconRenderer(CheckCircle2),
    [`${prefix}chevron-left`]: createLucideIconRenderer(ChevronLeft),
    [`${prefix}chevron-right`]: createLucideIconRenderer(ChevronRight),
    [`${prefix}chevrons-left`]: createLucideIconRenderer(ChevronsLeft),
    [`${prefix}clock-3`]: createLucideIconRenderer(Clock3),
    [`${prefix}columns-2`]: createLucideIconRenderer(Columns2),
    [`${prefix}database`]: createLucideIconRenderer(Database),
    [`${prefix}external-link`]: createLucideIconRenderer(ExternalLink),
    [`${prefix}file-code-2`]: createLucideIconRenderer(FileCode2),
    [`${prefix}git-branch`]: createLucideIconRenderer(GitBranch),
    [`${prefix}hammer`]: createLucideIconRenderer(Hammer),
    [`${prefix}history`]: createLucideIconRenderer(History),
    [`${prefix}home`]: createLucideIconRenderer(Home),
    [`${prefix}list-checks`]: createLucideIconRenderer(ListChecks),
    [`${prefix}list-tree`]: createLucideIconRenderer(ListTree),
    [`${prefix}loader-circle`]: createLucideIconRenderer(LoaderCircle),
    [`${prefix}log-in`]: createLucideIconRenderer(LogIn),
    [`${prefix}more-horizontal`]: createLucideIconRenderer(MoreHorizontal),
    [`${prefix}refresh-cw`]: createLucideIconRenderer(RefreshCw),
    [`${prefix}rotate-ccw`]: createLucideIconRenderer(RotateCcw),
    [`${prefix}save`]: createLucideIconRenderer(Save),
    [`${prefix}search`]: createLucideIconRenderer(Search),
    [`${prefix}shield-check`]: createLucideIconRenderer(ShieldCheck),
    [`${prefix}square`]: createLucideIconRenderer(Square),
    [`${prefix}square-arrow-out-up-right`]: createLucideIconRenderer(SquareArrowOutUpRight),
    [`${prefix}x`]: createLucideIconRenderer(X),
  }
}

function createLucideIconRenderer<TSubject = unknown>(Icon: LucideIcon) {
  return function renderLucideIcon({
    className = 'size-3.5',
  }: PresentationIconRenderContext<TSubject>) {
    return <Icon className={className} />
  }
}
