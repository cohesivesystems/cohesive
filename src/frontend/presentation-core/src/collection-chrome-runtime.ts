import type {
  CollectionChromeDefinition,
  CollectionChromeSlotDefinition,
  CollectionChromeSlotKind,
  CollectionChromeSlotPlacement,
  ViewDefinition,
} from './module'
import {
  collectionChromeSlotKinds,
  collectionChromeSlotPlacements,
} from '@cohesive/presentation-contracts'

export type CollectionChromeRuntimeDefinition = NonNullable<ViewDefinition['Collection']>

export interface CollectionChromeRuntime {
  readonly bodySlot: CollectionChromeSlotDefinition | null
  readonly definition: CollectionChromeDefinition | null
  readonly detailSlot: CollectionChromeSlotDefinition | null
  readonly paginationFooterSlot: CollectionChromeSlotDefinition | null
  readonly paginationSlot: CollectionChromeSlotDefinition | null
  readonly queryFormSlot: CollectionChromeSlotDefinition | null
  readonly rowActionsSlot: CollectionChromeSlotDefinition | null
  readonly selectionActionsSlot: CollectionChromeSlotDefinition | null
  readonly slots: readonly CollectionChromeSlotDefinition[]
  readonly summarySlot: CollectionChromeSlotDefinition | null
  readonly findSlot: (
    kind: CollectionChromeSlotKind,
    placement?: CollectionChromeSlotPlacement,
  ) => CollectionChromeSlotDefinition | null
  readonly findSlots: (
    kind: CollectionChromeSlotKind,
    placement?: CollectionChromeSlotPlacement,
  ) => readonly CollectionChromeSlotDefinition[]
}

export function createCollectionChromeRuntime(
  collection: CollectionChromeRuntimeDefinition | null | undefined,
): CollectionChromeRuntime {
  const slots = resolveCollectionChromeSlots(collection)
  const findSlots = (
    kind: CollectionChromeSlotKind,
    placement?: CollectionChromeSlotPlacement,
  ) =>
    slots.filter((slot) =>
      isCollectionChromeSlotKind(slot, kind) &&
      (placement === undefined || isCollectionChromeSlotPlacement(slot, placement)))
  const findSlot = (
    kind: CollectionChromeSlotKind,
    placement?: CollectionChromeSlotPlacement,
  ) => findSlots(kind, placement)[0] ?? null

  return {
    bodySlot: findSlot(collectionChromeSlotKinds.body),
    definition: collection?.Chrome ?? null,
    detailSlot: findSlot(collectionChromeSlotKinds.detail),
    findSlot,
    findSlots,
    paginationFooterSlot: findSlot(
      collectionChromeSlotKinds.pagination,
      collectionChromeSlotPlacements.footer,
    ),
    paginationSlot: findSlot(collectionChromeSlotKinds.pagination),
    queryFormSlot: findSlot(collectionChromeSlotKinds.queryForm),
    rowActionsSlot: findSlot(collectionChromeSlotKinds.rowActions),
    selectionActionsSlot: findSlot(collectionChromeSlotKinds.selectionActions),
    slots,
    summarySlot: findSlot(collectionChromeSlotKinds.summary),
  }
}

export function resolveCollectionChromeSlots(
  collection: CollectionChromeRuntimeDefinition | null | undefined,
): readonly CollectionChromeSlotDefinition[] {
  return collection?.Chrome?.Slots ?? []
}

export function resolveCollectionChromeDataSourceIds(
  collection: CollectionChromeRuntimeDefinition | null | undefined,
) {
  return resolveCollectionChromeSlots(collection).flatMap((slot) => slot.DataSourceIds)
}

export function resolveCollectionChromePrimaryDataSourceId(
  collection: CollectionChromeRuntimeDefinition | null | undefined,
) {
  const chrome = createCollectionChromeRuntime(collection)
  return chrome.bodySlot?.DataSourceIds[0] ??
    chrome.slots.flatMap((slot) => slot.DataSourceIds)[0] ??
    null
}

export function isCollectionChromeSlotKind(
  slot: CollectionChromeSlotDefinition,
  kind: CollectionChromeSlotKind,
) {
  return matchesCollectionChromeSlotKind(slot.Kind, kind)
}

export function isCollectionChromeSlotPlacement(
  slot: CollectionChromeSlotDefinition,
  placement: CollectionChromeSlotPlacement,
) {
  return matchesCollectionChromeSlotPlacement(slot.Placement, placement)
}

export function matchesCollectionChromeSlotKind(
  value: unknown,
  kind: CollectionChromeSlotKind,
) {
  const labelsByKind: Readonly<Record<CollectionChromeSlotKind, string>> = {
    [collectionChromeSlotKinds.queryForm]: 'queryForm',
    [collectionChromeSlotKinds.pagination]: 'pagination',
    [collectionChromeSlotKinds.selectionActions]: 'selectionActions',
    [collectionChromeSlotKinds.rowActions]: 'rowActions',
    [collectionChromeSlotKinds.detail]: 'detail',
    [collectionChromeSlotKinds.summary]: 'summary',
    [collectionChromeSlotKinds.custom]: 'custom',
    [collectionChromeSlotKinds.body]: 'body',
  }
  return matchesGeneratedEnumValue(value, kind, labelsByKind[kind])
}

export function matchesCollectionChromeSlotPlacement(
  value: unknown,
  placement: CollectionChromeSlotPlacement,
) {
  const labelsByPlacement: Readonly<Record<CollectionChromeSlotPlacement, string>> = {
    [collectionChromeSlotPlacements.none]: 'none',
    [collectionChromeSlotPlacements.header]: 'header',
    [collectionChromeSlotPlacements.toolbar]: 'toolbar',
    [collectionChromeSlotPlacements.above]: 'above',
    [collectionChromeSlotPlacements.inline]: 'inline',
    [collectionChromeSlotPlacements.footer]: 'footer',
    [collectionChromeSlotPlacements.sidePanel]: 'sidePanel',
    [collectionChromeSlotPlacements.drawer]: 'drawer',
  }
  return matchesGeneratedEnumValue(value, placement, labelsByPlacement[placement])
}

function matchesGeneratedEnumValue<TValue extends number>(
  value: unknown,
  numericValue: TValue,
  camelLabel: string,
) {
  const normalizedValue = String(value).toLocaleLowerCase()
  return (
    value === numericValue ||
    String(value) === String(numericValue) ||
    normalizedValue === camelLabel.toLocaleLowerCase()
  )
}
