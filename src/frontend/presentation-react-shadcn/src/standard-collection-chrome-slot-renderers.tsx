import type { ReactNode } from 'react'

import {
  collectionChromeIconIds,
  collectionChromeSlotProjectionAnnotationName,
  createCollectionChromeSlotRendererRegistry,
  findPresentationInputForm,
  findPresentationQueryForm,
  getCollectionChromeSlotRendererRegistryKeys,
  resolveCollectionChromeSlotRenderer,
  type CollectionChromeSlotDefinition,
} from '@cohesive/presentation-core'
import {
  renderPresentationIcon,
} from './presentation-icon-registry'
import type {
  ProjectedCollectionChromeSlotRenderContext,
} from './projected-collection-view'
import {
  ProjectedCollectionRowActions,
  ProjectedCollectionSelectionActionToolbar,
} from './projected-collection-view'
import {
  collectionChromeSlotKinds,
  collectionChromeSlotPlacements,
} from '@cohesive/presentation-contracts'

/**
 * Default shadcn-backed renderers for the semantic collection chrome slots
 * projected by Cohesive.Presentation.
 *
 * The registry is keyed by slot kind and optional placement so callers can
 * override individual semantic slots while preserving the standard behavior
 * for the rest of the collection surface.
 */
export const standardCollectionChromeSlotRenderers =
  createCollectionChromeSlotRendererRegistry<
    ProjectedCollectionChromeSlotRenderContext<object>,
    ReactNode
  >([
    {
      kind: collectionChromeSlotKinds.queryForm,
      render: renderStandardCollectionQueryForm,
    },
    {
      kind: collectionChromeSlotKinds.summary,
      render: renderStandardCollectionSummary,
    },
    {
      kind: collectionChromeSlotKinds.pagination,
      placement: collectionChromeSlotPlacements.footer,
      render: renderStandardCollectionPaginationFooter,
    },
    {
      kind: collectionChromeSlotKinds.selectionActions,
      placement: collectionChromeSlotPlacements.toolbar,
      render: renderStandardCollectionSelectionActionsToolbar,
    },
    {
      kind: collectionChromeSlotKinds.detail,
      render: renderStandardCollectionDetail,
    },
    {
      kind: collectionChromeSlotKinds.rowActions,
      placement: collectionChromeSlotPlacements.inline,
      render: renderStandardCollectionRowActions,
    },
    {
      kind: collectionChromeSlotKinds.body,
      placement: collectionChromeSlotPlacements.inline,
      render: renderStandardCollectionBody,
    },
  ])

/**
 * Registry keys exposed for diagnostics, documentation, and composition with
 * custom collection chrome slot renderer registries.
 */
export const standardCollectionChromeSlotRendererKeys =
  getCollectionChromeSlotRendererRegistryKeys(standardCollectionChromeSlotRenderers)

/**
 * Renders a collection chrome slot with the standard renderer matching the
 * slot's semantic kind and placement.
 *
 * @param context Rendering context for the projected collection slot.
 * @returns The rendered chrome node, or `null` when no standard renderer
 * matches the slot or the matching renderer elects not to render.
 */
export function renderStandardCollectionChromeSlot(
  context: ProjectedCollectionChromeSlotRenderContext<object>,
) {
  const renderer = resolveCollectionChromeSlotRenderer(
    standardCollectionChromeSlotRenderers,
    context.slot,
  )
  return renderer?.(context) ?? null
}

/**
 * Renders footer pagination from the collection runtime paging window.
 *
 * The slot is omitted when pagination is disabled, no page window is available,
 * or the slot requests suppression of the standard projection.
 */
