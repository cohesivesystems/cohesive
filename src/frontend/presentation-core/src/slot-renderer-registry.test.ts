import { describe, expect, it } from 'vitest'

import {
  collectionChromeSlotKinds,
  collectionChromeSlotPlacements,
  viewChromeSlotKinds,
  viewChromeSlotPlacements,
  type CollectionChromeSlotDefinition,
  type ViewChromeSlotDefinition,
} from '@cohesive/presentation-contracts'
import {
  createCollectionChromeSlotRendererRegistry,
  createViewChromeSlotRendererRegistry,
  getCollectionChromeSlotRendererCandidateKeys,
  getCollectionChromeSlotRendererRegistryKeys,
  getViewChromeSlotRendererCandidateKeys,
  getViewChromeSlotRendererRegistryKeys,
  hasCollectionChromeSlotRendererBinding,
  hasViewChromeSlotRendererBinding,
  resolveCollectionChromeSlotRenderer,
  resolveViewChromeSlotRenderer,
} from './index'

describe('slot renderer registry helpers', () => {
  it('creates and resolves collection chrome renderer keys with placement fallback', () => {
    const slot = {
      Kind: collectionChromeSlotKinds.pagination,
      Placement: collectionChromeSlotPlacements.footer,
    } as unknown as CollectionChromeSlotDefinition
    const registry = createCollectionChromeSlotRendererRegistry([
      {
        kind: collectionChromeSlotKinds.pagination,
        render: () => 'pagination-anywhere',
      },
    ])

    expect(getCollectionChromeSlotRendererCandidateKeys(slot)).toEqual([
      'pagination:footer',
      'pagination:*',
    ])
    expect(getCollectionChromeSlotRendererRegistryKeys(registry)).toEqual(['pagination:*'])
    expect(hasCollectionChromeSlotRendererBinding(['pagination:*'], slot)).toBe(true)
    expect(resolveCollectionChromeSlotRenderer(registry, slot)?.({})).toBe('pagination-anywhere')
  })

  it('creates and resolves view chrome renderer keys with exact placement preference', () => {
    const slot = {
      Kind: viewChromeSlotKinds.actions,
      Placement: viewChromeSlotPlacements.toolbar,
    } as unknown as ViewChromeSlotDefinition
    const registry = createViewChromeSlotRendererRegistry([
      {
        kind: viewChromeSlotKinds.actions,
        placement: viewChromeSlotPlacements.toolbar,
        render: () => 'toolbar-actions',
      },
      {
        kind: viewChromeSlotKinds.actions,
        render: () => 'any-actions',
      },
    ])

    expect(getViewChromeSlotRendererCandidateKeys(slot)).toEqual([
      'actions:toolbar',
      'actions:*',
    ])
    expect(getViewChromeSlotRendererRegistryKeys(registry)).toEqual([
      'actions:toolbar',
      'actions:*',
    ])
    expect(hasViewChromeSlotRendererBinding(['actions:toolbar'], slot)).toBe(true)
    expect(resolveViewChromeSlotRenderer(registry, slot)?.({})).toBe('toolbar-actions')
  })
})
