import type {
  CollectionChromeSlotDefinition,
  CollectionChromeSlotKind,
  CollectionChromeSlotPlacement,
} from './module'
import {
  collectionChromeSlotKindLabels,
  collectionChromeSlotPlacementLabels,
} from '@cohesivesystems/presentation-contracts'

/**
 * Placement token used by collection chrome renderer keys that apply to any
 * placement for a slot kind.
 */
export const collectionChromeSlotAnyPlacement = '*'

/**
 * Renderer function for a projected collection chrome slot.
 *
 * @typeParam TContext Rendering context supplied by the host projection layer.
 * @typeParam TResult Render result produced by the host projection layer.
 */
export type CollectionChromeSlotRenderer<TContext, TResult = unknown> = (
  context: TContext,
) => TResult

/**
 * Lookup table from normalized collection chrome slot keys to renderer
 * functions.
 *
 * Keys are created with {@link createCollectionChromeSlotRendererKey} and encode
 * both the semantic slot kind and either a concrete placement or the any
 * placement token.
 */
export type CollectionChromeSlotRendererRegistry<TContext, TResult = unknown> = Readonly<
  Record<string, CollectionChromeSlotRenderer<TContext, TResult>>
>

/**
 * Declarative binding used to build a collection chrome slot renderer registry.
 *
 * Omit `placement` to register a kind-level fallback renderer that can satisfy
 * slots of the same kind in any placement.
 */
export interface CollectionChromeSlotRendererBinding<TContext, TResult = unknown> {
  /** Semantic collection chrome slot kind handled by the renderer. */
  readonly kind: CollectionChromeSlotKind | string | number

  /** Optional concrete placement handled by the renderer. */
  readonly placement?: CollectionChromeSlotPlacement | string | number | null

  /** Renderer invoked when the binding is selected for a slot. */
  readonly render: CollectionChromeSlotRenderer<TContext, TResult>
}

/**
 * Builds a normalized collection chrome slot renderer registry from declarative
 * bindings.
 *
 * Later bindings with the same normalized key overwrite earlier bindings.
 *
 * @param bindings Slot renderer bindings to index by kind and placement.
 * @returns Registry suitable for resolver and key-inspection helpers.
 */
export function createCollectionChromeSlotRendererRegistry<TContext, TResult = unknown>(
  bindings: readonly CollectionChromeSlotRendererBinding<TContext, TResult>[],
): CollectionChromeSlotRendererRegistry<TContext, TResult> {
  return Object.fromEntries(
    bindings.map((binding) => [
      createCollectionChromeSlotRendererKey(binding.kind, binding.placement),
      binding.render,
    ]),
  )
}

/**
 * Creates the canonical registry key for a collection chrome slot kind and
 * placement.
 *
 * Generated enum values, enum labels, and string values are normalized into the
 * same lowercase alphanumeric token so backend-generated contracts and
 * handwritten adapters can use the same registry.
 *
 * @param kind Collection chrome slot kind.
 * @param placement Optional slot placement. `null` and `undefined` become the
 * any-placement fallback token.
 */
export function createCollectionChromeSlotRendererKey(
  kind: CollectionChromeSlotKind | string | number,
  placement?: CollectionChromeSlotPlacement | string | number | null,
) {
  return `${normalizeCollectionChromeSlotKind(kind)}:${placement == null
    ? collectionChromeSlotAnyPlacement
    : normalizeCollectionChromeSlotPlacement(placement)}`
}

/**
 * Returns the normalized keys currently registered in a collection chrome slot
 * renderer registry.
 */
export function getCollectionChromeSlotRendererRegistryKeys<TContext, TResult = unknown>(
  registry: CollectionChromeSlotRendererRegistry<TContext, TResult> | null | undefined,
) {
  return Object.keys(registry ?? {})
}

/**
 * Checks whether a precomputed key set contains a renderer that could satisfy
 * the provided collection chrome slot.
 *
 * This uses the same candidate-key order as
 * {@link resolveCollectionChromeSlotRenderer}.
 */
