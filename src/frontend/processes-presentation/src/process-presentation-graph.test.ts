import { describe, expect, it } from 'vitest'
import {
  canonicalProcessAwaitClauseKinds,
  canonicalProcessNodeKinds,
  type ExecutionDefinitionDocument,
  type ExecutionDefinitionReference,
  type ValueContract,
} from '@cohesivesystems/processes'
import {
  canonicalProcessPresentationCompatibility,
  canonicalProcessAwaitClausePresentationDispositionKinds,
  canonicalProcessPresentationDispositionKinds,
  processPresentationDefinitionReferenceId,
  processPresentationElementId,
  projectCanonicalProcessDocument,
} from './process-presentation-graph'

const valueContract: ValueContract = {
  cardinality: 'Single',
  nullability: 'NonNullable',
  presence: 'Required',
  type: {
    $type: 'scalar',
    format: 'None',
    kind: 'String',
  },
}

const childReference: ExecutionDefinitionReference = {
  definitionId: 'training-child',
  fingerprint: {
    algorithm: 'sha256',
    canonicalization: 'cohesive-canonical-json/v1',
    value: 'child-fingerprint',
  },
  revisionId: 'training-child/r3',
}

const requestContractReference: ExecutionDefinitionReference = {
  definitionId: 'training-request',
  fingerprint: {
    algorithm: 'sha256',
    canonicalization: 'cohesive-canonical-json/v1',
    value: 'request-fingerprint',
  },
  revisionId: 'training-request/r2',
}

const expression = { $expr: 'parameter', parameter: 'input' } as const

