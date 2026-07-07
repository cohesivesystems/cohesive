import {
  navigationShellSlotKindLabels,
  navigationShellSlotKinds,
  type NavigationDefinition,
  type NavigationNodeDefinition,
  type NavigationShellRegionDefinition,
  type NavigationShellSlotDefinition,
  type NavigationShellSlotKind,
} from '@cohesive/presentation-contracts'
import {
  createNavigationHref,
  getNavigationShellRegions,
  resolveNavigationRouteId,
} from './navigation'
import {
  hasNavigationShellSlotRendererBinding,
} from './navigation-shell-slot-renderer-registry'
import {
  createPresentationProjectionDiagnostic,
  type PresentationProjectionDiagnostic,
} from './presentation-projection-diagnostics'

export interface ProjectedNavigationShellItem {
  readonly href: string
  readonly label: string
  readonly node: NavigationNodeDefinition
}

/**
 * Pure navigation shell interpretation inputs used to report target binding
 * gaps without coupling diagnostics to a concrete React component.
 */
export interface NavigationShellDiagnosticsOptions {
  readonly isNodeIconSupported?: (icon: string) => boolean
  readonly isShellRegionComponentBound?: (
    componentKey: string,
    region: NavigationShellRegionDefinition,
  ) => boolean
  readonly isShellRegionComponentTargetBound?: (
    region: NavigationShellRegionDefinition,
  ) => boolean
  readonly isShellRegionSupported?: (region: NavigationShellRegionDefinition) => boolean
  readonly isShellRegionViewBound?: (
    viewId: string,
    region: NavigationShellRegionDefinition,
  ) => boolean
  readonly navigation: NavigationDefinition
  readonly renderShellIconBound: boolean
  readonly shellSlotRendererKeys?: readonly string[]
  readonly shellSlots: readonly NavigationShellSlotDefinition[]
  readonly supportedSlotPlacements?: readonly string[]
}

export const defaultNavigationShellSlotPlacements = [
  'top-left',
  'top-center',
  'top-right',
  'below-navigation',
  'main',
] as const

const supportedNavigationShellSlotKinds = new Set<NavigationShellSlotKind>([
  navigationShellSlotKinds.brand,
  navigationShellSlotKinds.primaryNavigation,
  navigationShellSlotKinds.utilityActions,
  navigationShellSlotKinds.systemNotices,
  navigationShellSlotKinds.routedContent,
  navigationShellSlotKinds.custom,
])

/**
 * Lowers a semantic shell navigation slot into routed link items.
 */
export function createProjectedNavigationShellItems(
  navigation: NavigationDefinition,
  slot: NavigationShellSlotDefinition | null,
): readonly ProjectedNavigationShellItem[] {
  const nodesById = new Map(navigation.Nodes.map((node) => [node.Id, node]))
  const nodeIds = slot?.NodeIds.length ? slot.NodeIds : navigation.Shell.PrimaryNodeIds

  return nodeIds.map((nodeId) => {
    const node = nodesById.get(nodeId)
    if (!node) {
      return null
    }

    const href = createNavigationHref(navigation, node.RouteId)
    if (!href) {
      return null
    }

    return {
      href,
      label: node.Label,
      node,
    }
  }).filter((item): item is ProjectedNavigationShellItem => item !== null)
}

export function findNavigationShellSlot(
  slots: readonly NavigationShellSlotDefinition[],
  numericKind: NavigationShellSlotKind,
  label: string,
) {
  return slots.find((slot) => isNavigationShellSlotKind(slot, numericKind, label)) ?? null
}

export function isNavigationShellSlotKind(
  slot: NavigationShellSlotDefinition,
  numericKind: NavigationShellSlotKind,
  label: string,
) {
  const normalizedKind = String(slot.Kind).replace(/[-_\s]/g, '').toLocaleLowerCase()
  const normalizedLabel = label.replace(/[-_\s]/g, '').toLocaleLowerCase()

  return slot.Kind === numericKind || normalizedKind === normalizedLabel
}

