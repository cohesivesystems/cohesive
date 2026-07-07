import { describe, expect, it } from 'vitest'

import {
  navigationNodeKinds,
  navigationRouteKinds,
  navigationShellKinds,
  navigationShellRegionKinds,
  navigationShellSlotKinds,
  type NavigationDefinition,
  type NavigationShellRegionDefinition,
  type NavigationShellSlotDefinition,
} from '@cohesivesystems/presentation-contracts'
import {
  createProjectedNavigationShellItems,
  findNavigationShellSlot,
  isProjectedNavigationShellItemActive,
  projectNavigationShellDiagnostics,
  resolveNavigationShellSlotRegions,
  resolveProjectedNavigationShellSlots,
} from './navigation-shell-runtime'

describe('navigation shell runtime', () => {
  it('synthesizes standard shell slots when a shell has no explicit slots', () => {
    const navigation = createNavigation({
      primaryNodeIds: ['runs'],
      regions: [
        createRegion({
          id: 'toolbar',
          placement: 'top-right',
        }),
      ],
    })

    const slots = resolveProjectedNavigationShellSlots(navigation)

    expect(slots.map((slot) => ({
      id: slot.Id,
      kind: slot.Kind,
      placement: slot.Placement,
      regionIds: slot.RegionIds,
    }))).toEqual([
      {
        id: 'brand',
        kind: navigationShellSlotKinds.brand,
        placement: 'top-left',
        regionIds: [],
      },
      {
        id: 'primary-navigation',
        kind: navigationShellSlotKinds.primaryNavigation,
        placement: 'top-center',
        regionIds: [],
      },
      {
        id: 'utility-actions',
        kind: navigationShellSlotKinds.utilityActions,
        placement: 'top-right',
        regionIds: ['toolbar'],
      },
      {
        id: 'system-notices',
        kind: navigationShellSlotKinds.systemNotices,
        placement: 'below-navigation',
        regionIds: [],
      },
      {
        id: 'routed-content',
        kind: navigationShellSlotKinds.routedContent,
        placement: 'main',
        regionIds: [],
      },
    ])
  })

  it('lowers shell navigation nodes to browser href items and active state', () => {
    const navigation = createNavigation({
      nodes: [
        createNode({ id: 'runs', routeId: 'runs-route' }),
        createNode({
          activeRouteIds: ['run-detail-route'],
          id: 'details',
          routeId: 'runs-route',
        }),
      ],
      primaryNodeIds: ['runs', 'missing', 'details'],
      routes: [
        createRoute({ id: 'runs-route', pathTemplate: '/runs' }),
        createRoute({ id: 'run-detail-route', pathTemplate: '/runs/{id}' }),
      ],
    })

    const items = createProjectedNavigationShellItems(navigation, null)

    expect(items.map((item) => ({
      href: item.href,
      id: item.node.Id,
      label: item.label,
    }))).toEqual([
      {
        href: '/runs',
        id: 'runs',
        label: 'Runs',
      },
      {
        href: '/runs',
        id: 'details',
        label: 'Details',
      },
    ])
    expect(isProjectedNavigationShellItemActive('/runs/42', items[1], navigation))
      .toBe(true)
  })

  it('resolves slot regions from explicit region ids, slot refs, and placement fallback', () => {
    const explicit = createRegion({ id: 'explicit', placement: 'top-right' })
    const bySlot = createRegion({
      id: 'by-slot',
      placement: 'elsewhere',
      slotId: 'utility',
    })
    const byPlacement = createRegion({
      id: 'by-placement',
      placement: 'below-navigation',
    })

    const explicitSlot = createSlot({
      id: 'utility',
      placement: 'top-right',
      regionIds: ['explicit'],
    })
    const placementSlot = createSlot({
      id: 'notice',
      placement: 'below-navigation',
    })
    const navigation = createNavigation({
      regions: [explicit, bySlot, byPlacement],
      slots: [explicitSlot, placementSlot],
    })

    expect(resolveNavigationShellSlotRegions(navigation, explicitSlot).map((region) => region.Id))
      .toEqual(['explicit', 'by-slot'])
    expect(resolveNavigationShellSlotRegions(navigation, placementSlot).map((region) => region.Id))
      .toEqual(['by-placement'])
  })

  it('reports shell projection diagnostics for missing slot bindings and unbound regions', () => {
    const navigation = createNavigation({
      regions: [
        createRegion({ id: 'empty-region', placement: 'top-right' }),
      ],
      slots: [
        createSlot({
          id: 'unsupported',
          kind: 99 as NavigationShellSlotDefinition['Kind'],
          placement: 'floating',
          regionIds: ['missing-region'],
        }),
      ],
    })

    const diagnostics = projectNavigationShellDiagnostics({
      navigation,
      renderShellIconBound: false,
      shellSlotRendererKeys: [],
      shellSlots: navigation.Shell.Slots ?? [],
    })

    expect(diagnostics.map((diagnostic) => diagnostic.id)).toEqual([
      'navigation-shell.main-shell.slot.unsupported.missing-renderer',
      'navigation-shell.main-shell.slot.unsupported.unsupported-kind',
      'navigation-shell.main-shell.slot.unsupported.unsupported-placement',
      'navigation-shell.main-shell.slot.unsupported.region.missing-region.missing-region',
      'navigation-shell.main-shell.region.empty-region.missing-target',
    ])
    expect(diagnostics.every((diagnostic) =>
      diagnostic.interpretation?.target !== 'sample-training-shell-region'))
      .toBe(true)
  })

  it('finds slots by generated kind or compatible label', () => {
    const slots = [
      createSlot({
        id: 'primary',
        kind: 'Primary Navigation' as unknown as NavigationShellSlotDefinition['Kind'],
        placement: 'top-center',
      }),
    ]

    expect(findNavigationShellSlot(
      slots,
      navigationShellSlotKinds.primaryNavigation,
      'PrimaryNavigation',
    )?.Id).toBe('primary')
  })
})