export function hasCollectionChromeSlotRendererBinding(
  keys: readonly string[],
  slot: CollectionChromeSlotDefinition,
) {
  return getCollectionChromeSlotRendererCandidateKeys(slot).some((key) =>
    keys.includes(key))
}

/**
 * Resolves the renderer for a collection chrome slot.
 *
 * Exact kind-and-placement bindings are preferred. If no exact binding exists,
 * the resolver falls back to a kind-level any-placement binding.
 *
 * @param registry Registry to resolve from. `null` and `undefined` are treated
 * as empty registries.
 * @param slot Projected collection chrome slot definition.
 * @returns Matching renderer, or `null` when no binding can satisfy the slot.
 */
export function resolveCollectionChromeSlotRenderer<TContext, TResult = unknown>(
  registry: CollectionChromeSlotRendererRegistry<TContext, TResult> | null | undefined,
  slot: CollectionChromeSlotDefinition,
) {
  const renderers = registry ?? {}
  for (const key of getCollectionChromeSlotRendererCandidateKeys(slot)) {
    const renderer = renderers[key]
    if (renderer) {
      return renderer
    }
  }

  return null
}

/**
 * Checks whether a collection chrome slot is assigned to the provided
 * placement, using the same normalization rules as renderer keys.
 */
export function isCollectionChromeSlotPlacement(
  slot: CollectionChromeSlotDefinition,
  placement: CollectionChromeSlotPlacement | string | number,
) {
  return isCollectionChromeSlotPlacementValue(slot.Placement, placement)
}

/**
 * Checks whether a collection chrome slot has the provided semantic kind, using
 * the same normalization rules as renderer keys.
 */
export function isCollectionChromeSlotKind(
  slot: CollectionChromeSlotDefinition,
  kind: CollectionChromeSlotKind | string | number,
) {
  return normalizeCollectionChromeSlotKind(slot.Kind) ===
    normalizeCollectionChromeSlotKind(kind)
}

/**
 * Checks whether an optional placement value matches a concrete placement.
 *
 * Missing slot placement values never match, including against any-placement
 * fallback renderer bindings.
 */
export function isCollectionChromeSlotPlacementValue(
  value: CollectionChromeSlotPlacement | string | number | null | undefined,
  placement: CollectionChromeSlotPlacement | string | number,
) {
  if (value == null) {
    return false
  }

  return normalizeCollectionChromeSlotPlacement(value) ===
    normalizeCollectionChromeSlotPlacement(placement)
}

/**
 * Returns candidate renderer keys for a collection chrome slot in resolution
 * order.
 *
 * The exact kind-and-placement key is first, followed by the kind-level
 * any-placement fallback key.
 */
export function getCollectionChromeSlotRendererCandidateKeys(
  slot: CollectionChromeSlotDefinition,
) {
  return [
    createCollectionChromeSlotRendererKey(slot.Kind, slot.Placement),
    createCollectionChromeSlotRendererKey(slot.Kind),
  ]
}

/** Normalizes a slot kind into the registry-key token space. */
function normalizeCollectionChromeSlotKind(value: unknown) {
  return normalizeGeneratedEnumToken(value, collectionChromeSlotKindLabels)
}

/** Normalizes a slot placement into the registry-key token space. */
function normalizeCollectionChromeSlotPlacement(value: unknown) {
  return normalizeGeneratedEnumToken(value, collectionChromeSlotPlacementLabels)
}

/**
 * Converts generated enum numbers, enum label strings, and handwritten strings
 * into stable registry-key tokens.
 */
function normalizeGeneratedEnumToken(
  value: unknown,
  labels: Readonly<Record<number, string>>,
) {
  const label = typeof value === 'number'
    ? labels[value] ?? String(value)
    : String(value)
  return label.replace(/[^a-z0-9]/gi, '').toLocaleLowerCase()
}
