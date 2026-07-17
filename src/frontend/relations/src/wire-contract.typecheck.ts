import type {
  RelationDraftDocument,
  RelationQueryRealizationReport,
  RelationQueryTargetCapabilityProfile,
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
      canonicalization: 'relationship-catalog/v1-c14n/v2'
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
      canonicalization: 'relation-draft/v1-c14n/v2'
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

export type _RelationQueryTargetCapabilityProfileWireContractCheck = AssertAssignable<
  RelationQueryTargetCapabilityProfile,
  {
    target: 'cohesive.relations.in-memory'
    id: 'cohesive.relations.in-memory/realization-v1'
    supportedDefinitionSchemaVersions: ['relation-query/v1']
    supportedCompilerProfiles: ['relation-query-static-compiler/v1']
    capabilities: [
      {
        id: 'in-memory/filter'
        capability: {
          $capability: 'logical'
          kind: 'Filter'
        }
        operatingBoundaries: ['boundary/max-input-rows']
      },
    ]
    operatingBoundaries: [
      {
        id: 'boundary/max-input-rows'
        kind: 'MaximumInputRows'
        limit: '9007199254740993'
      },
    ]
  }
>

export type _RelationQueryStaticFactWideIntegerWireContractCheck = AssertAssignable<
  RelationQueryRealizationReport['requirements'][number]['staticFacts'][number],
  {
    kind: 'PageSize'
    value: '9007199254740993'
  }
>

export type _RelationQueryBoundaryValidationWideIntegerWireContractCheck = AssertAssignable<
  Extract<
    RelationQueryRealizationReport['decisions'][number],
    { readonly $decision: 'constrained' }
  >['boundaryValidations'][number],
  {
    boundary: 'boundary/max-page-size'
    kind: 'StaticPlanFact'
    measuredValue: '9007199254740993'
  }
>

export type _RelationQueryInvalidProfileUnknownEnumWireContractCheck = AssertAssignable<
  RelationQueryTargetCapabilityProfile,
  {
    target: 'target/test'
    id: 'target/test/v1'
    supportedDefinitionSchemaVersions: ['relation-query/v1']
    supportedCompilerProfiles: ['relation-query-static-compiler/v1']
    capabilities: [
      {
        id: 'evidence/unknown-logical'
        capability: {
          $capability: 'logical'
          kind: 2147483647
        }
        operatingBoundaries: []
      },
      {
        id: 'evidence/unknown-expression-requirement-kind'
        capability: {
          $capability: 'expression'
          capability: { value: 'expression/unknown-requirement-kind' }
          requirementKind: 2147483647
        }
        operatingBoundaries: []
      },
    ]
    operatingBoundaries: [
      {
        id: 'boundary/unknown-kind'
        kind: 2147483647
      },
    ]
  }
>

export type _RelationQueryUnavailableDecisionWireContractCheck = AssertAssignable<
  RelationQueryRealizationReport['decisions'][number],
  {
    $decision: 'unavailable'
    requirement: 'requirement/temporal/interval-overlap'
    reason: 'CapabilityNotAdvertised'
    missingCapabilities: [
      {
        $capability: 'temporal'
        capability: 'IntervalOverlap'
      },
    ]
  }
>

export type _RelationQueryRequirementOriginWireContractCheck = AssertAssignable<
  NonNullable<RelationQueryRealizationReport['requirements'][number]['origin']>,
  {
    node: 'temporal-join'
    binding: 'customer-version'
    semanticSite: 'temporal-join/correlation'
    expressionPath: '$.left'
    fieldPath: {
      segments: [{ kind: 'Field'; segment: 'CustomerId' }]
    }
  }
>
