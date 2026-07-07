import { describe, expect, it } from 'vitest'

import {
  actionResultStateWriteModes,
} from '@cohesivesystems/presentation-contracts'
import {
  applyPresentationActionResultPolicyStateWrites,
  applyPresentationActionResultStateWrites,
} from './index'

describe('presentation action result state', () => {
  it('writes full action responses and response paths to data-source state', () => {
    const result = {
      Diagnostics: [{ Message: 'Target path is unmapped.' }],
      Relation: null,
      Succeeded: false,
    }

    const state = applyPresentationActionResultPolicyStateWrites({
      policy: {
        InvalidateDataSourceIds: [],
        NavigateToRouteId: null,
        StateWrites: [
          {
            Mode: actionResultStateWriteModes.replace,
            SourcePath: null,
            TargetDataSourceId: 'compile-result',
          },
          {
            Mode: actionResultStateWriteModes.replace,
            SourcePath: 'Diagnostics',
            TargetDataSourceId: 'compile-diagnostics',
          },
        ],
        Toast: null,
      },
      result,
      state: {},
    })

    expect(state['compile-result']).toBe(result)
    expect(state['compile-diagnostics']).toEqual([
      { Message: 'Target path is unmapped.' },
    ])
  })

  it('applies merge, append, and clear write modes', () => {
    const state = applyPresentationActionResultStateWrites({
      result: {
        ClearValue: null,
        Items: ['next'],
        Patch: { Status: 'complete' },
      },
      state: {
        cleared: 'value',
        items: ['existing'],
        record: { Id: 'run-1', Status: 'pending' },
      },
      writes: [
        {
          Mode: actionResultStateWriteModes.merge,
          SourcePath: 'Patch',
          TargetDataSourceId: 'record',
        },
        {
          Mode: actionResultStateWriteModes.append,
          SourcePath: 'Items',
          TargetDataSourceId: 'items',
        },
        {
          Mode: actionResultStateWriteModes.clear,
          SourcePath: 'ClearValue',
          TargetDataSourceId: 'cleared',
        },
      ],
    })

    expect(state.record).toEqual({ Id: 'run-1', Status: 'complete' })
    expect(state.items).toEqual(['existing', 'next'])
    expect(state).not.toHaveProperty('cleared')
  })
})
