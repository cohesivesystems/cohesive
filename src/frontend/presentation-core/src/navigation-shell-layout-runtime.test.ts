import { describe, expect, it } from 'vitest'

import {
  navigationNodeKinds,
  navigationRouteKinds,
  navigationShellKinds,
  navigationShellSlotKinds,
  type NavigationDefinition,
  type NavigationShellSlotDefinition,
} from '@cohesivesystems/presentation-contracts'
import {
  createNavigationShellFrameRendererKey,
} from './navigation-shell-frame-renderer-registry'
import {
  projectNavigationShellLayoutDiagnostics,
  resolveProjectedNavigationShellLayout,
} from './navigation-shell-layout-runtime'

describe('navigation shell layout runtime', () => {
  it('groups semantic shell slots while preserving adapter layout projections', () => {
    const navigation = createNavigation()
    const slots = [
      createSlot({
        id: 'brand',
        kind: navigationShellSlotKinds.brand,
        placement: 'top-left',
      }),
      createSlot({
        id: 'primary',
        kind: navigationShellSlotKinds.primaryNavigation,
        placement: 'top-center',
      }),
      createSlot({
        id: 'utility',
        kind: navigationShellSlotKinds.utilityActions,
        placement: 'top-right',
      }),
      createSlot({
        id: 'notices',
        kind: navigationShellSlotKinds.systemNotices,
        placement: 'below-navigation',
      }),
      createSlot({
        id: 'content',
        kind: navigationShellSlotKinds.routedContent,
        placement: 'main',
      }),
    ]

    const layout = resolveProjectedNavigationShellLayout({
      navigation,
      resolveFrameLayout: (layoutNavigation) => ({
        shellId: layoutNavigation.Shell.Id,
      }),
      resolveSlotLayout: (slot) => ({
        slotId: slot.Id,
      }),
      shellSlots: slots,
    })

    expect(layout.brandSlot?.Id).toBe('brand')
    expect(layout.primaryNavigationSlot?.Id).toBe('primary')
    expect(layout.routedContentSlot?.Id).toBe('content')
    expect(layout.utilityActionSlots.map((slot) => slot.Id)).toEqual(['utility'])
    expect(layout.systemNoticeSlots.map((slot) => slot.Id)).toEqual(['notices'])
    expect(layout.frame).toEqual({
      shellId: 'main-shell',
    })
    expect(layout.slotLayouts).toEqual({
      brand: { slotId: 'brand' },
      content: { slotId: 'content' },
      notices: { slotId: 'notices' },
      primary: { slotId: 'primary' },
      utility: { slotId: 'utility' },
    })
  })

  it('reports only missing frame renderer layout diagnostics in core', () => {
    const navigation = createNavigation()

    expect(projectNavigationShellLayoutDiagnostics({
      navigation,
      shellFrameRendererKeys: [
        createNavigationShellFrameRendererKey(navigationShellKinds.topNavigation),
      ],
    })).toEqual([])

    const diagnostics = projectNavigationShellLayoutDiagnostics({
      navigation,
      shellFrameRendererKeys: [],
    })

    expect(diagnostics.map((diagnostic) => diagnostic.id)).toEqual([
      'navigation-shell.main-shell.frame.missing-renderer',
    ])
    expect(diagnostics[0]).toMatchObject({
      category: 'missing-binding',
      details: {
        kind: navigationShellKinds.topNavigation,
        kindLabel: 'TopNavigation',
      },
      interpretation: {
        status: 'unbound',
        target: 'navigation-shell',
      },
    })
  })
})

function createNavigation(): NavigationDefinition {
  return {
    Actions: [],
    Contexts: [],
    Edges: [],
    Id: 'main-navigation',
    Label: 'Main navigation',
    Nodes: [
      {
        ActionId: null,
        ActiveRouteIds: [],
        Icon: null,
        Id: 'runs',
        IsPrimary: true,
        Kind: navigationNodeKinds.page,
        Label: 'Runs',
        RouteId: 'runs-route',
      },
    ],
    PageHosts: [],
    Routes: [
      {
        Id: 'runs-route',
        Kind: navigationRouteKinds.page,
        Label: 'Runs',
        PageHostId: 'runs-host',
        Parameters: [],
        PathTemplate: '/runs',
      },
    ],
    Shell: {
      Chrome: null,
      Design: null,
      Id: 'main-shell',
      Kind: navigationShellKinds.topNavigation,
      PrimaryNodeIds: ['runs'],
      Regions: [],
      Slots: null,
    },
  }
}

function createSlot({
  id,
  kind,
  placement,
}: {
  readonly id: string
  readonly kind: NavigationShellSlotDefinition['Kind']
  readonly placement: string
}): NavigationShellSlotDefinition {
  return {
    Annotations: [],
    Design: null,
    Id: id,
    Kind: kind,
    NodeIds: [],
    Placement: placement,
    RegionIds: [],
  }
}
