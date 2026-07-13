import type { RelationshipCatalogDocument } from './generated/relations.shapes.generated'

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
