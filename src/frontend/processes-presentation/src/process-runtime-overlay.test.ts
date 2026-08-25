import { describe, expect, it } from 'vitest'
import {
  type ExecutionDefinitionDocument,
  type ExecutionDefinitionReference,
  type ExecutionStatus,
  type ProcessExecutionTraceArtifact,
  type ValueContract,
} from '@cohesivesystems/processes'
import {
  processPresentationElementId,
  projectCanonicalProcessDocument,
} from './process-presentation-graph'
import {
  canonicalProcessRuntimePresentationCompatibility,
  processRuntimePresentationOverlayId,
  projectCanonicalProcessRuntime,
} from './process-runtime-overlay'

const processReference: ExecutionDefinitionReference = {
  definitionId: 'training-process',
  fingerprint: {
    algorithm: 'sha256',
    canonicalization: 'cohesive-canonical-json/v1',
    value: 'process-fingerprint',
  },
  revisionId: 'training-process/r5',
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

const requestReference: ExecutionDefinitionReference = {
  definitionId: 'training-request',
  fingerprint: {
    algorithm: 'sha256',
    canonicalization: 'cohesive-canonical-json/v1',
    value: 'request-fingerprint',
  },
  revisionId: 'training-request/r2',
}

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

const expression = { $expr: 'parameter', parameter: 'input' } as const

describe('canonical Process runtime presentation projection', () => {
  it('joins status, waits, Request outcomes, and higher-order lineage deterministically', () => {
    const graph = projectCanonicalProcessDocument(representativeDocument()).graph!
    const status = representativeStatus()
    const trace = representativeTraceArtifact()

    const first = projectCanonicalProcessRuntime(graph, status, trace)
    const second = projectCanonicalProcessRuntime(graph, status, trace)
    const reordered = projectCanonicalProcessRuntime({
      ...graph,
      edges: [...graph.edges].reverse(),
      nodes: [...graph.nodes].reverse(),
    }, status, trace)

    expect(first).toEqual(second)
    expect(first).toEqual(reordered)
    expect(first.overlay).not.toBeNull()
    const overlay = first.overlay!
    status.controlMode = 'Paused'
    trace.missingTracePrefixCount = 99
    expect(Object.isFrozen(overlay)).toBe(true)
    expect(Object.isFrozen(overlay.status)).toBe(true)
    expect(overlay.status.controlMode).toBe('Running')
    expect(overlay.id).toBe(processRuntimePresentationOverlayId(
      status.processInstanceId,
      status.definition,
    ))
    expect(overlay.projectionVersion).toBe(
      canonicalProcessRuntimePresentationCompatibility.projectionVersion,
    )
    expect(overlay.missingTracePrefixCount).toBe(2)
    expect(overlay.unmatchedEvidence).toEqual([])
    expect(overlay.diagnostics).toContainEqual(expect.objectContaining({
      code: 'PROCESS_RUNTIME_TRACE_PREFIX_MISSING',
    }))
    expect(overlay.diagnostics).toContainEqual(expect.objectContaining({
      code: 'PROCESS_RUNTIME_OCCURRENCE_DISCLOSURE_GAP',
      subject: 'Recurrence',
    }))

    const request = elementOverlay(overlay, processPresentationElementId('process-node', 'request'))
    expect(request.tokens).toContainEqual(expect.objectContaining({
      token: expect.objectContaining({ tokenId: 'token-request', disposition: 'Waiting' }),
    }))
    expect(request.waits).toContainEqual(expect.objectContaining({
      wait: expect.objectContaining({
        deadlineUtc: '2026-08-25T04:00:00.0000000+00:00',
        tokenId: 'token-request',
      }),
    }))

    const accepted = elementOverlay(
      overlay,
      processPresentationElementId('request-outcome', 'request-accepted', 'request'),
    )
    const outcomeEvent = accepted.traceEvents.find(
      (event) => event.event.requestOutcome === 'accepted',
    )!
    expect(outcomeEvent.requestOutcomeElementId).toBe(accepted.elementId)
    expect(outcomeEvent.event.emission).toBe('emission-request-1')
    expect(outcomeEvent.event.correlation).toBe('correlation-request-1')

    const child = elementOverlay(
      overlay,
      processPresentationElementId('process-node', 'invoke-child'),
    )
    const childEvent = child.traceEvents.find(
      (event) => event.event.processOccurrence?.kind === 'Child',
    )!
    expect(childEvent.event.processOccurrence).toEqual(expect.objectContaining({
      continuation: {
        processAttemptId: 'child-attempt-4',
        processInstanceId: 'child-instance-9',
      },
      definition: childReference,
      disclosure: 'Disclosed',
      occurrence: '1',
      registrationId: 'child-registration-1',
    }))
    expect(childEvent.occurrenceDefinitionElementId).not.toBeNull()

    const partition = elementOverlay(
      overlay,
      processPresentationElementId('process-node', 'partition'),
    )
    expect(partition.traceEvents).toContainEqual(expect.objectContaining({
      event: expect.objectContaining({
        processOccurrence: expect.objectContaining({
          kind: 'Partition',
          progressIdentity: 'partner-42',
        }),
      }),
    }))
    expect(partition.traceEvents.find(
      (event) => event.event.requestOutcome === 'accepted',
    )).toEqual(expect.objectContaining({
      primaryElementId: partition.elementId,
      requestOutcomeElementId: null,
    }))

    const recurrence = elementOverlay(
      overlay,
      processPresentationElementId('process-node', 'repeat'),
    )
    expect(recurrence.traceEvents).toContainEqual(expect.objectContaining({
      event: expect.objectContaining({
        processOccurrence: expect.objectContaining({
          kind: 'Recurrence',
          repeatCount: 3,
          unchangedProgressCount: 1,
        }),
      }),
    }))
  })

  it('fails closed when graph, status, or trace compatibility is not exact', () => {
    const graph = projectCanonicalProcessDocument(representativeDocument()).graph!
    const wrongStatus: ExecutionStatus = {
      ...representativeStatus(),
      definition: { ...processReference, revisionId: 'training-process/r6' },
    }
    const wrongInstance: ProcessExecutionTraceArtifact = {
      ...representativeTraceArtifact(),
      processInstanceId: 'another-instance',
    }

    const definitionResult = projectCanonicalProcessRuntime(graph, wrongStatus)
    const instanceResult = projectCanonicalProcessRuntime(
      graph,
      representativeStatus(),
      wrongInstance,
    )
    const statusOnlyResult = projectCanonicalProcessRuntime(graph, representativeStatus())

    expect(definitionResult.overlay).toBeNull()
    expect(definitionResult.diagnostics).toContainEqual(expect.objectContaining({
      code: 'PROCESS_RUNTIME_DEFINITION_MISMATCH',
      severity: 'error',
    }))
    expect(instanceResult.overlay).toBeNull()
    expect(instanceResult.diagnostics).toContainEqual(expect.objectContaining({
      code: 'PROCESS_RUNTIME_INSTANCE_MISMATCH',
      severity: 'error',
    }))
    expect(statusOnlyResult.overlay).not.toBeNull()
    expect(statusOnlyResult.diagnostics).toContainEqual(expect.objectContaining({
      code: 'PROCESS_RUNTIME_TRACE_UNAVAILABLE',
      severity: 'warning',
    }))
  })

  it('accounts for every unmatched reference and never assigns global terminal evidence to a node', () => {
    const graph = projectCanonicalProcessDocument(representativeDocument()).graph!
    const status: ExecutionStatus = {
      ...representativeStatus(),
      runtime: {
        ...representativeStatus().runtime,
        capacity: undefined,
        capacityDisclosure: 'Unknown',
        progress: undefined,
        progressDisclosure: 'Redacted',
        tokens: [
          { tokenId: 'missing-ready-token', node: 'missing-node', disposition: 'Ready' },
          { tokenId: 'missing-wait-token', node: 'missing-wait-node', disposition: 'Waiting' },
        ],
        waits: [{
          deadlineUtc: null,
          node: 'missing-wait-node',
          tokenId: 'missing-wait-token',
          waitingSinceUtc: '2026-08-25T03:00:00.0000000+00:00',
        }],
      },
      terminalOutcome: {
        detail: { contract: valueContract, disclosure: 'Redacted' },
        kind: 'Completed',
        occurredAtUtc: '2026-08-25T03:05:00.0000000+00:00',
      },
    }
    const trace: ProcessExecutionTraceArtifact = {
      ...representativeTraceArtifact(),
      missingTracePrefixCount: 0,
      traces: [{
        ...representativeTraceArtifact().traces[0]!,
        events: [{
          branchOrClause: 'missing-branch',
          kind: 'Observed',
          node: 'missing-node',
          relatedNode: 'orphan-related-node',
          requestOutcome: 'missing-outcome',
          sequence: 99,
          sourceReferences: [],
        }],
      }],
    }

    const result = projectCanonicalProcessRuntime(graph, status, trace)

    expect(result.overlay).not.toBeNull()
    expect(result.overlay!.status.terminalOutcome.kind).toBe('Completed')
    expect(result.overlay!.elementOverlays.every((element) =>
      element.tokens.length === 0
      && element.waits.length === 0
      && element.traceEvents.length === 0)).toBe(true)
    expect(new Set(result.overlay!.unmatchedEvidence.map((item) => item.kind))).toEqual(new Set([
      'status-token-node',
      'status-wait-node',
      'trace-branch-or-clause',
      'trace-node',
      'trace-related-node',
      'trace-request-outcome',
    ]))
    expect(result.overlay!.diagnostics).toEqual(expect.arrayContaining([
      expect.objectContaining({ code: 'PROCESS_RUNTIME_STATUS_FACET_UNKNOWN', subject: 'capacity' }),
      expect.objectContaining({ code: 'PROCESS_RUNTIME_STATUS_FACET_REDACTED', subject: 'progress' }),
      expect.objectContaining({
        code: 'PROCESS_RUNTIME_STATUS_FACET_REDACTED',
        subject: 'terminalOutcome.detail',
      }),
      expect.objectContaining({ code: 'PROCESS_RUNTIME_TOKEN_NODE_UNMATCHED' }),
      expect.objectContaining({ code: 'PROCESS_RUNTIME_WAIT_NODE_UNMATCHED' }),
      expect.objectContaining({ code: 'PROCESS_RUNTIME_TRACE_NODE_UNMATCHED' }),
      expect.objectContaining({ code: 'PROCESS_RUNTIME_TRACE_BRANCH_UNMATCHED' }),
      expect.objectContaining({ code: 'PROCESS_RUNTIME_TRACE_OUTCOME_UNMATCHED' }),
    ]))
  })
})

function elementOverlay(
  overlay: NonNullable<ReturnType<typeof projectCanonicalProcessRuntime>['overlay']>,
  elementId: string,
) {
  return overlay.elementOverlays.find((element) => element.elementId === elementId)!
}

function representativeStatus(): ExecutionStatus {
  return {
    activeActivation: null,
    attempts: [{
      attemptId: 'attempt-1',
      completedActivationCount: '1',
      disposition: 'Current',
      endedAtUtc: null,
      lastSafePointId: 'safe-point-1',
      lastSafePointNode: 'request',
      phase: 'AtSafePoint',
      startedAtUtc: '2026-08-25T02:00:00.0000000+00:00',
    }],
    controlMode: 'Running',
    controlRevision: '2',
    createdAtUtc: '2026-08-25T02:00:00.0000000+00:00',
    currentAttemptId: 'attempt-1',
    definition: processReference,
    processInstanceId: 'process-instance-7',
    runtime: {
      capacity: { active: '1', limit: '4' },
      capacityDisclosure: 'Disclosed',
      demand: { delayed: '1', ready: '2' },
      demandDisclosure: 'Disclosed',
      extensions: [],
      health: 'Healthy',
      progress: { completed: '2', total: '5', unit: 'items' },
      progressDisclosure: 'Disclosed',
      tokens: [{ tokenId: 'token-request', node: 'request', disposition: 'Waiting' }],
      tokensDisclosure: 'Disclosed',
      waits: [{
        deadlineUtc: '2026-08-25T04:00:00.0000000+00:00',
        node: 'request',
        tokenId: 'token-request',
        waitingSinceUtc: '2026-08-25T03:00:00.0000000+00:00',
      }],
      waitsDisclosure: 'Disclosed',
    },
    schemaVersion: 'cohesive-execution-status/v1',
    terminalOutcome: { kind: 'None' },
    updatedAtUtc: '2026-08-25T03:05:00.0000000+00:00',
  }
}

function representativeTraceArtifact(): ProcessExecutionTraceArtifact {
  return {
    definition: processReference,
    missingTracePrefixCount: 2,
    processInstanceId: 'process-instance-7',
    schemaVersion: 'cohesive-process-execution-traces/v2',
    traces: [{
      activation: 'activation-1',
      continuation: {
        processAttemptId: 'attempt-1',
        processInstanceId: 'process-instance-7',
      },
      definition: processReference,
      disposition: 'Waiting',
      durableCommitSequence: '1',
      events: [
        {
          kind: 'WaitRegistered',
          node: 'request',
          sequence: 1,
          sourceReferences: [],
          token: 'token-request',
          waitRegistrationId: 'wait-registration-1',
        },
        {
          correlation: 'correlation-request-1',
          emission: 'emission-request-1',
          kind: 'RequestOutcomeObserved',
          node: 'request',
          relatedDefinition: requestReference,
          requestOutcome: 'accepted',
          sequence: 2,
          sourceReferences: ['provider.cs:20'],
          token: 'token-request',
        },
        {
          kind: 'ChildRegistered',
          node: 'invoke-child',
          processOccurrence: {
            continuation: {
              processAttemptId: 'child-attempt-4',
              processInstanceId: 'child-instance-9',
            },
            definition: childReference,
            disclosure: 'Disclosed',
            kind: 'Child',
            occurrence: '1',
            ownerToken: 'token-child',
            registrationId: 'child-registration-1',
          },
          relatedDefinition: childReference,
          relatedNode: 'child-entry',
          sequence: 3,
          sourceReferences: ['training-process.cs:30'],
          token: 'token-child',
        },
        {
          kind: 'PartitionChildRegistered',
          node: 'partition',
          processOccurrence: {
            disclosure: 'Disclosed',
            kind: 'Partition',
            occurrence: '2',
            ownerToken: 'token-partition',
            progressIdentity: 'partner-42',
            registrationId: 'partition-registration-2',
          },
          sequence: 4,
          sourceReferences: [],
          token: 'token-partition',
        },
        {
          kind: 'RecurrenceAdvanced',
          node: 'repeat',
          processOccurrence: {
            disclosure: 'Disclosed',
            kind: 'Recurrence',
            occurrence: '3',
            ownerToken: 'token-repeat',
            registrationId: 'recurrence-registration-3',
            repeatCount: 3,
            unchangedProgressCount: 1,
          },
          sequence: 5,
          sourceReferences: [],
          token: 'token-repeat',
        },
        {
          kind: 'RecurrenceAdvanced',
          node: 'repeat',
          processOccurrence: {
            disclosure: 'Unsupported',
            kind: 'Recurrence',
          },
          sequence: 6,
          sourceReferences: [],
          token: 'token-repeat',
        },
        {
          emission: 'emission-partition-2',
          kind: 'WaitResolved',
          node: 'partition',
          requestOutcome: 'accepted',
          sequence: 7,
          sourceReferences: [],
          token: 'token-partition',
        },
      ],
      kind: 'process',
      safePointNode: 'request',
      schemaVersion: 'cohesive-execution-trace/v2',
    }],
  }
}

function representativeDocument(): ExecutionDefinitionDocument {
  return {
    definition: {
      entry: 'request',
      input: valueContract,
      nodes: [
        {
          $node: 'request',
          contract: { definition: requestReference },
          id: 'request',
          outcomes: [{
            continuation: { edge: { id: 'edge-request-accepted', target: 'invoke-child' } },
            id: 'request-accepted',
            outcome: 'accepted',
          }],
          payload: expression,
        },
        {
          $node: 'invokeProcess',
          cancellation: 'Propagate',
          contract: { definition: requestReference },
          id: 'invoke-child',
          input: expression,
          outcomeMapping: {
            cancelled: 'cancelled',
            completed: 'accepted',
            failed: 'rejected',
            terminated: 'terminated',
          },
          outcomes: [{
            continuation: { edge: { id: 'edge-child-accepted', target: 'partition' } },
            id: 'child-accepted',
            outcome: 'accepted',
          }],
          process: childReference,
          purpose: 'Work',
        },
        {
          $node: 'forEachPartition',
          cancellation: 'Propagate',
          capacityDomains: [],
          childInput: expression,
          completed: { id: 'edge-partition-completed', target: 'repeat' },
          contract: { definition: requestReference },
          failed: { id: 'edge-partition-failed', target: 'failed' },
          failure: 'AwaitAll',
          id: 'partition',
          limits: {
            maximumItems: 10,
            maximumParallelism: 2,
            maximumStartsPerActivation: 2,
            minimumParallelism: 1,
          },
          outcomeMapping: {
            cancelled: 'cancelled',
            completed: 'accepted',
            failed: 'rejected',
            terminated: 'terminated',
          },
          partition: { binding: 'partition-item', contract: valueContract },
          partitions: expression,
          process: childReference,
          progressIdentity: expression,
        },
        {
          $node: 'repeatAcrossActivation',
          completed: { id: 'edge-repeat-completed', target: 'returned' },
          continueWhen: expression,
          exhausted: { id: 'edge-repeat-exhausted', target: 'failed' },
          id: 'repeat',
          policy: { maximumOccurrences: 10, maximumUnchangedProgressOccurrences: 2 },
          progress: expression,
          progressContract: valueContract,
          repeat: { id: 'edge-repeat', target: 'request' },
          stalled: { id: 'edge-repeat-stalled', target: 'failed' },
        },
        { $node: 'return', id: 'returned', result: expression },
        { $node: 'fail', id: 'failed', result: expression },
      ],
      recoveryPolicy: 'ContinueAttempt',
      result: valueContract,
    },
    extensions: [],
    kind: 'process',
    metadata: {
      definitionId: processReference.definitionId,
      diagnostics: [],
      fingerprint: processReference.fingerprint,
      provenance: {
        origin: { authority: 'tests', location: 'training-process.cs' },
        producer: { producer: 'tests', version: '1' },
        source: { reference: 'training-process.cs' },
      },
      revisionId: processReference.revisionId,
      schemaVersion: 'cohesive-execution/v3',
      sourceMap: { entries: [] },
    },
  } as unknown as ExecutionDefinitionDocument
}