export function isProjectedNavigationShellItemActive(
  activePath: string,
  item: ProjectedNavigationShellItem,
  navigation: NavigationDefinition,
) {
  const activeRouteId = resolveNavigationRouteId(navigation, activePath)
  if (!activeRouteId) {
    return activePath === item.href
  }

  return activeRouteId === item.node.RouteId || item.node.ActiveRouteIds.includes(activeRouteId)
}

export function isSupportedNavigationShellSlot(slot: NavigationShellSlotDefinition) {
  return supportedNavigationShellSlotKinds.has(slot.Kind)
}

/**
 * Resolves slot regions through declared region ids, region SlotId links, and
 * finally legacy placement matching for shells that predate explicit slots.
 */
export function resolveNavigationShellSlotRegions(
  navigation: NavigationDefinition,
  slot: NavigationShellSlotDefinition,
) {
  const regionsById = new Map(navigation.Shell.Regions.map((region) => [region.Id, region]))
  const regionIds = new Set(slot.RegionIds)
  const regions = slot.RegionIds
    .map((regionId) => regionsById.get(regionId))
    .filter((region): region is NavigationShellRegionDefinition => Boolean(region))

  for (const region of navigation.Shell.Regions) {
    if (region.SlotId === slot.Id && !regionIds.has(region.Id)) {
      regions.push(region)
      regionIds.add(region.Id)
    }
  }

  if (regions.length === 0) {
    for (const region of navigation.Shell.Regions) {
      if (region.Placement === slot.Placement && !regionIds.has(region.Id)) {
        regions.push(region)
        regionIds.add(region.Id)
      }
    }
  }

  return regions
}

/**
 * Returns declared shell slots, or synthesizes the standard shell slots for
 * compatibility with older presentation modules.
 */
export function resolveProjectedNavigationShellSlots(
  navigation: NavigationDefinition,
): readonly NavigationShellSlotDefinition[] {
  const declaredSlots = navigation.Shell.Slots ?? []
  if (declaredSlots.length > 0) {
    return declaredSlots
  }

  return synthesizeNavigationShellSlots(navigation)
}

/**
 * Produces projection diagnostics for shell slots, navigation links, chrome,
 * and app-specific shell region bindings.
 */
