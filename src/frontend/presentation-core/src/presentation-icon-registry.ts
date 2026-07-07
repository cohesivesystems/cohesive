import type {
  PresentationBindingDefinition,
} from './module'
import {
  createPresentationEnumDiscriminator,
  resolvePresentationComponentBinding,
} from './target-bindings'
import {
  presentationBindingKinds,
  presentationTargetKinds,
} from '@cohesivesystems/presentation-contracts'

export interface PresentationIconRenderContext<TSubject = unknown> {
  readonly className?: string
  readonly icon: string
  readonly subject?: TSubject
}

export type PresentationIconRenderer<
  TSubject = unknown,
  TResult = unknown,
> = (context: PresentationIconRenderContext<TSubject>) => TResult

export interface PresentationIconRegistry<
  TSubject = unknown,
  TResult = unknown,
> {
  readonly byComponentKey?: Readonly<Record<string, PresentationIconRenderer<TSubject, TResult>>>
  readonly byComponentRole?: Readonly<Record<string, PresentationIconRenderer<TSubject, TResult>>>
  readonly byIconKey?: Readonly<Record<string, PresentationIconRenderer<TSubject, TResult>>>
}

export interface PresentationIconModuleProjection {
  readonly Targets?: readonly {
    readonly Bindings: readonly PresentationBindingDefinition[]
    readonly ComponentSet?: string | null
    readonly Target: string | number
  }[]
}

export interface PresentationIconResolution<
  TSubject = unknown,
  TResult = unknown,
> {
  readonly componentKey: string | null
  readonly componentRole: string | null
  readonly icon: string
  readonly renderer: PresentationIconRenderer<TSubject, TResult> | null
  readonly resolutionSource:
    | 'component-key'
    | 'component-role'
    | 'icon-key'
    | null
  readonly targetBindingSource: 'target-icon-binding' | null
}

export const defaultPresentationComponentSet = 'cohesive.presentation.default'

export function createPresentationIconRegistry<
  TSubject = unknown,
  TResult = unknown,
>(
  registry: PresentationIconRegistry<TSubject, TResult>,
) {
  return registry
}

export function resolvePresentationIcon<
  TSubject = unknown,
  TResult = unknown,
>({
  componentSet = defaultPresentationComponentSet,
  icon,
  module,
  registry,
}: {
  readonly componentSet?: string
  readonly icon: string | null | undefined
  readonly module?: PresentationIconModuleProjection | null
  readonly registry: PresentationIconRegistry<TSubject, TResult> | null | undefined
}): PresentationIconResolution<TSubject, TResult> {
  const iconKey = icon ?? ''
  const targetBinding = iconKey
    ? resolvePresentationIconTargetBinding({ componentSet, icon: iconKey, module })
    : null
  const componentRole = targetBinding?.componentRole ?? null
  const componentKey = targetBinding?.componentKey ?? null

  if (componentRole) {
    const renderer = registry?.byComponentRole?.[componentRole]
    if (renderer) {
      return {
        componentKey,
        componentRole,
        icon: iconKey,
        renderer,
        resolutionSource: 'component-role',
        targetBindingSource: targetBinding?.source ?? null,
      }
    }
  }

  if (componentKey) {
    const renderer = registry?.byComponentKey?.[componentKey]
    if (renderer) {
      return {
        componentKey,
        componentRole,
        icon: iconKey,
        renderer,
        resolutionSource: 'component-key',
        targetBindingSource: targetBinding?.source ?? null,
      }
    }
  }

  if (iconKey) {
    const renderer = registry?.byIconKey?.[iconKey]
    if (renderer) {
      return {
        componentKey,
        componentRole,
        icon: iconKey,
        renderer,
        resolutionSource: 'icon-key',
        targetBindingSource: targetBinding?.source ?? null,
      }
    }
  }

  return {
    componentKey,
    componentRole,
    icon: iconKey,
    renderer: null,
    resolutionSource: null,
    targetBindingSource: targetBinding?.source ?? null,
  }
}

function resolvePresentationIconTargetBinding({
  componentSet,
  icon,
  module,
}: {
  readonly componentSet: string
  readonly icon: string
  readonly module?: PresentationIconModuleProjection | null
}) {
  if (!module?.Targets) {
    return null
  }

  const resolvedBinding = resolvePresentationComponentBinding(
    { Targets: module.Targets },
    {
      bindingKind: createPresentationEnumDiscriminator(
        presentationBindingKinds,
        'icon',
        'Icon',
      ),
      componentSet,
      id: icon,
      targetKind: createPresentationEnumDiscriminator(
        presentationTargetKinds,
        'react',
        'React',
      ),
    },
  )

  if (!resolvedBinding.binding) {
    return null
  }

  return {
    componentKey: resolvedBinding.componentKey,
    componentRole: resolvedBinding.componentRole,
    source: 'target-icon-binding' as const,
  }
}