function renderStandardCollectionPaginationFooter({
  collectionRuntime,
  componentSet,
  componentSystem,
  module,
  slot,
  viewId,
}: ProjectedCollectionChromeSlotRenderContext<object>) {
  if (shouldSuppressStandardCollectionChromeSlot(slot)) {
    return null
  }

  const pagination = collectionRuntime.pagination
  const window = pagination.window
  if (!pagination.isFooterEnabled || !window) {
    return null
  }

  const pageInfo = window.pageInfo
  const isFetching = pagination.isFetching
  const pageLabel = pageInfo.totalPageCount
    ? `Page ${(pageInfo.pageIndex + 1).toLocaleString()} of ${pageInfo.totalPageCount.toLocaleString()}`
    : `Page ${(pageInfo.pageIndex + 1).toLocaleString()}`
  const totalLabel = typeof pageInfo.totalCount === 'number'
    ? `${pageInfo.totalCount.toLocaleString()} total`
    : `${pageInfo.itemCount.toLocaleString()} shown`

  return componentSystem.collectionChrome.CollectionPaginationBar({
    canGoNextPage: window.canGoNextPage,
    canGoPreviousPage: window.canGoPreviousPage,
    firstIcon: renderPresentationIcon({
      componentSet,
      icon: collectionChromeIconIds.paginationFirstPage,
      module,
      subject: slot,
    }),
    isFetching,
    loadingIcon: renderPresentationIcon({
      className: 'size-3.5 animate-spin text-sky-700',
      componentSet,
      icon: collectionChromeIconIds.paginationLoading,
      module,
      subject: slot,
    }),
    nextIcon: renderPresentationIcon({
      componentSet,
      icon: collectionChromeIconIds.paginationNextPage,
      module,
      subject: slot,
    }),
    onFirstPage: window.goToFirstPage,
    onNextPage: window.goToNextPage,
    onPreviousPage: window.goToPreviousPage,
    pageLabel,
    pageSizeLabel: `${pageInfo.pageSize.toLocaleString()} per page`,
    previousIcon: renderPresentationIcon({
      componentSet,
      icon: collectionChromeIconIds.paginationPreviousPage,
      module,
      subject: slot,
    }),
    shownLabel: `${pageInfo.itemCount.toLocaleString()} shown`,
    slotId: slot.Id,
    totalLabel,
    viewId,
  })
}

/**
 * Renders the collection query form chrome around the projected query form.
 *
 * Query forms without a backing input form are rendered without the standard
 * chrome wrapper so the backend projection can supply a fully custom surface.
 */
function renderStandardCollectionQueryForm({
  componentSystem,
  module,
  renderQueryFormSlot,
  slot,
  viewId,
}: ProjectedCollectionChromeSlotRenderContext<object>) {
  if (shouldSuppressStandardCollectionChromeSlot(slot)) {
    return null
  }

  if (shouldRenderQueryFormWithoutChrome({ module, slot })) {
    return renderQueryFormSlot?.(slot) ?? null
  }

  const children = renderQueryFormSlot?.(slot) ?? null
  if (children === null || children === undefined || children === false) {
    return null
  }

  return componentSystem.collectionChrome.CollectionQueryFormSlot({
    children,
    slotId: slot.Id,
    viewId,
  })
}

/**
 * Determines whether a query-form slot represents a custom projection rather
 * than a standard input form projection.
 */
function shouldRenderQueryFormWithoutChrome({
  module,
  slot,
}: Pick<ProjectedCollectionChromeSlotRenderContext<object>, 'module' | 'slot'>) {
  if (!slot.QueryFormId) {
    return false
  }

  const queryForm = findPresentationQueryForm(module ?? null, slot.QueryFormId)
  return Boolean(
    queryForm &&
      !findPresentationInputForm(module ?? null, queryForm.FormId),
  )
}

/**
 * Renders the summary slot using the component system's collection chrome
 * wrapper and the caller-provided summary projection.
 */
function renderStandardCollectionSummary({
  componentSystem,
  renderSummarySlot,
  slot,
  viewId,
}: ProjectedCollectionChromeSlotRenderContext<object>) {
  if (shouldSuppressStandardCollectionChromeSlot(slot)) {
    return null
  }

  return componentSystem.collectionChrome.CollectionSummarySlot({
    children: renderSummarySlot?.(slot) ?? null,
    slotId: slot.Id,
    viewId,
  })
}

