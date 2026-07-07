import { describe, expect, it } from 'vitest'

import {
  projectPresentationContentDiagnostics,
} from './index'

describe('presentation content diagnostics', () => {
  it('reports missing required title and description content', () => {
    const diagnostics = projectPresentationContentDiagnostics({
      content: {
        Annotations: [],
      },
      diagnosticIdPrefix: 'surface.documents',
      requireDescription: true,
      requireTitle: true,
      source: 'content-test',
      subject: {
        id: 'documents',
        kind: 'view',
        name: 'Documents',
      },
      surfaceLabel: 'Documents view',
    })

    expect(diagnostics.map((diagnostic) => diagnostic.id)).toEqual([
      'surface.documents.missing-content-title',
      'surface.documents.missing-content-description',
    ])
    expect(diagnostics.every((diagnostic) =>
      diagnostic.category === 'incomplete-projection' &&
      diagnostic.interpretation?.status === 'locally-interpreted',
    )).toBe(true)
  })
})
