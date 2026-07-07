import {
  navigationShellSlotKindLabels,
  type NavigationShellSlotDefinition,
  type NavigationShellSlotKind,
} from '@cohesive/presentation-contracts'

export const navigationShellSlotAnyPlacement = '*'

export type NavigationShellSlotRendererRegistry<TRenderer = unknown> = Readonly<
  Record<string, TRenderer>
>

export interface NavigationShellSlotRendererBinding<TRenderer = unknown> {
  readonly kind: NavigationShellSlotKind | string | number
  readonly placement?: string | number | null
  readonly render: TRenderer
}

export function createNavigationShellSlotRendererRegistry<TRenderer>(
  bindings: readonly NavigationShellSlotRendererBinding<TRenderer>[],
): NavigationShellSlotRendererRegistry<TRenderer> {
  return Object.fromEntries(
    bindings.map((binding) => [
      createNavigationShellSlotRendererKey(binding.kind, binding.placement),
      binding.render,
    ]),
  )
}

export function createNavigationShellSlotRendererKey(
  kind: NavigationShellSlotKind | string | number,
  placement?: string | number | null,
) {
  return `${normalizeNavigationShellSlotKind(kind)}:${placement == null
    ? navigationShellSlotAnyPlacement
    : normalizeNavigationShellSlotPlacement(placement)}`
}

export function getNavigationShellSlotRendererRegistryKeys(
  registry: NavigationShellSlotRendererRegistry | null | undefined,
) {
  return Object.keys(registry ?? {})
}

export function getNavigationShellSlotRendererCandidateKeys(
  slot: NavigationShellSlotDefinition,
) {
  return [
    createNavigationShellSlotRendererKey(slot.Kind, slot.Placement),
    createNavigationShellSlotRendererKey(slot.Kind),
  ]
}

export function hasNavigationShellSlotRendererBinding(
  keys: readonly string[],
  slot: NavigationShellSlotDefinition,
) {
  return getNavigationShellSlotRendererCandidateKeys(slot).some((key) =>
    keys.includes(key))
}

export function resolveNavigationShellSlotRenderer<TRenderer>(
  registry: NavigationShellSlotRendererRegistry<TRenderer> | null | undefined,
  slot: NavigationShellSlotDefinition,
) {
  const renderers = registry ?? {}
  for (const key of getNavigationShellSlotRendererCandidateKeys(slot)) {
    const renderer = renderers[key]
    if (renderer !== undefined && renderer !== null) {
      return renderer
    }
  }

  return null
}

function normalizeNavigationShellSlotKind(value: unknown) {
  return normalizeGeneratedEnumToken(value, navigationShellSlotKindLabels)
}

function normalizeNavigationShellSlotPlacement(value: unknown) {
  return String(value).replace(/[^a-z0-9]/gi, '').toLocaleLowerCase()
}

function normalizeGeneratedEnumToken(
  value: unknown,
  labels: Readonly<Record<number, string>>,
) {
  const label = typeof value === 'number'
    ? labels[value] ?? String(value)
    : String(value)

  return label.replace(/[^a-z0-9]/gi, '').toLocaleLowerCase()
}
