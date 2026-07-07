import type { ReactNode } from 'react'

import {
  collectionChromeSlotAnyPlacement,
  createCollectionChromeSlotRendererKey,
  createCollectionChromeSlotRendererRegistry as createCoreCollectionChromeSlotRendererRegistry,
  getCollectionChromeSlotRendererCandidateKeys,
  getCollectionChromeSlotRendererRegistryKeys as getCoreCollectionChromeSlotRendererRegistryKeys,
  hasCollectionChromeSlotRendererBinding,
  isCollectionChromeSlotKind,
  isCollectionChromeSlotPlacement,
  isCollectionChromeSlotPlacementValue,
  resolveCollectionChromeSlotRenderer as resolveCoreCollectionChromeSlotRenderer,
  type CollectionChromeSlotRendererBinding as CoreCollectionChromeSlotRendererBinding,
  type CollectionChromeSlotRendererRegistry as CoreCollectionChromeSlotRendererRegistry,
} from '@cohesivesystems/presentation-core'
import type {
  CollectionChromeSlotDefinition,
  CollectionChromeSlotKind,
  CollectionChromeSlotPlacement,
} from '@cohesivesystems/presentation-contracts'

export {
  collectionChromeSlotAnyPlacement,
  createCollectionChromeSlotRendererKey,
  getCollectionChromeSlotRendererCandidateKeys,
  hasCollectionChromeSlotRendererBinding,
  isCollectionChromeSlotKind,
  isCollectionChromeSlotPlacement,
  isCollectionChromeSlotPlacementValue,
}

export type CollectionChromeSlotRenderer<TContext> = (
  context: TContext,
) => ReactNode

export type CollectionChromeSlotRendererRegistry<TContext> =
  CoreCollectionChromeSlotRendererRegistry<TContext, ReactNode>

export type CollectionChromeSlotRendererBinding<TContext> =
  CoreCollectionChromeSlotRendererBinding<TContext, ReactNode>

export function createCollectionChromeSlotRendererRegistry<TContext>(
  bindings: readonly CollectionChromeSlotRendererBinding<TContext>[],
): CollectionChromeSlotRendererRegistry<TContext> {
  return createCoreCollectionChromeSlotRendererRegistry<TContext, ReactNode>(bindings)
}

export function getCollectionChromeSlotRendererRegistryKeys<TContext>(
  registry: CollectionChromeSlotRendererRegistry<TContext> | null | undefined,
) {
  return getCoreCollectionChromeSlotRendererRegistryKeys<TContext, ReactNode>(registry)
}

export function resolveCollectionChromeSlotRenderer<TContext>(
  registry: CollectionChromeSlotRendererRegistry<TContext> | null | undefined,
  slot: CollectionChromeSlotDefinition,
) {
  return resolveCoreCollectionChromeSlotRenderer<TContext, ReactNode>(registry, slot)
}

export type {
  CollectionChromeSlotDefinition,
  CollectionChromeSlotKind,
  CollectionChromeSlotPlacement,
}
