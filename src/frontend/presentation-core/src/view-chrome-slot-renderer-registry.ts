import type {
  ViewChromeSlotDefinition,
  ViewChromeSlotKind,
  ViewChromeSlotPlacement,
} from './module'
import {
  viewChromeSlotKindLabels,
  viewChromeSlotPlacementLabels,
} from '@cohesivesystems/presentation-contracts'

export const viewChromeSlotAnyPlacement = '*'

export type ViewChromeSlotRenderer<TContext, TResult = unknown> = (
  context: TContext,
) => TResult

export type ViewChromeSlotRendererRegistry<TContext, TResult = unknown> = Readonly<
  Record<string, ViewChromeSlotRenderer<TContext, TResult>>
>

export interface ViewChromeSlotRendererBinding<TContext, TResult = unknown> {
  readonly kind: ViewChromeSlotKind | string | number
  readonly placement?: ViewChromeSlotPlacement | string | number | null
  readonly render: ViewChromeSlotRenderer<TContext, TResult>
}

export function createViewChromeSlotRendererRegistry<TContext, TResult = unknown>(
  bindings: readonly ViewChromeSlotRendererBinding<TContext, TResult>[],
): ViewChromeSlotRendererRegistry<TContext, TResult> {
  return Object.fromEntries(
    bindings.map((binding) => [
      createViewChromeSlotRendererKey(binding.kind, binding.placement),
      binding.render,
    ]),
  )
}

export function createViewChromeSlotRendererKey(
  kind: ViewChromeSlotKind | string | number,
  placement?: ViewChromeSlotPlacement | string | number | null,
) {
  return `${normalizeViewChromeSlotKind(kind)}:${placement == null
    ? viewChromeSlotAnyPlacement
    : normalizeViewChromeSlotPlacement(placement)}`
}

export function getViewChromeSlotRendererRegistryKeys<TContext, TResult = unknown>(
  registry: ViewChromeSlotRendererRegistry<TContext, TResult> | null | undefined,
) {
  return Object.keys(registry ?? {})
}

export function getViewChromeSlotRendererCandidateKeys(
  slot: ViewChromeSlotDefinition,
) {
  return [
    createViewChromeSlotRendererKey(slot.Kind, slot.Placement),
    createViewChromeSlotRendererKey(slot.Kind),
  ]
}

export function hasViewChromeSlotRendererBinding(
  keys: readonly string[],
  slot: ViewChromeSlotDefinition,
) {
  return getViewChromeSlotRendererCandidateKeys(slot).some((key) =>
    keys.includes(key))
}

export function resolveViewChromeSlotRenderer<TContext, TResult = unknown>(
  registry: ViewChromeSlotRendererRegistry<TContext, TResult> | null | undefined,
  slot: ViewChromeSlotDefinition,
) {
  const renderers = registry ?? {}
  for (const key of getViewChromeSlotRendererCandidateKeys(slot)) {
    const renderer = renderers[key]
    if (renderer) {
      return renderer
    }
  }

  return null
}

export function isViewChromeSlotPlacement(
  slot: ViewChromeSlotDefinition,
  placement: ViewChromeSlotPlacement | string | number,
) {
  return isViewChromeSlotPlacementValue(slot.Placement, placement)
}

export function isViewChromeSlotKind(
  slot: ViewChromeSlotDefinition,
  kind: ViewChromeSlotKind | string | number,
) {
  return normalizeViewChromeSlotKind(slot.Kind) ===
    normalizeViewChromeSlotKind(kind)
}

export function isViewChromeSlotPlacementValue(
  value: ViewChromeSlotPlacement | string | number | null | undefined,
  placement: ViewChromeSlotPlacement | string | number,
) {
  if (value == null) {
    return false
  }

  return normalizeViewChromeSlotPlacement(value) ===
    normalizeViewChromeSlotPlacement(placement)
}

function normalizeViewChromeSlotKind(value: unknown) {
  return normalizeGeneratedEnumToken(value, viewChromeSlotKindLabels)
}

function normalizeViewChromeSlotPlacement(value: unknown) {
  return normalizeGeneratedEnumToken(value, viewChromeSlotPlacementLabels)
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
