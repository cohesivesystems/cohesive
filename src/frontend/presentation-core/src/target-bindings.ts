import type {
  PresentationBindingDefinition,
} from './module'

export interface PresentationEnumDiscriminator {
  readonly label?: string
  readonly value: string | number
}

export interface FindPresentationComponentBindingOptions {
  readonly bindingKind: PresentationEnumDiscriminator
  readonly componentSet?: string | null
  readonly id: string
  readonly routeId?: string | null
  readonly targetKind?: PresentationEnumDiscriminator | null
}

export interface ResolvedPresentationComponentBinding<
  TBinding extends PresentationBindingDefinition = PresentationBindingDefinition,
> {
  readonly binding: TBinding | null
  readonly componentKey: string | null
  readonly componentRole: string | null
}

interface PresentationTargetBindingProjection {
  readonly Bindings: readonly PresentationBindingDefinition[]
  readonly ComponentSet?: string | null
  readonly Target: string | number
}

export function findPresentationComponentBinding<
  TBinding extends PresentationBindingDefinition,
>(
  module: { readonly Targets: readonly PresentationTargetBindingProjection[] },
  {
    bindingKind,
    componentSet,
    id,
    routeId,
    targetKind,
  }: FindPresentationComponentBindingOptions,
): TBinding | null {
  const targetMatches = module.Targets.filter((target) => {
    if (targetKind && !matchesPresentationEnum(target.Target, targetKind)) {
      return false
    }

    return !componentSet || target.ComponentSet === componentSet
  })
  const targets = targetMatches.length > 0 ? targetMatches : module.Targets
  const bindings = targets
    .flatMap((target) => target.Bindings)
    .filter(
      (binding) =>
        matchesPresentationEnum(binding.Kind, bindingKind) && binding.Id === id,
    )

  return (
    (bindings.find((binding) => routeId && binding.RouteId === routeId) as
      | TBinding
      | undefined) ??
    (bindings.find((binding) => !binding.RouteId) as TBinding | undefined) ??
    (bindings[0] as TBinding | undefined) ??
    null
  )
}

export function resolvePresentationComponentBinding<
  TBinding extends PresentationBindingDefinition = PresentationBindingDefinition,
>(
  module: { readonly Targets: readonly PresentationTargetBindingProjection[] },
  options: FindPresentationComponentBindingOptions,
): ResolvedPresentationComponentBinding<TBinding> {
  const binding = findPresentationComponentBinding<TBinding>(module, options)

  return {
    binding,
    componentKey: binding?.ComponentKey ?? null,
    componentRole: binding?.ComponentRole ?? null,
  }
}

export function matchesPresentationEnum(
  value: string | number,
  discriminator: PresentationEnumDiscriminator,
) {
  return value === discriminator.value || value === discriminator.label
}

export function createPresentationEnumDiscriminator<
  TValues extends Readonly<Record<string, string | number>>,
>(
  values: TValues,
  key: keyof TValues,
  label?: string,
): PresentationEnumDiscriminator {
  return { label, value: values[key] }
}

export function createPresentationComponentBinding<
  TComponentKey extends string,
>({
  componentKey,
  componentRole,
  id,
  kind,
  routeId,
}: {
  readonly componentKey?: TComponentKey
  readonly componentRole?: string
  readonly id: string
  readonly kind: PresentationBindingDefinition['Kind']
  readonly routeId?: string
}): PresentationBindingDefinition {
  return {
    ComponentKey: componentKey ?? null,
    ComponentRole: componentRole,
    Id: id,
    Kind: kind,
    RouteId: routeId,
  }
}