describe('canonical Process presentation projection', () => {
  it('accounts for the complete generated Process construct inventory', () => {
    expect(canonicalProcessPresentationDispositionKinds).toEqual(
      [...canonicalProcessNodeKinds].sort(),
    )
    expect(canonicalProcessAwaitClausePresentationDispositionKinds).toEqual(
      [...canonicalProcessAwaitClauseKinds].sort(),
    )
  })

  it('projects branch, clause, outcome, child, contract, terminal, and source evidence deterministically', () => {
    const document = representativeDocument()

    const first = projectCanonicalProcessDocument(document)
    const second = projectCanonicalProcessDocument(document)

    expect(first).toEqual(second)
    expect(first.diagnostics).toEqual([])
    expect(first.graph).not.toBeNull()
    const graph = first.graph!
    expect(Object.isFrozen(graph)).toBe(true)
    expect(Object.isFrozen(graph.nodes)).toBe(true)
    expect(graph.entryNodeId).toBe(
      processPresentationElementId('process-node', 'invoke-child'),
    )

    expect(new Set(graph.nodes.map((node) => node.category))).toEqual(new Set([
      'await-clause',
      'choice-case',
      'definition-reference',
      'fallback',
      'fork-branch',
      'join-result',
      'match-case',
      'process-node',
      'request-outcome',
    ]))

    const invocation = graph.nodes.find(
      (node) => node.id === processPresentationElementId('process-node', 'invoke-child'),
    )!
    expect(invocation.details.definitionReferences).toContainEqual({
      reference: childReference,
      role: 'child-process',
    })
    expect(invocation.details.sourceMap).toEqual([
      {
        description: 'Child invocation',
        reference: 'training-process.cs:42',
        semanticPath: { segments: ['nodes', '0'] },
      },
    ])
    expect(graph.nodes).toContainEqual(expect.objectContaining({
      id: processPresentationDefinitionReferenceId(childReference),
      category: 'definition-reference',
    }))

    const accepted = graph.nodes.find(
      (node) => node.id === processPresentationElementId(
        'request-outcome',
        'child-accepted',
        'invoke-child',
      ),
    )!
    expect(accepted.details.sourceMap).toEqual([
      {
        description: 'Accepted continuation',
        reference: 'training-process.cs:48',
        semanticPath: { segments: ['nodes', '0', 'outcomes', '0'] },
      },
    ])

    expect(graph.nodes.find((node) => node.canonicalId === 'returned')?.terminal).toBe(true)
    expect(graph.nodes.find((node) => node.canonicalId === 'failed')?.terminal).toBe(true)
    expect(graph.edges).toContainEqual(expect.objectContaining({
      canonicalEdgeId: 'edge-child-accepted',
      kind: 'control',
      role: 'accepted',
      target: processPresentationElementId('process-node', 'choice'),
    }))
    expect(graph.edges).toContainEqual(expect.objectContaining({
      kind: 'reciprocal',
      source: processPresentationElementId('process-node', 'fork'),
      target: processPresentationElementId('process-node', 'join'),
    }))
  })

  it('keeps unknown future constructs visible and diagnoses them', () => {
    const document = createDocument([
      { $node: 'futureNode', id: 'future' },
    ], 'future')

    const result = projectCanonicalProcessDocument(document)

    expect(result.graph?.nodes).toContainEqual(expect.objectContaining({
      canonicalId: 'future',
      category: 'process-node',
      processNodeKind: 'futureNode',
    }))
    expect(result.diagnostics).toContainEqual(expect.objectContaining({
      code: 'PROCESS_NODE_KIND_UNSUPPORTED',
      subject: 'future',
    }))
  })

  it('retains malformed control flow as an unresolved node plus diagnostic', () => {
    const document = createDocument([
      {
        $node: 'timer',
        dueAt: expression,
        id: 'timer',
        next: { id: 'edge-missing', target: 'missing' },
      },
    ], 'timer')

    const result = projectCanonicalProcessDocument(document)

    expect(result.graph?.nodes).toContainEqual(expect.objectContaining({
      canonicalId: 'missing',
      category: 'unresolved-node',
    }))
    expect(result.graph?.edges).toContainEqual(expect.objectContaining({
      canonicalEdgeId: 'edge-missing',
      target: processPresentationElementId('unresolved-node', 'missing'),
    }))
    expect(result.diagnostics).toContainEqual(expect.objectContaining({
      code: 'PROCESS_EDGE_TARGET_UNRESOLVED',
      subject: 'missing',
    }))
  })

  it('diagnoses duplicate canonical control-edge identities without emitting ambiguous graph edges', () => {
    const document = createDocument([
      {
        $node: 'timer',
        dueAt: expression,
        id: 'first',
        next: { id: 'edge-duplicated', target: 'returned' },
      },
      {
        $node: 'timer',
        dueAt: expression,
        id: 'second',
        next: { id: 'edge-duplicated', target: 'returned' },
      },
      {
        $node: 'return',
        id: 'returned',
        value: expression,
      },
    ], 'first')

    const result = projectCanonicalProcessDocument(document)

    expect(result.graph?.edges.filter((edge) => edge.canonicalEdgeId === 'edge-duplicated')).toHaveLength(1)
    expect(result.diagnostics).toContainEqual(expect.objectContaining({
      code: 'PROCESS_ELEMENT_ID_DUPLICATED',
      subject: 'edge-duplicated',
    }))
  })

  it('rejects unsupported definition kinds and schemas before interpreting a payload', () => {
    const wrongKind = representativeDocument() as unknown as Record<string, unknown>
    wrongKind.kind = 'transition'
    const wrongSchema = representativeDocument() as unknown as {
      metadata: Record<string, unknown>
    }
    wrongSchema.metadata.schemaVersion = 'cohesive-execution/v99'

    const kindResult = projectCanonicalProcessDocument(
      wrongKind as unknown as ExecutionDefinitionDocument,
    )
    const schemaResult = projectCanonicalProcessDocument(
      wrongSchema as unknown as ExecutionDefinitionDocument,
    )

    expect(kindResult.graph).toBeNull()
    expect(kindResult.diagnostics[0]?.code).toBe('PROCESS_DEFINITION_KIND_UNSUPPORTED')
    expect(schemaResult.graph).toBeNull()
    expect(schemaResult.diagnostics[0]?.code).toBe('PROCESS_DEFINITION_SCHEMA_UNSUPPORTED')
    expect(canonicalProcessPresentationCompatibility.schemaVersions).toEqual([
      'cohesive-execution/v3',
    ])
  })
})

