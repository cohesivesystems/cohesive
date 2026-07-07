import type { ReactNode } from 'react'

import {
  createViewChromeSlotRendererKey,
  createViewChromeSlotRendererRegistry as createCoreViewChromeSlotRendererRegistry,
  getViewChromeSlotRendererCandidateKeys,
  getViewChromeSlotRendererRegistryKeys as getCoreViewChromeSlotRendererRegistryKeys,
  hasViewChromeSlotRendererBinding,
  isViewChromeSlotKind,
  isViewChromeSlotPlacement,
  isViewChromeSlotPlacementValue,
  resolveViewChromeSlotRenderer as resolveCoreViewChromeSlotRenderer,
  viewChromeSlotAnyPlacement,
  type ViewChromeSlotRendererBinding as CoreViewChromeSlotRendererBinding,
  type ViewChromeSlotRendererRegistry as CoreViewChromeSlotRendererRegistry,
} from '@cohesive/presentation-core'
import type {
  ViewChromeSlotDefinition,
  ViewChromeSlotKind,
  ViewChromeSlotPlacement,
} from '@cohesive/presentation-contracts'

export {
  createViewChromeSlotRendererKey,
  getViewChromeSlotRendererCandidateKeys,
  hasViewChromeSlotRendererBinding,
  isViewChromeSlotKind,
  isViewChromeSlotPlacement,
  isViewChromeSlotPlacementValue,
  viewChromeSlotAnyPlacement,
}

export type ViewChromeSlotRenderer<TContext> = (
  context: TContext,
) => ReactNode

export type ViewChromeSlotRendererRegistry<TContext> =
  CoreViewChromeSlotRendererRegistry<TContext, ReactNode>

export type ViewChromeSlotRendererBinding<TContext> =
  CoreViewChromeSlotRendererBinding<TContext, ReactNode>

export function createViewChromeSlotRendererRegistry<TContext>(
  bindings: readonly ViewChromeSlotRendererBinding<TContext>[],
): ViewChromeSlotRendererRegistry<TContext> {
  return createCoreViewChromeSlotRendererRegistry<TContext, ReactNode>(bindings)
}

export function getViewChromeSlotRendererRegistryKeys<TContext>(
  registry: ViewChromeSlotRendererRegistry<TContext> | null | undefined,
) {
  return getCoreViewChromeSlotRendererRegistryKeys<TContext, ReactNode>(registry)
}

export function resolveViewChromeSlotRenderer<TContext>(
  registry: ViewChromeSlotRendererRegistry<TContext> | null | undefined,
  slot: ViewChromeSlotDefinition,
) {
  return resolveCoreViewChromeSlotRenderer<TContext, ReactNode>(registry, slot)
}

export type {
  ViewChromeSlotDefinition,
  ViewChromeSlotKind,
  ViewChromeSlotPlacement,
}