export function projectNavigationShellDiagnostics({
  isNodeIconSupported,
  isShellRegionComponentBound,
  isShellRegionComponentTargetBound,
  isShellRegionSupported,
  isShellRegionViewBound,
  navigation,
  renderShellIconBound,
  shellSlotRendererKeys,
  shellSlots,
  supportedSlotPlacements = defaultNavigationShellSlotPlacements,
}: NavigationShellDiagnosticsOptions): readonly PresentationProjectionDiagnostic[] {
  const diagnostics: PresentationProjectionDiagnostic[] = []
  const nodesById = new Map(navigation.Nodes.map((node) => [node.Id, node]))
  const regionsById = new Map(navigation.Shell.Regions.map((region) => [region.Id, region]))
  const shell = navigation.Shell
  const declaredSlots = shell.Slots ?? []
  const hasDeclaredSlots = declaredSlots.length > 0
  const slotIds = new Set(shellSlots.map((slot) => slot.Id))
  const supportedPlacementSet = new Set(supportedSlotPlacements)

  for (const slot of shellSlots) {
    if (
      shellSlotRendererKeys &&
      !hasNavigationShellSlotRendererBinding(shellSlotRendererKeys, slot)
    ) {
      diagnostics.push(createPresentationProjectionDiagnostic({
        category: 'missing-binding',
        details: {
          kind: slot.Kind,
          placement: slot.Placement,
        },
        id: `navigation-shell.${shell.Id}.slot.${slot.Id}.missing-renderer`,
        interpretation: {
          status: 'unbound',
          target: 'navigation-shell',
        },
        message: `Navigation shell slot '${slot.Id}' has no frontend slot renderer binding.`,
        severity: 'warning',
        source: `navigation-shell:${shell.Id}`,
        subject: {
          id: slot.Id,
          kind: 'NavigationShellSlotDefinition',
        },
        suggestedNextStep: 'Register a standard or app-specific shell slot renderer for this slot kind.',
      }))
    }

    if (!isSupportedNavigationShellSlot(slot)) {
      const kindLabel =
        navigationShellSlotKindLabels[slot.Kind] ?? String(slot.Kind)
      diagnostics.push(createPresentationProjectionDiagnostic({
        category: 'unsupported',
        details: {
          kind: slot.Kind,
          kindLabel,
        },
        id: `navigation-shell.${shell.Id}.slot.${slot.Id}.unsupported-kind`,
        interpretation: {
          status: 'unsupported',
          target: 'navigation-shell',
        },
        message: `Navigation shell slot '${slot.Id}' uses unsupported slot kind '${kindLabel}'.`,
        severity: 'warning',
        source: `navigation-shell:${shell.Id}`,
        subject: {
          id: slot.Id,
          kind: 'NavigationShellSlotDefinition',
        },
        suggestedNextStep: 'Add a standard shell slot renderer for this kind or map it to a supported slot kind.',
      }))
    }

    if (!supportedPlacementSet.has(slot.Placement)) {
      diagnostics.push(createPresentationProjectionDiagnostic({
        category: 'unsupported',
        details: {
          placement: slot.Placement,
        },
        id: `navigation-shell.${shell.Id}.slot.${slot.Id}.unsupported-placement`,
        interpretation: {
          status: 'unsupported',
          target: 'navigation-shell',
        },
        message: `Navigation shell slot '${slot.Id}' uses unsupported placement '${slot.Placement}'.`,
        severity: 'warning',
        source: `navigation-shell:${shell.Id}`,
        subject: {
          id: slot.Id,
          kind: 'NavigationShellSlotDefinition',
        },
        suggestedNextStep: 'Add a frontend shell slot placement or change the slot placement.',
      }))
    }

    for (const nodeId of slot.NodeIds) {
      if (!nodesById.has(nodeId)) {
        diagnostics.push(createPresentationProjectionDiagnostic({
          category: 'missing-definition',
          id: `navigation-shell.${shell.Id}.slot.${slot.Id}.node.${nodeId}.missing-node`,
          interpretation: {
            status: 'unbound',
            target: 'navigation-shell',
          },
          message: `Navigation shell slot '${slot.Id}' references missing node '${nodeId}'.`,
          severity: 'warning',
          source: `navigation-shell:${shell.Id}`,
          subject: {
            id: slot.Id,
            kind: 'NavigationShellSlotDefinition',
          },
          suggestedNextStep: 'Declare the navigation node or remove it from the slot.',
        }))
      }
    }

    for (const regionId of slot.RegionIds) {
      if (!regionsById.has(regionId)) {
        diagnostics.push(createPresentationProjectionDiagnostic({
          category: 'missing-definition',
          id: `navigation-shell.${shell.Id}.slot.${slot.Id}.region.${regionId}.missing-region`,
          interpretation: {
            status: 'unbound',
            target: 'navigation-shell',
          },
          message: `Navigation shell slot '${slot.Id}' references missing region '${regionId}'.`,
          severity: 'warning',
          source: `navigation-shell:${shell.Id}`,
          subject: {
            id: slot.Id,
            kind: 'NavigationShellSlotDefinition',
          },
          suggestedNextStep: 'Declare the shell region or remove it from the slot.',
        }))
      }
    }
  }

  const shellNavigationNodeIds = new Set([
    ...shell.PrimaryNodeIds,
    ...shellSlots.flatMap((slot) => slot.NodeIds),
  ])

  for (const nodeId of shellNavigationNodeIds) {
    const node = nodesById.get(nodeId)
    if (!node) {
      continue
    }

    if (!createNavigationHref(navigation, node.RouteId)) {
      diagnostics.push(createPresentationProjectionDiagnostic({
        category: 'missing-binding',
        details: {
          routeId: node.RouteId,
        },
        id: `navigation-shell.${shell.Id}.node.${node.Id}.missing-href`,
        interpretation: {
          status: 'unbound',
          target: 'navigation-route',
        },
        message: `Navigation node '${node.Label}' cannot be lowered to a browser href.`,
        severity: 'warning',
        source: `navigation-shell:${shell.Id}`,
        subject: {
          id: node.Id,
          kind: 'NavigationNodeDefinition',
          name: node.Label,
        },
        suggestedNextStep: 'Bind the node to a route whose required parameters are available in shell context.',
      }))
    }

    if (node.Icon && isNodeIconSupported && !isNodeIconSupported(node.Icon)) {
      diagnostics.push(createPresentationProjectionDiagnostic({
        category: 'missing-binding',
        details: {
          icon: node.Icon,
        },
        id: `navigation-shell.${shell.Id}.node.${node.Id}.unbound-icon`,
        interpretation: {
          status: 'unbound',
          target: 'navigation-icon',
        },
        message: `Navigation node '${node.Label}' declares icon '${node.Icon}', but no frontend icon binding is registered.`,
        severity: 'info',
        source: `navigation-shell:${shell.Id}`,
        subject: {
          id: node.Id,
          kind: 'NavigationNodeDefinition',
          name: node.Label,
        },
        suggestedNextStep: 'Add this icon key to the app shell icon registry or remove it from the navigation node.',
      }))
    }
  }

  if (shell.Chrome?.Icon && !renderShellIconBound) {
    diagnostics.push(createPresentationProjectionDiagnostic({
      category: 'missing-binding',
      details: {
        icon: shell.Chrome.Icon,
      },
      id: `navigation-shell.${shell.Id}.chrome.unbound-icon`,
      interpretation: {
        status: 'unbound',
        target: 'navigation-shell',
      },
      message: `Navigation shell '${shell.Id}' declares shell icon '${shell.Chrome.Icon}', but no shell icon renderer is registered.`,
      severity: 'info',
      source: `navigation-shell:${shell.Id}`,
      subject: {
        id: shell.Id,
        kind: 'NavigationShellDefinition',
      },
      suggestedNextStep: 'Bind a shell icon renderer in the frontend target interpretation.',
    }))
  }

  for (const region of shell.Regions) {
    const componentKey = region.ComponentKey ?? null
    const viewId = region.ViewId ?? null
    const hasComponentTarget = Boolean(componentKey) ||
      Boolean(isShellRegionComponentTargetBound?.(region))

    if (hasDeclaredSlots && region.SlotId && !slotIds.has(region.SlotId)) {
      diagnostics.push(createPresentationProjectionDiagnostic({
        category: 'missing-definition',
        details: {
          slotId: region.SlotId,
        },
        id: `navigation-shell.${shell.Id}.region.${region.Id}.missing-slot`,
        interpretation: {
          status: 'unbound',
          target: 'navigation-shell',
        },
        message: `Shell region '${region.Id}' references missing slot '${region.SlotId}'.`,
        severity: 'warning',
        source: `navigation-shell:${shell.Id}`,
        subject: {
          id: region.Id,
          kind: 'NavigationShellRegionDefinition',
        },
        suggestedNextStep: 'Declare the shell slot or update the region SlotId.',
      }))
    }

    if (viewId && isShellRegionViewBound && !isShellRegionViewBound(viewId, region)) {
      diagnostics.push(createPresentationProjectionDiagnostic({
        category: 'missing-definition',
        details: {
          viewId,
        },
        id: `navigation-shell.${shell.Id}.region.${region.Id}.missing-view`,
        interpretation: {
          status: 'unbound',
          target: 'navigation-shell',
        },
        message: `Shell region '${region.Id}' references missing view '${viewId}'.`,
        severity: 'warning',
        source: `navigation-shell:${shell.Id}`,
        subject: {
          id: region.Id,
          kind: 'NavigationShellRegionDefinition',
        },
        suggestedNextStep: 'Declare the view or remove the ViewId from the shell region.',
      }))
    }

    if (
      componentKey &&
      isShellRegionComponentBound &&
      !isShellRegionComponentBound(componentKey, region)
    ) {
      diagnostics.push(createPresentationProjectionDiagnostic({
        category: 'missing-binding',
        details: {
          componentKey,
        },
        id: `navigation-shell.${shell.Id}.region.${region.Id}.unbound-component`,
        interpretation: {
          status: 'unbound',
          target: 'navigation-shell',
        },
        message: `Shell region '${region.Id}' declares unbound component key '${componentKey}'.`,
        severity: 'warning',
        source: `navigation-shell:${shell.Id}`,
        subject: {
          id: region.Id,
          kind: 'NavigationShellRegionDefinition',
        },
        suggestedNextStep: 'Bind the component key in the frontend shell-region adapter or remove it from the region.',
      }))
    }

    if (!viewId && !hasComponentTarget) {
      diagnostics.push(createPresentationProjectionDiagnostic({
        category: 'unbound',
        id: `navigation-shell.${shell.Id}.region.${region.Id}.missing-target`,
        interpretation: {
          status: 'unbound',
          target: 'navigation-shell',
        },
        message: `Shell region '${region.Id}' has neither ViewId nor component target binding.`,
        severity: 'warning',
        source: `navigation-shell:${shell.Id}`,
        subject: {
          id: region.Id,
          kind: 'NavigationShellRegionDefinition',
        },
        suggestedNextStep: 'Declare a projected view for the region, bind a ComponentRole target, or bind a local component-key escape hatch.',
      }))
    }

    if (isShellRegionSupported && !isShellRegionSupported(region)) {
      diagnostics.push(createPresentationProjectionDiagnostic({
        category: 'missing-binding',
        details: {
          kind: region.Kind,
        },
        id: `navigation-shell.${shell.Id}.region.${region.Id}.unsupported-kind`,
        interpretation: {
          status: 'unbound',
          target: 'navigation-shell-region',
        },
        message: `Shell region '${region.Id}' is not handled by the frontend shell-region adapter.`,
        severity: 'warning',
        source: `navigation-shell:${shell.Id}`,
        subject: {
          id: region.Id,
          kind: 'NavigationShellRegionDefinition',
        },
        suggestedNextStep: 'Add a local shell-region interpretation or remove the region from the shell.',
      }))
    }
  }

  return diagnostics
}

