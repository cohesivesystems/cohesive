import { describe, expect, it } from 'vitest'

import {
  presentationValueKinds,
  type ActionDefinition,
} from '@cohesive/presentation-contracts'
import { resolveActionPendingLabel } from './index'

describe('presentation action runtime labels', () => {
  it('prefers matching pending label variants and falls back to the default label', () => {
    const action = {
      RuntimePresentation: {
        Annotations: [],
        PendingLabel: 'Saving',
        PendingLabelVariants: [
          {
            Annotations: [],
            Condition: {
              Field: 'mode',
              Kind: presentationValueKinds.field,
            },
            ExpectedValue: 'create',
            Label: 'Creating',
          },
        ],
      },
    } as unknown as ActionDefinition

    expect(resolveActionPendingLabel({
      action,
      data: { mode: 'CREATE' },
      fallback: 'Working',
    })).toBe('Creating')
    expect(resolveActionPendingLabel({
      action,
      data: { mode: 'update' },
      fallback: 'Working',
    })).toBe('Saving')
  })

  it('uses the caller fallback when runtime metadata has no label', () => {
    expect(resolveActionPendingLabel({
      action: {
        RuntimePresentation: {
          Annotations: [],
          PendingLabel: '',
          PendingLabelVariants: [],
        },
      } as unknown as ActionDefinition,
      fallback: 'Working',
    })).toBe('Working')
  })
})
