import { describe, expect, it } from 'vitest'

import {
  navigationShellKinds,
  navigationShellSlotKinds,
  presentationBindingKinds,
  presentationTargetKinds,
  type NavigationShellRegionDefinition,
  type NavigationShellSlotDefinition,
  type PresentationBindingDefinition,
} from '@cohesive/presentation-contracts'
import {
  createNavigationShellFrameRendererRegistry,
  getNavigationShellFrameRendererRegistryKeys,
  hasNavigationShellFrameRendererBinding,
  resolveNavigationShellFrameRenderer,
} from './navigation-shell-frame-renderer-registry'
import {
  createNavigationShellRegionComponentRegistry,
  resolveNavigationShellRegionComponent,
  type NavigationShellRegionComponentModuleProjection,
} from './navigation-shell-region-component-registry'
import {
  createNavigationShellSlotRendererRegistry,
  getNavigationShellSlotRendererCandidateKeys,
  hasNavigationShellSlotRendererBinding,
  resolveNavigationShellSlotRenderer,
} from './navigation-shell-slot-renderer-registry'
import { createPresentationEnumDiscriminator } from './target-bindings'

describe('navigation shell renderer registries', () => {
  it('normalizes shell frame renderer keys across generated values and labels', () => {
    const registry = createNavigationShellFrameRendererRegistry([
      {
        kind: navigationShellKinds.topNavigation,
        render: 'top-frame-renderer',
      },
    ])
    const keys = getNavigationShellFrameRendererRegistryKeys(registry)

    expect(keys).toEqual(['topnavigation'])
    expect(hasNavigationShellFrameRendererBinding(keys, 'Top Navigation')).toBe(true)
    expect(resolveNavigationShellFrameRenderer(registry, 'TopNavigation')).toBe(
      'top-frame-renderer',
    )
  })

  it('prefers exact shell slot placement renderers before kind fallback renderers', () => {
    const slot = createSlot({
      kind: navigationShellSlotKinds.utilityActions,
      placement: 'header-right',
    })
    const registry = createNavigationShellSlotRendererRegistry([
      {
        kind: navigationShellSlotKinds.utilityActions,
        render: 'utility-fallback-renderer',
      },
      {
        kind: navigationShellSlotKinds.utilityActions,
        placement: 'Header Right',
        render: 'utility-header-renderer',
      },
    ])

    expect(getNavigationShellSlotRendererCandidateKeys(slot)).toEqual([
      'utilityactions:headerright',
      'utilityactions:*',
    ])
    expect(hasNavigationShellSlotRendererBinding(Object.keys(registry), slot)).toBe(true)
    expect(resolveNavigationShellSlotRenderer(registry, slot)).toBe(
      'utility-header-renderer',
    )
  })

  it('resolves navigation shell region components from target component roles', () => {
    const region = createRegion({ id: 'auth-region' })
    const registry = createNavigationShellRegionComponentRegistry({
      byComponentRole: {
        'auth-prompt': 'auth-renderer',
      },
    })

    const resolution = resolveNavigationShellRegionComponent({
      componentSet: 'sample-admin-ui',
      module: createModule({
        bindings: [
          createRegionComponentBinding({
            componentKey: 'auth-card',
            componentRole: 'auth-prompt',
            id: region.Id,
          }),
        ],
      }),
      region,
      registry,
      targetKind: createPresentationEnumDiscriminator(
        presentationTargetKinds,
        'react',
        'React',
      ),
    })

    expect(resolution).toMatchObject({
      componentKey: 'auth-card',
      componentRole: 'auth-prompt',
      renderer: 'auth-renderer',
      resolutionSource: 'component-role',
      targetBindingSource: 'target-region-binding',
    })
  })

  it('falls back to direct region component keys when no target role matches', () => {
    const region = createRegion({
      componentKey: 'custom-region',
      id: 'custom-region',
    })

    const resolution = resolveNavigationShellRegionComponent({
      module: createModule(),
      region,
      registry: {
        byComponentKey: {
          'custom-region': 'custom-renderer',
        },
      },
    })

    expect(resolution).toMatchObject({
      componentKey: 'custom-region',
      componentRole: null,
      renderer: 'custom-renderer',
      resolutionSource: 'component-key',
      targetBindingSource: null,
    })
  })
})

function createModule({
  bindings = [],
}: {
  readonly bindings?: readonly PresentationBindingDefinition[]
} = {}): NavigationShellRegionComponentModuleProjection {
  return {
    Targets: [
      {
        Bindings: bindings,
        ComponentSet: 'sample-admin-ui',
        Target: presentationTargetKinds.react,
      },
    ],
  }
}

function createRegion({
  componentKey = null,
  id,
}: {
  readonly componentKey?: string | null
  readonly id: string
}): NavigationShellRegionDefinition {
  return {
    ComponentKey: componentKey,
    Id: id,
    Kind: 0,
    Placement: 'header',
    SlotId: null,
    ViewId: null,
  }
}

function createSlot({
  kind,
  placement,
}: {
  readonly kind: NavigationShellSlotDefinition['Kind']
  readonly placement: string
}): NavigationShellSlotDefinition {
  return {
    Annotations: [],
    Id: 'utility',
    Kind: kind,
    NodeIds: [],
    Placement: placement,
    RegionIds: [],
  }
}

function createRegionComponentBinding({
  componentKey,
  componentRole,
  id,
}: {
  readonly componentKey: string
  readonly componentRole: string
  readonly id: string
}): PresentationBindingDefinition {
  return {
    ComponentKey: componentKey,
    ComponentRole: componentRole,
    Id: id,
    Kind: presentationBindingKinds.navigationShellRegionComponent,
    RouteId: null,
  } as unknown as PresentationBindingDefinition
}