function synthesizeNavigationShellSlots(
  navigation: NavigationDefinition,
): readonly NavigationShellSlotDefinition[] {
  return [
    {
      Annotations: [],
      Design: null,
      Id: 'brand',
      Kind: navigationShellSlotKinds.brand,
      NodeIds: [],
      Placement: 'top-left',
      RegionIds: [],
    },
    {
      Annotations: [],
      Design: null,
      Id: 'primary-navigation',
      Kind: navigationShellSlotKinds.primaryNavigation,
      NodeIds: navigation.Shell.PrimaryNodeIds,
      Placement: 'top-center',
      RegionIds: [],
    },
    {
      Annotations: [],
      Design: null,
      Id: 'utility-actions',
      Kind: navigationShellSlotKinds.utilityActions,
      NodeIds: [],
      Placement: 'top-right',
      RegionIds: getNavigationShellRegions(navigation, { placement: 'top-right' })
        .map((region) => region.Id),
    },
    {
      Annotations: [],
      Design: null,
      Id: 'system-notices',
      Kind: navigationShellSlotKinds.systemNotices,
      NodeIds: [],
      Placement: 'below-navigation',
      RegionIds: getNavigationShellRegions(navigation, { placement: 'below-navigation' })
        .map((region) => region.Id),
    },
    {
      Annotations: [],
      Design: null,
      Id: 'routed-content',
      Kind: navigationShellSlotKinds.routedContent,
      NodeIds: [],
      Placement: 'main',
      RegionIds: [],
    },
  ]
}
