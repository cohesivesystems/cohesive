import type {
  CollectionChromeRuntime,
} from './collection-chrome-runtime'

export const collectionChromeIconIds = {
  detailClose: 'collection.detail.close',
  paginationFirstPage: 'collection.pagination.first-page',
  paginationLoading: 'collection.pagination.loading',
  paginationNextPage: 'collection.pagination.next-page',
  paginationPreviousPage: 'collection.pagination.previous-page',
  rowActionsMenu: 'collection.row-actions.menu',
} as const

export interface CollectionChromeIconSubject {
  readonly details?: Readonly<Record<string, unknown>>
  readonly icon: string
  readonly id: string
  readonly kind: string
  readonly label?: string | null
}

export function resolveCollectionChromeIconSubjects(
  chrome: CollectionChromeRuntime,
): readonly CollectionChromeIconSubject[] {
  const subjects: CollectionChromeIconSubject[] = []

  if (chrome.paginationSlot) {
    subjects.push(
      createCollectionChromeIconSubject({
        icon: collectionChromeIconIds.paginationLoading,
        label: 'Pagination loading',
        slotId: chrome.paginationSlot.Id,
      }),
      createCollectionChromeIconSubject({
        icon: collectionChromeIconIds.paginationFirstPage,
        label: 'First page',
        slotId: chrome.paginationSlot.Id,
      }),
      createCollectionChromeIconSubject({
        icon: collectionChromeIconIds.paginationPreviousPage,
        label: 'Previous page',
        slotId: chrome.paginationSlot.Id,
      }),
      createCollectionChromeIconSubject({
        icon: collectionChromeIconIds.paginationNextPage,
        label: 'Next page',
        slotId: chrome.paginationSlot.Id,
      }),
    )
  }

  if (chrome.rowActionsSlot) {
    subjects.push(createCollectionChromeIconSubject({
      icon: collectionChromeIconIds.rowActionsMenu,
      label: 'Row actions menu',
      slotId: chrome.rowActionsSlot.Id,
    }))
  }

  if (chrome.detailSlot) {
    subjects.push(createCollectionChromeIconSubject({
      icon: collectionChromeIconIds.detailClose,
      label: 'Close detail panel',
      slotId: chrome.detailSlot.Id,
    }))
  }

  return subjects
}

function createCollectionChromeIconSubject({
  icon,
  label,
  slotId,
}: {
  readonly icon: string
  readonly label: string
  readonly slotId: string
}): CollectionChromeIconSubject {
  return {
    details: {
      slotId,
    },
    icon,
    id: `${slotId}:${icon}`,
    kind: 'collection-chrome-icon',
    label,
  }
}
