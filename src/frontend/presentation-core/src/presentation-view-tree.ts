import {
  findPresentationView,
  type PresentationModuleDefinition,
  type ViewDefinition,
  type ViewRegionDefinition,
} from './module'

export function findPresentationViewRegion(
  view: Pick<ViewDefinition, 'Regions'> | null,
  regionId: string,
) {
  return view?.Regions?.find((region) => region.Id === regionId) ?? null
}

export function readFirstRegionViewId(
  view: Pick<ViewDefinition, 'Regions'> | null,
  regionId: string,
) {
  return findPresentationViewRegion(view, regionId)?.ViewIds?.[0] ?? null
}

export function findFirstRegionView<TView extends ViewDefinition>(
  module: Pick<PresentationModuleDefinition, 'Views'> | null,
  view: Pick<ViewDefinition, 'Regions'> | null,
  regionId: string,
) {
  const viewId = readFirstRegionViewId(view, regionId)
  return viewId ? findPresentationView<TView>(module, viewId) : null
}

export function getRegionViewIds(region: ViewRegionDefinition) {
  return region.ViewIds ?? []
}
