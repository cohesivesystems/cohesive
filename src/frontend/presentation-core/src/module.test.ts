import { describe, expect, it } from 'vitest'

import { viewChromeSlotKinds } from '@cohesivesystems/presentation-contracts'
import {
  findPresentationView,
  resolvePresentationViewActionPlacements,
  type ViewDefinition,
} from './module'

describe('presentation module projection', () => {
  it('finds identified view definitions', () => {
    const view = { Id: 'runs-view' } as ViewDefinition

    expect(findPresentationView({ Views: [view] }, 'runs-view')).toBe(view)
    expect(findPresentationView({ Views: [view] }, 'missing')).toBeNull()
  })

  it('resolves view and chrome action placements with semantic de-duplication', () => {
    const view = {
      Actions: [
        { ActionId: 'refresh', Region: 'header' },
        { ActionId: 'open', Region: 'body' },
      ],
      Chrome: {
        Slots: [
          {
            Actions: [
              { ActionId: 'refresh', Region: 'header' },
              { ActionId: 'export', Region: 'header' },
            ],
            Kind: viewChromeSlotKinds.actions,
          },
        ],
      },
    } as unknown as Pick<ViewDefinition, 'Actions' | 'Chrome'>

    expect(resolvePresentationViewActionPlacements(view).map((placement) =>
      `${placement.Region}:${placement.ActionId}`,
    )).toEqual([
      'header:refresh',
      'header:export',
      'body:open',
    ])
  })
})
