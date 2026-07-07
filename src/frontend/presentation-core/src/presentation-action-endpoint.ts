import {
  findPresentationAction,
  type PresentationBindingDefinition,
  type PresentationModuleDefinition,
} from './module'
import {
  createPresentationEnumDiscriminator,
  matchesPresentationEnum,
  type PresentationEnumDiscriminator,
} from './target-bindings'
import {
  presentationBindingKinds,
  presentationTargetKinds,
} from '@cohesivesystems/presentation-contracts'

interface PresentationTargetBindingProjection {
  readonly Bindings: readonly PresentationBindingDefinition[]
  readonly ComponentSet?: string | null
  readonly Target: string | number
}

export interface ResolvePresentationActionEndpointBindingOptions {
  readonly actionId: string
  readonly componentSet?: string | null
  readonly dataSourceId?: string | null
  readonly module: Pick<PresentationModuleDefinition, 'Actions'> & {
    readonly Targets?: readonly PresentationTargetBindingProjection[]
  } | null
  readonly targetKind?: PresentationEnumDiscriminator | null
}

/**
 * Resolves the concrete endpoint that realizes a semantic action for the
 * current projection target. Target bindings can specialize an action by data
 * source while the action definition remains semantic and reusable.
 */
export function resolvePresentationActionEndpointBinding({
  actionId,
  componentSet,
  dataSourceId,
  module,
  targetKind = createPresentationEnumDiscriminator(presentationTargetKinds, 'react', 'React'),
}: ResolvePresentationActionEndpointBindingOptions): PresentationBindingDefinition | null {
  if (!module) {
    return null
  }
  return (
    resolveActionEndpointTargetBinding({
      actionId,
      componentSet,
      dataSourceId,
      module,
      targetKind,
    }) ??
    resolveActionDefinitionEndpointBinding(module, actionId) ??
    null
  )
}

function resolveActionEndpointTargetBinding({
  actionId,
  componentSet,
  dataSourceId,
  module,
  targetKind,
}: ResolvePresentationActionEndpointBindingOptions): PresentationBindingDefinition | null {
  const targets = resolveCandidateTargets(module?.Targets ?? [], {
    componentSet,
    targetKind,
  })
  const bindings = targets
    .flatMap((target) => target.Bindings)
    .filter(
      (binding) =>
        matchesPresentationEnum(binding.Kind, actionEndpointBindingKind) &&
        binding.Id === actionId,
    )

  return (
    bindings.find((binding) => dataSourceId && binding.DataSourceId === dataSourceId) ??
    bindings.find((binding) => !binding.DataSourceId) ??
    bindings[0] ??
    null
  )
}

function resolveActionDefinitionEndpointBinding(
  module: Pick<PresentationModuleDefinition, 'Actions'>,
  actionId: string,
): PresentationBindingDefinition | null {
  const binding = findPresentationAction(module, actionId)?.Binding ?? null
  return binding && isEndpointBinding(binding) ? binding : null
}

function resolveCandidateTargets(
  targets: readonly PresentationTargetBindingProjection[],
  {
    componentSet,
    targetKind,
  }: Pick<ResolvePresentationActionEndpointBindingOptions, 'componentSet' | 'targetKind'>,
) {
  const filteredTargets = targets.filter((target) => {
    if (targetKind && !matchesPresentationEnum(target.Target, targetKind)) {
      return false
    }

    return !componentSet || target.ComponentSet === componentSet
  })

  return filteredTargets.length > 0 ? filteredTargets : targets
}

function isEndpointBinding(binding: PresentationBindingDefinition) {
  return (
    matchesPresentationEnum(binding.Kind, actionEndpointBindingKind) ||
    matchesPresentationEnum(binding.Kind, apiEndpointBindingKind)
  )
}

const actionEndpointBindingKind = createPresentationEnumDiscriminator(
  presentationBindingKinds,
  'actionEndpoint',
  'ActionEndpoint',
)

const apiEndpointBindingKind = createPresentationEnumDiscriminator(
  presentationBindingKinds,
  'apiEndpoint',
  'ApiEndpoint',
)
