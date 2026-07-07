import type {
  ViewChromeSlotDefinition,
} from './module'
import type {
  PresentationIconDiagnosticSubject,
} from './presentation-icon-diagnostics'
import {
  isViewChromeSlotKind,
} from './view-chrome-slot-renderer-registry'
import {
  viewChromeSlotKinds,
} from '@cohesivesystems/presentation-contracts'

export const viewChromeIconIds = {
  layoutSingle: 'view-chrome.layout.single',
  layoutSplit: 'view-chrome.layout.split',
  viewDefault: 'view-chrome.view.default',
  viewJson: 'view-chrome.view.json',
  viewStructure: 'view-chrome.view.structure',
  viewTypes: 'view-chrome.view.types',
} as const

export function resolveViewChromeIconSubjects(
  slots: readonly ViewChromeSlotDefinition[],
): readonly PresentationIconDiagnosticSubject[] {
  return slots.flatMap((slot) => {
    if (isViewChromeSlotKind(slot, viewChromeSlotKinds.layoutSwitch)) {
      return [
        createViewChromeIconSubject(slot, viewChromeIconIds.layoutSingle, 'Single layout'),
        createViewChromeIconSubject(slot, viewChromeIconIds.layoutSplit, 'Split layout'),
      ]
    }

    if (isViewChromeSlotKind(slot, viewChromeSlotKinds.viewSwitch)) {
      return slot.ViewIds.map((viewId) =>
        createViewChromeIconSubject(
          slot,
          resolveViewChromeViewIconId(viewId),
          `View switch: ${viewId}`,
          { viewId },
        ))
    }

    return []
  })
}

function createViewChromeIconSubject(
  slot: ViewChromeSlotDefinition,
  icon: string,
  label: string,
  details: Readonly<Record<string, unknown>> = {},
): PresentationIconDiagnosticSubject {
  return {
    details: {
      slotId: slot.Id,
      ...details,
    },
    icon,
    id: `${slot.Id}:${icon}`,
    kind: 'view-chrome-icon',
    label,
  }
}

function resolveViewChromeViewIconId(viewId: string) {
  const normalized = viewId.toLocaleLowerCase()
  if (normalized.includes('json')) {
    return viewChromeIconIds.viewJson
  }

  if (normalized.includes('type')) {
    return viewChromeIconIds.viewTypes
  }

  if (normalized.includes('structure')) {
    return viewChromeIconIds.viewStructure
  }

  return viewChromeIconIds.viewDefault
}
