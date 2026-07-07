import { describe, expect, it } from 'vitest'

import { projectPromptDocumentPreviewData } from './prompt-document-preview-projection'

describe('projectPromptDocumentPreviewData', () => {
  it('copies generated conventional preview resource fields from raw responses', () => {
    const preview = projectPromptDocumentPreviewData({
      response: {
        Document: { id: 'shape-graph-1' },
        Id: 'shape-graph-1',
        Name: 'Compiled shape graph',
        SourceEdiSpecId: 'edi-spec-1',
        ConcurrencyToken: 'token-1',
        EntityVersion: 4,
      },
    })

    expect(preview.resource).toEqual({
      Document: { id: 'shape-graph-1' },
      Id: 'shape-graph-1',
      Name: 'Compiled shape graph',
      ConcurrencyToken: 'token-1',
      EntityVersion: 4,
    })
    expect(preview.response).toMatchObject({
      SourceEdiSpecId: 'edi-spec-1',
    })
  })
})
