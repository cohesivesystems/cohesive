import { describe, expect, it } from 'vitest'

import {
  presentationValueKinds,
  type PresentationModuleDefinition,
  type ViewDefinition,
} from '@cohesive/presentation-contracts'
import {
  projectViewChromeContentDiagnostics,
} from './index'

describe('view chrome content diagnostics', () => {
  it('reports legacy chrome content fallback across a view tree', () => {
    const child = createView({
      Id: 'child',
      Name: 'Child',
      Title: 'Child title',
    })
    const root = createView({
      Id: 'root',
      Name: 'Root',
      RegionViewIds: ['child'],
      Subtitle: 'Root subtitle',
    })
    const module = {
      Views: [root, child],
    } as unknown as PresentationModuleDefinition

    expect(projectViewChromeContentDiagnostics({
      module,
      view: root,
    }).map((diagnostic) => diagnostic.id)).toEqual([
      'view-chrome.root.content.legacy-content-fallback',
      'view-chrome.child.content.legacy-content-fallback',
    ])
  })
})

function createView({
  Id,
  Name,
  RegionViewIds = [],
  Subtitle,
  Title,
}: {
  readonly Id: string
  readonly Name: string
  readonly RegionViewIds?: readonly string[]
  readonly Subtitle?: string
  readonly Title?: string
}): ViewDefinition {
  return {
    Actions: [],
    Chrome: {
      Collapsible: false,
      Slots: [],
      Subtitle: Subtitle
        ? {
            Kind: presentationValueKinds.literal,
            Literal: Subtitle,
          }
        : null,
      Title: Title
        ? {
            Kind: presentationValueKinds.literal,
            Literal: Title,
          }
        : null,
    },
    Id,
    Name,
    Regions: [
      {
        ViewIds: RegionViewIds,
      },
    ],
  } as unknown as ViewDefinition
}