function createNavigation({
  nodes = [createNode({ id: 'runs', routeId: 'runs-route' })],
  primaryNodeIds = ['runs'],
  regions = [],
  routes = [createRoute({ id: 'runs-route', pathTemplate: '/runs' })],
  slots = null,
}: {
  readonly nodes?: NavigationDefinition['Nodes']
  readonly primaryNodeIds?: readonly string[]
  readonly regions?: readonly NavigationShellRegionDefinition[]
  readonly routes?: NavigationDefinition['Routes']
  readonly slots?: readonly NavigationShellSlotDefinition[] | null
} = {}): NavigationDefinition {
  return {
    Actions: [],
    Contexts: [],
    Edges: [],
    Id: 'main-navigation',
    Label: 'Main navigation',
    Nodes: [...nodes],
    PageHosts: [],
    Routes: [...routes],
    Shell: {
      Chrome: null,
      Design: null,
      Id: 'main-shell',
      Kind: navigationShellKinds.topNavigation,
      PrimaryNodeIds: [...primaryNodeIds],
      Regions: [...regions],
      Slots: slots ? [...slots] : slots,
    },
  } as unknown as NavigationDefinition
}

function createNode({
  activeRouteIds = [],
  id,
  label,
  routeId,
}: {
  readonly activeRouteIds?: readonly string[]
  readonly id: string
  readonly label?: string
  readonly routeId: string
}): NavigationDefinition['Nodes'][number] {
  return {
    ActionId: null,
    ActiveRouteIds: [...activeRouteIds],
    Icon: null,
    Id: id,
    IsPrimary: true,
    Kind: navigationNodeKinds.page,
    Label: label ?? titleCase(id),
    RouteId: routeId,
  }
}

function createRoute({
  id,
  pathTemplate,
}: {
  readonly id: string
  readonly pathTemplate: string
}): NavigationDefinition['Routes'][number] {
  return {
    Id: id,
    Kind: navigationRouteKinds.page,
    Label: titleCase(id),
    PageHostId: `${id}-host`,
    Parameters: pathTemplate.includes('{id}')
      ? [{ IsRequired: true, Name: 'id', Type: 'string' }]
      : [],
    PathTemplate: pathTemplate,
  }
}

function createRegion({
  componentKey = null,
  id,
  placement,
  slotId = null,
  viewId = null,
}: {
  readonly componentKey?: string | null
  readonly id: string
  readonly placement: string
  readonly slotId?: string | null
  readonly viewId?: string | null
}): NavigationShellRegionDefinition {
  return {
    ComponentKey: componentKey,
    Id: id,
    Kind: navigationShellRegionKinds.toolbarActions,
    Placement: placement,
    SlotId: slotId,
    ViewId: viewId,
  }
}

function createSlot({
  id,
  kind = navigationShellSlotKinds.utilityActions,
  nodeIds = [],
  placement,
  regionIds = [],
}: {
  readonly id: string
  readonly kind?: NavigationShellSlotDefinition['Kind']
  readonly nodeIds?: readonly string[]
  readonly placement: string
  readonly regionIds?: readonly string[]
}): NavigationShellSlotDefinition {
  return {
    Annotations: [],
    Design: null,
    Id: id,
    Kind: kind,
    NodeIds: [...nodeIds],
    Placement: placement,
    RegionIds: [...regionIds],
  }
}

function titleCase(value: string) {
  return value
    .split(/[-_\s]/)
    .filter(Boolean)
    .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
    .join(' ')
}