/**
 * Renders the toolbar surface for actions that target the current collection
 * selection.
 */
function renderStandardCollectionSelectionActionsToolbar({
  canExecuteAction,
  collectionRuntime,
  componentSet,
  componentSystem,
  executeAction,
  module,
  renderActionIcon,
  slot,
}: ProjectedCollectionChromeSlotRenderContext<object>) {
  if (shouldSuppressStandardCollectionChromeSlot(slot)) {
    return null
  }

  return (
    <ProjectedCollectionSelectionActionToolbar
      canExecuteAction={canExecuteAction}
      collectionRuntime={collectionRuntime}
      componentSet={componentSet}
      componentSystem={componentSystem}
      executeAction={executeAction}
      module={module}
      renderActionIcon={renderActionIcon}
      slot={slot}
    />
  )
}

/**
 * Renders the detail slot using the component system's collection chrome
 * wrapper and the caller-provided detail projection.
 */
function renderStandardCollectionDetail({
  componentSystem,
  renderDetailSlot,
  slot,
  viewId,
}: ProjectedCollectionChromeSlotRenderContext<object>) {
  if (shouldSuppressStandardCollectionChromeSlot(slot)) {
    return null
  }

  return componentSystem.collectionChrome.CollectionDetailSlot({
    children: renderDetailSlot?.(slot) ?? null,
    slotId: slot.Id,
    viewId,
  })
}

/**
 * Renders the body slot using the component system's collection chrome wrapper
 * and the caller-provided body projection.
 */
function renderStandardCollectionBody({
  componentSystem,
  renderBodySlot,
  slot,
  viewId,
}: ProjectedCollectionChromeSlotRenderContext<object>) {
  if (shouldSuppressStandardCollectionChromeSlot(slot)) {
    return null
  }

  return componentSystem.collectionChrome.CollectionBodySlot({
    children: renderBodySlot?.(slot) ?? null,
    slotId: slot.Id,
    viewId,
  })
}

/**
 * Renders row-scoped actions for the current collection row.
 *
 * A caller-provided row action slot renderer takes precedence over the standard
 * projected row action component. The slot is skipped outside row context.
 */
function renderStandardCollectionRowActions({
  canExecuteAction,
  collectionRuntime,
  componentSet,
  componentSystem,
  executeAction,
  module,
  renderActionIcon,
  renderRowActionsSlot,
  row,
  rowLabel,
  slot,
  viewId,
}: ProjectedCollectionChromeSlotRenderContext<object>) {
  if (shouldSuppressStandardCollectionChromeSlot(slot) || !row) {
    return null
  }

  return renderRowActionsSlot?.(slot, row, rowLabel ?? null) ?? (
    <ProjectedCollectionRowActions
      canExecuteAction={canExecuteAction}
      collectionRuntime={collectionRuntime}
      componentSet={componentSet}
      componentSystem={componentSystem}
      executeAction={executeAction}
      module={module}
      renderActionIcon={renderActionIcon}
      row={row}
      rowLabel={rowLabel ?? null}
      slot={slot}
      viewId={viewId}
    />
  )
}

/**
 * Checks whether a slot's projection annotation opts out of the standard
 * renderer.
 *
 * A boolean annotation value suppresses directly. Object annotation values use
 * `suppressDefaultRenderer` so other projection options can share the same
 * annotation payload.
 */
function shouldSuppressStandardCollectionChromeSlot(
  slot: CollectionChromeSlotDefinition,
) {
  return slot.Annotations.some((annotation) => {
    if (annotation.Name.toLocaleLowerCase() !== collectionChromeSlotProjectionAnnotationName) {
      return false
    }

    const value = annotation.Value
    if (typeof value === 'boolean') {
      return value
    }

    if (!value || typeof value !== 'object') {
      return false
    }

    return Boolean(
      (value as Readonly<Record<string, unknown>>).suppressDefaultRenderer,
    )
  })
}