function representativeDocument(): ExecutionDefinitionDocument {
  return createDocument([
    {
      $node: 'invokeProcess',
      cancellation: 'Propagate',
      contract: { definition: requestContractReference },
      id: 'invoke-child',
      input: expression,
      outcomeMapping: {
        cancelled: 'cancelled',
        completed: 'accepted',
        failed: 'rejected',
        terminated: 'terminated',
      },
      outcomes: [
        {
          continuation: {
            edge: { id: 'edge-child-accepted', target: 'choice' },
            output: { binding: 'child-result', contract: valueContract },
          },
          id: 'child-accepted',
          outcome: 'accepted',
        },
        {
          continuation: {
            edge: { id: 'edge-child-rejected', target: 'failed' },
          },
          id: 'child-rejected',
          outcome: 'rejected',
        },
      ],
      process: childReference,
      purpose: 'Work',
    },
    {
      $node: 'choice',
      cases: [
        {
          id: 'eligible-case',
          next: { id: 'edge-choice-selected', target: 'fork' },
          predicate: expression,
        },
      ],
      completeness: 'Fallback',
      fallback: {
        id: 'choice-fallback',
        next: { id: 'edge-choice-fallback', target: 'failed' },
      },
      id: 'choice',
      selection: 'OrderedFirstMatch',
    },
    {
      $node: 'fork',
      branches: [
        {
          capacityDomain: 'provider',
          id: 'provider-branch',
          start: { id: 'edge-provider-branch', target: 'await' },
        },
      ],
      capacityDomains: [{ identity: 'provider', maximumParallelism: 1 }],
      id: 'fork',
      join: 'join',
      limits: {
        maximumItems: 1,
        maximumParallelism: 1,
        maximumStartsPerActivation: 1,
        minimumParallelism: 1,
      },
    },
    {
      $node: 'join',
      fork: 'fork',
      id: 'join',
      next: { id: 'edge-join', target: 'returned' },
      policy: {
        cancellation: 'CancelRemaining',
        completionOrder: 'Unobservable',
        failure: 'FailFast',
        mode: 'All',
        requiredCount: 1,
        tieBreak: 'BranchIdentity',
      },
      result: {
        branches: [{ branch: 'provider-branch', result: expression }],
        output: { binding: 'joined', contract: valueContract },
        resultContract: valueContract,
      },
    },
    {
      $node: 'awaitMatch',
      arbitration: 'ExclusivePriorityThenClauseId',
      clauses: [
        {
          $clause: 'interaction',
          continuation: { edge: { id: 'edge-interaction', target: 'request' } },
          contract: { $contract: 'request', definition: requestContractReference },
          guard: expression,
          id: 'interaction-clause',
          input: { binding: 'interaction', contract: valueContract },
          priority: 10,
          requestObligation: { binding: 'request-obligation' },
        },
        {
          $clause: 'timer',
          continuation: { edge: { id: 'edge-await-timeout', target: 'failed' } },
          dueAt: expression,
          id: 'timer-clause',
          priority: 0,
        },
      ],
      duplicateInput: 'Reject',
      id: 'await',
      lateInput: 'Observe',
      missingTarget: 'DeadLetter',
      retentionHorizon: { ticks: 1 },
      staleInput: 'Reject',
    },
    {
      $node: 'request',
      contract: { definition: requestContractReference },
      id: 'request',
      outcomes: [
        {
          continuation: { edge: { id: 'edge-request', target: 'match' } },
          id: 'request-accepted',
          outcome: 'accepted',
        },
      ],
      payload: expression,
    },
    {
      $node: 'match',
      cases: [
        {
          id: 'match-case',
          next: { id: 'edge-match', target: 'returned' },
          pattern: { contract: valueContract, state: 'Concrete', value: 'done' },
        },
      ],
      completeness: 'Fallback',
      contract: valueContract,
      fallback: {
        id: 'match-fallback',
        next: { id: 'edge-match-fallback', target: 'failed' },
      },
      id: 'match',
      selection: 'OrderedFirstMatch',
      value: expression,
    },
    { $node: 'return', id: 'returned', result: expression },
    { $node: 'fail', id: 'failed', result: expression },
  ], 'invoke-child', [
    {
      description: 'Child invocation',
      reference: 'training-process.cs:42',
      semanticPath: { segments: ['nodes', '0'] },
    },
    {
      description: 'Accepted continuation',
      reference: 'training-process.cs:48',
      semanticPath: { segments: ['nodes', '0', 'outcomes', '0'] },
    },
  ])
}

function createDocument(
  nodes: readonly unknown[],
  entry: string,
  sourceMapEntries: readonly unknown[] = [],
): ExecutionDefinitionDocument {
  return {
    definition: {
      entry,
      input: valueContract,
      nodes,
      recoveryPolicy: 'ContinueAttempt',
      result: valueContract,
    },
    extensions: [],
    kind: 'process',
    metadata: {
      definitionId: 'training-process',
      diagnostics: [],
      fingerprint: {
        algorithm: 'sha256',
        canonicalization: 'cohesive-canonical-json/v1',
        value: 'process-fingerprint',
      },
      provenance: {
        origin: { authority: 'tests', location: 'training-process.cs' },
        producer: { producer: 'tests', version: '1' },
        source: { reference: 'training-process.cs' },
      },
      revisionId: 'training-process/r5',
      schemaVersion: 'cohesive-execution/v3',
      sourceMap: { entries: sourceMapEntries },
    },
  } as unknown as ExecutionDefinitionDocument
}
