import { describe, expect, it } from 'vitest'

import {
  projectPresentationDesignIntentDiagnostics,
} from './index'

describe('presentation design intent diagnostics', () => {
  it('reports declared design fields that the active interpreter ignores', () => {
    expect(projectPresentationDesignIntentDiagnostics({
      design: {
        Density: 'compact',
        Layout: 'stacked',
        Role: 'toolbar',
        Size: '',
        Tone: '',
        Variant: '',
      },
      ignoredFields: ['Density', 'Layout', 'Tone'],
      interpretedFields: ['Role'],
      message: 'Partial design interpretation',
      semanticInputs: ['chrome.slot.kind'],
      source: 'design-test',
      subject: {
        id: 'toolbar',
        kind: 'collection-chrome-slot',
      },
      target: 'react-shadcn',
    })).toEqual([
      expect.objectContaining({
        details: {
          ignoredFields: ['Design.Density', 'Design.Layout'],
          ignoredValues: {
            Density: 'compact',
            Layout: 'stacked',
          },
          interpretedFields: ['chrome.slot.kind', 'Design.Role'],
        },
        id: 'presentation-design.toolbar.design-intent.partial',
        interpretation: {
          status: 'projected',
          target: 'react-shadcn',
        },
      }),
    ])
  })
})
