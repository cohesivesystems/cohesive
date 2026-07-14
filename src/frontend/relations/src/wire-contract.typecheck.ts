import type {
  RelationDraftDocument,
  RelationshipCatalogDocument,
} from './generated/relations.shapes.generated'

type AssertAssignable<TExpected, TActual extends TExpected> = TActual

export type _RelationshipCatalogWireContractCheck = AssertAssignable<
  RelationshipCatalogDocument,
  {
    schemaVersion: 'relationship-catalog/v1'
    catalog: {
      relationships: [
        {
          id: 'relationship:v1:sha256:example'
          sourceShape: {
            graphId: 'domain/v1'
            shapeId: 'Load'
          }
          sourceReference: {
            segments: [
              {
                kind: 'Field'
                segment: 'CustomerId'
              },
            ]
          }
          targetShape: {
            graphId: 'domain/v1'
            shapeId: 'Customer'
          }
          targetKey: {
            $targetKey: 'observationIdentity'
          }
          sourceReferenceUniqueness: 'NotGuaranteed'
        },
      ]
    }
    catalogFingerprint: {
      algorithm: 'sha256'
      canonicalization: 'relationship-catalog/v1-c14n/v1'
      value: 'example'
    }
    metadata: {
      origin: 'Generated'
      producer: 'ari'
      annotations: {
        evidence: {
          confidence: 0.98
        }
      }
    }
  }
>

export type _RelationDraftWireContractCheck = AssertAssignable<
  RelationDraftDocument,
  {
    schemaVersion: 'relation-draft/v1'
    draft: {
      id: 'draft:load-search'
      relationId: 'relation:load-search'
      name: 'Load search projection'
      input: {
        nodes: [
          {
            $node: 'source'
            id: 'load-source'
            binding: 'load'
            shape: {
              graphId: 'domain/v1'
              shapeId: 'Load'
            }
          },
        ]
        parameters: []
      }
      rootBinding: 'load'
      projection: {
        id: 'load-search-project'
        input: 'load-source'
        resultBinding: 'loadSearch'
        resultShape: {
          graphId: 'search/v1'
          shapeId: 'LoadSearchDto'
        }
        assignments: [
          {
            id: 'slot:customerName'
            target: {
              segments: [{ kind: 'Field'; segment: 'CustomerName' }]
            }
            candidates: []
            resolution: {
              $resolution: 'unresolved'
              reasons: ['NoCandidate']
            }
          },
        ]
      }
      outputMode: 'OnePerRoot'
      invariants: []
    }
    draftFingerprint: {
      algorithm: 'sha256'
      canonicalization: 'relation-draft/v1-c14n/v1'
      value: 'example'
    }
    metadata: {
      origin: 'Generated'
      producer: 'ari'
      annotations: {}
      producerArtifacts: [
        {
          kind: 'ari/relation-proposal'
          value: 'proposal-42'
        },
      ]
      conventionDecisions: []
    }
  }
>
