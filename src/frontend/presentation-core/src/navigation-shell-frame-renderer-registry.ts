import {
  navigationShellKindLabels,
  type NavigationShellKind,
} from '@cohesive/presentation-contracts'

export type NavigationShellFrameRendererRegistry<TRenderer = unknown> = Readonly<
  Record<string, TRenderer>
>

export interface NavigationShellFrameRendererBinding<TRenderer = unknown> {
  readonly kind: NavigationShellKind | string | number
  readonly render: TRenderer
}

export function createNavigationShellFrameRendererRegistry<TRenderer>(
  bindings: readonly NavigationShellFrameRendererBinding<TRenderer>[],
): NavigationShellFrameRendererRegistry<TRenderer> {
  return Object.fromEntries(
    bindings.map((binding) => [
      createNavigationShellFrameRendererKey(binding.kind),
      binding.render,
    ]),
  )
}

export function createNavigationShellFrameRendererKey(
  kind: NavigationShellKind | string | number,
) {
  return normalizeNavigationShellKind(kind)
}

export function getNavigationShellFrameRendererRegistryKeys(
  registry: NavigationShellFrameRendererRegistry | null | undefined,
) {
  return Object.keys(registry ?? {})
}

export function hasNavigationShellFrameRendererBinding(
  keys: readonly string[],
  kind: NavigationShellKind | string | number,
) {
  return keys.includes(createNavigationShellFrameRendererKey(kind))
}

export function resolveNavigationShellFrameRenderer<TRenderer>(
  registry: NavigationShellFrameRendererRegistry<TRenderer> | null | undefined,
  kind: NavigationShellKind | string | number,
) {
  return registry?.[createNavigationShellFrameRendererKey(kind)] ?? null
}

function normalizeNavigationShellKind(value: unknown) {
  return normalizeGeneratedEnumToken(value, navigationShellKindLabels)
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
