import {
  findPresentationView,
  type PresentationModuleDefinition,
  type ViewDefinition,
} from './module'
import {
  viewChromeSlotKinds,
  viewRegionKinds,
} from '@cohesive/presentation-contracts'

/**
 * Resolves document workspace metadata fields from page/header chrome.
 *
 * The document editor surface renders these same badge-strip field ids. Keeping
 * this helper in the projection layer lets adapters build field renderers and
 * diagnostics from the same IR locations instead of repeating view ids.
 */
export function resolveDocumentWorkspaceMetadataFieldIds(
  module: Pick<PresentationModuleDefinition, 'Views'> | null,
  pageView: ViewDefinition | null,
) {
  const headerView = findHostedView(module, pageView, viewRegionKinds.header, 'Header')
  const badgeSlot = headerView?.Chrome?.Slots?.find((slot) =>
    matchesEnum(slot.Kind, viewChromeSlotKinds.badgeStrip, 'BadgeStrip'))

  return badgeSlot?.FieldIds ?? []
}

/**
 * Resolves the page-region id used for document workspace header chrome.
 *
 * Profile-level notices target region ids, while page layout regions carry
 * enum kinds. This helper keeps that cross-reference in the projection layer
 * instead of repeating product constants in workspace hosts.
 */
export function resolveDocumentWorkspaceHeaderRegionId(pageView: ViewDefinition | null) {
  return pageView?.Regions?.find((region) =>
    matchesEnum(region.Kind, viewRegionKinds.header, 'Header'))?.Id ?? null
}

function findHostedView(
  module: Pick<PresentationModuleDefinition, 'Views'> | null,
  view: ViewDefinition | null,
  regionKind: string | number,
  regionKindLabel: string,
) {
  const region = view?.Regions?.find((candidate) =>
    matchesEnum(candidate.Kind, regionKind, regionKindLabel))
  const viewId = region?.ViewIds?.[0]

  return viewId ? findPresentationView(module, viewId) : null
}

function matchesEnum(
  value: string | number | null | undefined,
  numericValue: string | number,
  label: string,
) {
  return value === numericValue ||
    String(value) === String(numericValue) ||
    String(value) === label
}
