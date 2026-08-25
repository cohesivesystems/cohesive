import {
  type ExecutionDefinitionReference,
  type ExecutionStatus,
  type ExecutionStatusDisclosure,
  type ExecutionTokenStatus,
  type ExecutionWaitStatus,
  type NormalizedExecutionTrace,
  type NormalizedExecutionTraceEvent,
  type ProcessExecutionTraceArtifact,
} from '@cohesivesystems/processes'
import {
  type ProcessPresentationGraph,
  type ProcessPresentationNode,
} from './process-presentation-graph'
import {
  cloneWireValue,
  compareOrdinal,
  deepFreeze,
  isRecord,
  type DeepReadonly,
} from './process-presentation-values'

export const canonicalProcessRuntimePresentationCompatibility = Object.freeze({
  processPresentationProjectionVersions: Object.freeze([
    'cohesive-process-presentation/v1',
  ] as const),
  statusSchemaVersions: Object.freeze([
    'cohesive-execution-status/v1',
  ] as const),
  traceArtifactSchemaVersions: Object.freeze([
    'cohesive-process-execution-traces/v2',
  ] as const),
  traceSchemaVersions: Object.freeze([
    'cohesive-execution-trace/v2',
  ] as const),
  projectionVersion: 'cohesive-process-runtime-presentation/v1',
})

export type ProcessRuntimePresentationDiagnosticSeverity = 'error' | 'warning' | 'info'

export type ProcessRuntimePresentationDiagnosticCode =
  | 'PROCESS_RUNTIME_DEFINITION_MISMATCH'
  | 'PROCESS_RUNTIME_GRAPH_PROJECTION_UNSUPPORTED'
  | 'PROCESS_RUNTIME_HEALTH_UNKNOWN'
  | 'PROCESS_RUNTIME_INSTANCE_MISMATCH'
  | 'PROCESS_RUNTIME_OCCURRENCE_DISCLOSURE_GAP'
  | 'PROCESS_RUNTIME_STATUS_FACET_REDACTED'
  | 'PROCESS_RUNTIME_STATUS_FACET_UNKNOWN'
  | 'PROCESS_RUNTIME_STATUS_SCHEMA_UNSUPPORTED'
  | 'PROCESS_RUNTIME_TRACE_ARTIFACT_SCHEMA_UNSUPPORTED'
  | 'PROCESS_RUNTIME_TRACE_ATTEMPT_UNMATCHED'
  | 'PROCESS_RUNTIME_TRACE_BRANCH_UNMATCHED'
  | 'PROCESS_RUNTIME_TRACE_DEFINITION_REFERENCE_UNMATCHED'
  | 'PROCESS_RUNTIME_TRACE_KIND_UNSUPPORTED'
  | 'PROCESS_RUNTIME_TRACE_NODE_UNMATCHED'
  | 'PROCESS_RUNTIME_TRACE_OUTCOME_UNMATCHED'
  | 'PROCESS_RUNTIME_TRACE_PREFIX_MISSING'
  | 'PROCESS_RUNTIME_TRACE_SCHEMA_UNSUPPORTED'
  | 'PROCESS_RUNTIME_TRACE_UNAVAILABLE'
  | 'PROCESS_RUNTIME_WAIT_NODE_UNMATCHED'
  | 'PROCESS_RUNTIME_TOKEN_NODE_UNMATCHED'

export interface ProcessRuntimePresentationDiagnostic {
  readonly code: ProcessRuntimePresentationDiagnosticCode
  readonly evidenceId: string | null
  readonly id: string
  readonly message: string
  readonly path: readonly string[]
  readonly severity: ProcessRuntimePresentationDiagnosticSeverity
  readonly subject: string | null
}

export interface ProcessRuntimeTokenOverlay {
  readonly id: string
  readonly token: DeepReadonly<ExecutionTokenStatus>
}

export interface ProcessRuntimeWaitOverlay {
  readonly id: string
  readonly wait: DeepReadonly<ExecutionWaitStatus>
}

export interface ProcessRuntimeTraceEventOverlay {
  readonly activationId: string
  readonly attemptId: string
  readonly branchOrClauseElementId: string | null
  readonly elementIds: readonly string[]
  readonly event: DeepReadonly<NormalizedExecutionTraceEvent>
  readonly id: string
  readonly occurrenceDefinitionElementId: string | null
  readonly primaryElementId: string | null
  readonly relatedDefinitionElementId: string | null
  readonly relatedNodeElementId: string | null
  readonly requestOutcomeElementId: string | null
  readonly traceDisposition: string
}

export interface ProcessRuntimeElementOverlay {
  readonly elementId: string
  readonly id: string
  readonly traceEvents: readonly ProcessRuntimeTraceEventOverlay[]
  readonly tokens: readonly ProcessRuntimeTokenOverlay[]
  readonly waits: readonly ProcessRuntimeWaitOverlay[]
}

export type ProcessRuntimeUnmatchedEvidenceKind =
  | 'definition-reference'
  | 'status-token-node'
  | 'status-wait-node'
  | 'trace-branch-or-clause'
  | 'trace-node'
  | 'trace-related-node'
  | 'trace-request-outcome'

export type ProcessRuntimeUnmatchedEvidenceSource =
  | ExecutionTokenStatus
  | ExecutionWaitStatus
  | NormalizedExecutionTraceEvent

export interface ProcessRuntimeUnmatchedEvidence {
  readonly evidenceId: string
  readonly id: string
  readonly kind: ProcessRuntimeUnmatchedEvidenceKind
  readonly reference: string
  readonly source: DeepReadonly<ProcessRuntimeUnmatchedEvidenceSource>
}

export interface ProcessRuntimePresentationOverlay {
  readonly definition: DeepReadonly<ExecutionDefinitionReference>
  readonly diagnostics: readonly ProcessRuntimePresentationDiagnostic[]
  readonly elementOverlays: readonly ProcessRuntimeElementOverlay[]
  readonly id: string
  readonly missingTracePrefixCount: number
  readonly processInstanceId: string
  readonly projectionVersion: string
  readonly status: DeepReadonly<ExecutionStatus>
  readonly traceArtifact: DeepReadonly<ProcessExecutionTraceArtifact> | null
  readonly unmatchedEvidence: readonly ProcessRuntimeUnmatchedEvidence[]
}

export interface ProcessRuntimePresentationProjectionResult {
  readonly diagnostics: readonly ProcessRuntimePresentationDiagnostic[]
  readonly overlay: ProcessRuntimePresentationOverlay | null
}

interface MutableProcessRuntimeElementOverlay {
  readonly elementId: string
  readonly traceEvents: ProcessRuntimeTraceEventOverlay[]
  readonly tokens: ProcessRuntimeTokenOverlay[]
  readonly waits: ProcessRuntimeWaitOverlay[]
}

interface GraphIndex {
  readonly definitionReferences: readonly ProcessPresentationNode[]
  readonly elements: Map<string, MutableProcessRuntimeElementOverlay>
  readonly nodes: readonly ProcessPresentationNode[]
  readonly processNodesByCanonicalId: Map<string, readonly ProcessPresentationNode[]>
}

interface RuntimeProjectionContext {
  readonly diagnostics: Map<string, ProcessRuntimePresentationDiagnostic>
  readonly graph: ProcessPresentationGraph
  readonly index: GraphIndex
  readonly processInstanceId: string
  readonly unmatchedEvidence: Map<string, ProcessRuntimeUnmatchedEvidence>
}

export function processRuntimePresentationOverlayId(
  processInstanceId: string,
  definition: ExecutionDefinitionReference,
): string {
  return runtimeIdentity(
    'overlay',
    processInstanceId,
    definition.definitionId,
    definition.revisionId,
    definition.fingerprint.algorithm,
    definition.fingerprint.canonicalization,
    definition.fingerprint.value,
  )
}

export function projectCanonicalProcessRuntime(
  graph: ProcessPresentationGraph,
  status: ExecutionStatus,
  traceArtifact: ProcessExecutionTraceArtifact | null = null,
): ProcessRuntimePresentationProjectionResult {
  const diagnostics = new Map<string, ProcessRuntimePresentationDiagnostic>()
  validateCompatibility(graph, status, traceArtifact, diagnostics)
  if ([...diagnostics.values()].some((diagnostic) => diagnostic.severity === 'error')) {
    return freezeProjectionResult(null, diagnostics)
  }

  const index = createGraphIndex(graph)
  const context: RuntimeProjectionContext = {
    diagnostics,
    graph,
    index,
    processInstanceId: status.processInstanceId,
    unmatchedEvidence: new Map(),
  }

  projectStatus(status, context)
  if (traceArtifact) {
    projectTraceArtifact(traceArtifact, context)
  } else {
    addDiagnostic(
      context.diagnostics,
      'PROCESS_RUNTIME_TRACE_UNAVAILABLE',
      'warning',
      ['traceArtifact'],
      'No retained trace artifact was supplied; event absence cannot imply execution or element completion.',
      null,
    )
  }

  const frozenDiagnostics = freezeDiagnostics(diagnostics)
  const overlay: ProcessRuntimePresentationOverlay = deepFreeze({
    definition: cloneWireValue(status.definition),
    diagnostics: frozenDiagnostics,
    elementOverlays: [...index.elements.values()]
      .sort((left, right) => compareOrdinal(left.elementId, right.elementId))
      .map((element) => ({
        elementId: element.elementId,
        id: runtimeIdentity('element', status.processInstanceId, element.elementId),
        traceEvents: element.traceEvents.sort(compareEvidenceById),
        tokens: element.tokens.sort(compareEvidenceById),
        waits: element.waits.sort(compareEvidenceById),
      })),
    id: processRuntimePresentationOverlayId(status.processInstanceId, status.definition),
    missingTracePrefixCount: traceArtifact?.missingTracePrefixCount ?? 0,
    processInstanceId: status.processInstanceId,
    projectionVersion: canonicalProcessRuntimePresentationCompatibility.projectionVersion,
    status: cloneWireValue(status),
    traceArtifact: traceArtifact ? cloneWireValue(traceArtifact) : null,
    unmatchedEvidence: [...context.unmatchedEvidence.values()]
      .sort(compareEvidenceById),
  })

  return deepFreeze({ diagnostics: frozenDiagnostics, overlay })
}

function validateCompatibility(
  graph: ProcessPresentationGraph,
  status: ExecutionStatus,
  traceArtifact: ProcessExecutionTraceArtifact | null,
  diagnostics: Map<string, ProcessRuntimePresentationDiagnostic>,
) {
  if (!canonicalProcessRuntimePresentationCompatibility.processPresentationProjectionVersions.includes(
    graph.projectionVersion as (typeof canonicalProcessRuntimePresentationCompatibility.processPresentationProjectionVersions)[number],
  )) {
    addDiagnostic(
      diagnostics,
      'PROCESS_RUNTIME_GRAPH_PROJECTION_UNSUPPORTED',
      'error',
      ['graph', 'projectionVersion'],
      `Runtime presentation does not support Process graph projection '${graph.projectionVersion}'.`,
      graph.projectionVersion,
    )
  }

  if (!canonicalProcessRuntimePresentationCompatibility.statusSchemaVersions.includes(
    status.schemaVersion as (typeof canonicalProcessRuntimePresentationCompatibility.statusSchemaVersions)[number],
  )) {
    addDiagnostic(
      diagnostics,
      'PROCESS_RUNTIME_STATUS_SCHEMA_UNSUPPORTED',
      'error',
      ['status', 'schemaVersion'],
      `Runtime presentation does not support execution status schema '${status.schemaVersion}'.`,
      status.schemaVersion,
    )
  }

  if (!sameDefinitionReference(graph.document.definition, status.definition)) {
    addDiagnostic(
      diagnostics,
      'PROCESS_RUNTIME_DEFINITION_MISMATCH',
      'error',
      ['status', 'definition'],
      'Execution status does not identify the exact Process definition projected by the graph.',
      definitionReferenceSubject(status.definition),
    )
  }

  if (!traceArtifact) {
    return
  }

  if (!canonicalProcessRuntimePresentationCompatibility.traceArtifactSchemaVersions.includes(
    traceArtifact.schemaVersion as (typeof canonicalProcessRuntimePresentationCompatibility.traceArtifactSchemaVersions)[number],
  )) {
    addDiagnostic(
      diagnostics,
      'PROCESS_RUNTIME_TRACE_ARTIFACT_SCHEMA_UNSUPPORTED',
      'error',
      ['traceArtifact', 'schemaVersion'],
      `Runtime presentation does not support trace artifact schema '${traceArtifact.schemaVersion}'.`,
      traceArtifact.schemaVersion,
    )
  }

  if (!sameDefinitionReference(status.definition, traceArtifact.definition)) {
    addDiagnostic(
      diagnostics,
      'PROCESS_RUNTIME_DEFINITION_MISMATCH',
      'error',
      ['traceArtifact', 'definition'],
      'Trace artifact and execution status do not identify the same exact Process definition.',
      definitionReferenceSubject(traceArtifact.definition),
    )
  }

  if (status.processInstanceId !== traceArtifact.processInstanceId) {
    addDiagnostic(
      diagnostics,
      'PROCESS_RUNTIME_INSTANCE_MISMATCH',
      'error',
      ['traceArtifact', 'processInstanceId'],
      'Trace artifact and execution status do not identify the same Process instance.',
      traceArtifact.processInstanceId,
    )
  }

  const attempts = new Set(status.attempts.map((attempt) => attempt.attemptId))
  traceArtifact.traces.forEach((trace, traceIndex) => {
    if (!canonicalProcessRuntimePresentationCompatibility.traceSchemaVersions.includes(
      trace.schemaVersion as (typeof canonicalProcessRuntimePresentationCompatibility.traceSchemaVersions)[number],
    )) {
      addDiagnostic(
        diagnostics,
        'PROCESS_RUNTIME_TRACE_SCHEMA_UNSUPPORTED',
        'error',
        ['traceArtifact', 'traces', String(traceIndex), 'schemaVersion'],
        `Runtime presentation does not support normalized trace schema '${trace.schemaVersion}'.`,
        trace.schemaVersion,
      )
    }
    if (trace.kind !== graph.document.kind) {
      addDiagnostic(
        diagnostics,
        'PROCESS_RUNTIME_TRACE_KIND_UNSUPPORTED',
        'error',
        ['traceArtifact', 'traces', String(traceIndex), 'kind'],
        `Normalized trace kind '${trace.kind}' does not match graph kind '${graph.document.kind}'.`,
        trace.kind,
      )
    }
    if (!sameDefinitionReference(status.definition, trace.definition)) {
      addDiagnostic(
        diagnostics,
        'PROCESS_RUNTIME_DEFINITION_MISMATCH',
        'error',
        ['traceArtifact', 'traces', String(traceIndex), 'definition'],
        'Normalized trace does not identify the same exact Process definition as execution status.',
        definitionReferenceSubject(trace.definition),
      )
    }
    if (!trace.continuation
      || trace.continuation.processInstanceId !== status.processInstanceId) {
      addDiagnostic(
        diagnostics,
        'PROCESS_RUNTIME_INSTANCE_MISMATCH',
        'error',
        ['traceArtifact', 'traces', String(traceIndex), 'continuation'],
        'Normalized Process trace does not identify the status Process instance.',
        trace.continuation?.processInstanceId ?? null,
      )
    } else if (!attempts.has(trace.continuation.processAttemptId)) {
      addDiagnostic(
        diagnostics,
        'PROCESS_RUNTIME_TRACE_ATTEMPT_UNMATCHED',
        'error',
        ['traceArtifact', 'traces', String(traceIndex), 'continuation', 'processAttemptId'],
        'Normalized trace attempt is absent from the authoritative execution status lineage.',
        trace.continuation.processAttemptId,
      )
    }
  })
}

function projectStatus(status: ExecutionStatus, context: RuntimeProjectionContext) {
  discloseStatusFacet('tokens', status.runtime.tokensDisclosure, context)
  discloseStatusFacet('waits', status.runtime.waitsDisclosure, context)
  discloseStatusFacet('progress', status.runtime.progressDisclosure, context)
  discloseStatusFacet('demand', status.runtime.demandDisclosure, context)
  discloseStatusFacet('capacity', status.runtime.capacityDisclosure, context)
  if (status.terminalOutcome.detail) {
    discloseStatusValue(
      'terminalOutcome.detail',
      status.terminalOutcome.detail.disclosure,
      ['status', 'terminalOutcome', 'detail', 'disclosure'],
      context,
    )
  }
  status.runtime.extensions.forEach((extension, extensionIndex) => {
    discloseStatusValue(
      `extension:${extension.id}`,
      extension.value.disclosure,
      ['status', 'runtime', 'extensions', String(extensionIndex), 'value', 'disclosure'],
      context,
    )
  })

  if (status.runtime.health === 'Unknown') {
    addDiagnostic(
      context.diagnostics,
      'PROCESS_RUNTIME_HEALTH_UNKNOWN',
      'warning',
      ['status', 'runtime', 'health'],
      'The runtime did not disclose an authoritative health observation.',
      'health',
    )
  }

  status.runtime.tokens.forEach((token, tokenIndex) => {
    const id = runtimeIdentity('token', status.processInstanceId, token.tokenId)
    const node = findSingleProcessNode(token.node, context.index)
    if (!node) {
      addUnmatchedEvidence(
        context,
        'status-token-node',
        id,
        token.node,
        token,
        'PROCESS_RUNTIME_TOKEN_NODE_UNMATCHED',
        ['status', 'runtime', 'tokens', String(tokenIndex), 'node'],
        `Token '${token.tokenId}' references Process node '${token.node}', which is absent or ambiguous in the graph.`,
      )
      return
    }

    context.index.elements.get(node.id)!.tokens.push(deepFreeze({
      id,
      token: cloneWireValue(token),
    }))
  })

  status.runtime.waits.forEach((wait, waitIndex) => {
    const id = runtimeIdentity('wait', status.processInstanceId, wait.tokenId, wait.node)
    const node = findSingleProcessNode(wait.node, context.index)
    if (!node) {
      addUnmatchedEvidence(
        context,
        'status-wait-node',
        id,
        wait.node,
        wait,
        'PROCESS_RUNTIME_WAIT_NODE_UNMATCHED',
        ['status', 'runtime', 'waits', String(waitIndex), 'node'],
        `Wait for token '${wait.tokenId}' references Process node '${wait.node}', which is absent or ambiguous in the graph.`,
      )
      return
    }

    context.index.elements.get(node.id)!.waits.push(deepFreeze({
      id,
      wait: cloneWireValue(wait),
    }))
  })
}

function discloseStatusFacet(
  facet: string,
  disclosure: ExecutionStatusDisclosure,
  context: RuntimeProjectionContext,
) {
  if (disclosure === 'Disclosed') {
    return
  }

  const redacted = disclosure === 'Redacted'
  addDiagnostic(
    context.diagnostics,
    redacted
      ? 'PROCESS_RUNTIME_STATUS_FACET_REDACTED'
      : 'PROCESS_RUNTIME_STATUS_FACET_UNKNOWN',
    redacted ? 'info' : 'warning',
    ['status', 'runtime', `${facet}Disclosure`],
    redacted
      ? `Runtime ${facet} evidence exists but was redacted.`
      : `The runtime has no authoritative ${facet} observation.`,
    facet,
  )
}

function discloseStatusValue(
  subject: string,
  disclosure: ExecutionStatusDisclosure,
  path: readonly string[],
  context: RuntimeProjectionContext,
) {
  if (disclosure === 'Disclosed') {
    return
  }

  const redacted = disclosure === 'Redacted'
  addDiagnostic(
    context.diagnostics,
    redacted
      ? 'PROCESS_RUNTIME_STATUS_FACET_REDACTED'
      : 'PROCESS_RUNTIME_STATUS_FACET_UNKNOWN',
    redacted ? 'info' : 'warning',
    path,
    redacted
      ? `Runtime status value '${subject}' exists but was redacted.`
      : `The runtime status value '${subject}' is explicitly unknown.`,
    subject,
  )
}

function projectTraceArtifact(
  artifact: ProcessExecutionTraceArtifact,
  context: RuntimeProjectionContext,
) {
  if (artifact.missingTracePrefixCount > 0) {
    addDiagnostic(
      context.diagnostics,
      'PROCESS_RUNTIME_TRACE_PREFIX_MISSING',
      'warning',
      ['traceArtifact', 'missingTracePrefixCount'],
      `The retained trace omits ${artifact.missingTracePrefixCount} earlier activation(s); absence cannot imply completion.`,
      String(artifact.missingTracePrefixCount),
    )
  }

  artifact.traces.forEach((trace, traceIndex) => projectTrace(trace, traceIndex, context))
}

function projectTrace(
  trace: NormalizedExecutionTrace,
  traceIndex: number,
  context: RuntimeProjectionContext,
) {
  const attemptId = trace.continuation!.processAttemptId
  trace.events.forEach((event, eventIndex) => {
    const id = runtimeIdentity(
      'trace-event',
      context.processInstanceId,
      attemptId,
      trace.activation,
      String(event.sequence),
    )
    const elementIds = new Set<string>()
    const primary = findSingleProcessNode(event.node, context.index)
    if (primary) {
      elementIds.add(primary.id)
    } else {
      addUnmatchedEvidence(
        context,
        'trace-node',
        id,
        event.node,
        event,
        'PROCESS_RUNTIME_TRACE_NODE_UNMATCHED',
        ['traceArtifact', 'traces', String(traceIndex), 'events', String(eventIndex), 'node'],
        `Trace event references Process node '${event.node}', which is absent or ambiguous in the graph.`,
      )
    }

    const branchOrClause = event.branchOrClause
      ? findOwnedElement(event.node, event.branchOrClause, context.index)
      : null
    if (event.branchOrClause) {
      if (branchOrClause) {
        elementIds.add(branchOrClause.id)
      } else {
        addUnmatchedEvidence(
          context,
          'trace-branch-or-clause',
          id,
          event.branchOrClause,
          event,
          'PROCESS_RUNTIME_TRACE_BRANCH_UNMATCHED',
          ['traceArtifact', 'traces', String(traceIndex), 'events', String(eventIndex), 'branchOrClause'],
          `Trace event branch or clause '${event.branchOrClause}' is absent or ambiguous under Process node '${event.node}'.`,
        )
      }
    }

    const requestOutcome = event.requestOutcome
      ? findRequestOutcome(event.node, event.requestOutcome, context.index)
      : null
    if (event.requestOutcome) {
      if (requestOutcome) {
        elementIds.add(requestOutcome.id)
      } else if (!primary || hasOwnedRequestOutcomes(event.node, context.index)) {
        addUnmatchedEvidence(
          context,
          'trace-request-outcome',
          id,
          event.requestOutcome,
          event,
          'PROCESS_RUNTIME_TRACE_OUTCOME_UNMATCHED',
          ['traceArtifact', 'traces', String(traceIndex), 'events', String(eventIndex), 'requestOutcome'],
          `Trace Request outcome '${event.requestOutcome}' is absent or ambiguous under Process node '${event.node}'.`,
        )
      }
    }

    const relatedDefinition = matchDefinitionReference(
      event.relatedDefinition ?? null,
      id,
      event,
      ['traceArtifact', 'traces', String(traceIndex), 'events', String(eventIndex), 'relatedDefinition'],
      context,
    )
    if (relatedDefinition) {
      elementIds.add(relatedDefinition.id)
    }

    let relatedNode: ProcessPresentationNode | null = null
    if (event.relatedNode) {
      if (event.relatedDefinition
        && sameDefinitionReference(event.relatedDefinition, context.graph.document.definition)) {
        relatedNode = findSingleProcessNode(event.relatedNode, context.index)
        if (relatedNode) {
          elementIds.add(relatedNode.id)
        } else {
          addUnmatchedEvidence(
            context,
            'trace-related-node',
            id,
            event.relatedNode,
            event,
            'PROCESS_RUNTIME_TRACE_NODE_UNMATCHED',
            ['traceArtifact', 'traces', String(traceIndex), 'events', String(eventIndex), 'relatedNode'],
            `Trace event related node '${event.relatedNode}' claims the current Process definition but is absent or ambiguous.`,
          )
        }
      } else if (!event.relatedDefinition) {
        addUnmatchedEvidence(
          context,
          'trace-related-node',
          id,
          event.relatedNode,
          event,
          'PROCESS_RUNTIME_TRACE_NODE_UNMATCHED',
          ['traceArtifact', 'traces', String(traceIndex), 'events', String(eventIndex), 'relatedNode'],
          `Trace event related node '${event.relatedNode}' has no exact related definition for a safe graph join.`,
        )
      }
    }

    const occurrenceDefinition = matchDefinitionReference(
      event.processOccurrence?.definition ?? null,
      id,
      event,
      ['traceArtifact', 'traces', String(traceIndex), 'events', String(eventIndex), 'processOccurrence', 'definition'],
      context,
    )
    if (occurrenceDefinition) {
      elementIds.add(occurrenceDefinition.id)
    }

    if (event.processOccurrence && event.processOccurrence.disclosure !== 'Disclosed') {
      const disclosure = event.processOccurrence.disclosure
      addDiagnostic(
        context.diagnostics,
        'PROCESS_RUNTIME_OCCURRENCE_DISCLOSURE_GAP',
        disclosure === 'Redacted' ? 'info' : 'warning',
        ['traceArtifact', 'traces', String(traceIndex), 'events', String(eventIndex), 'processOccurrence', 'disclosure'],
        `Process occurrence evidence is explicitly ${disclosure.toLowerCase()}; no occurrence completion or lineage may be inferred.`,
        event.processOccurrence.kind,
        id,
      )
    }

    const traceEvent: ProcessRuntimeTraceEventOverlay = deepFreeze({
      activationId: trace.activation,
      attemptId,
      branchOrClauseElementId: branchOrClause?.id ?? null,
      elementIds: [...elementIds].sort(),
      event: cloneWireValue(event),
      id,
      occurrenceDefinitionElementId: occurrenceDefinition?.id ?? null,
      primaryElementId: primary?.id ?? null,
      relatedDefinitionElementId: relatedDefinition?.id ?? null,
      relatedNodeElementId: relatedNode?.id ?? null,
      requestOutcomeElementId: requestOutcome?.id ?? null,
      traceDisposition: trace.disposition,
    })

    for (const elementId of traceEvent.elementIds) {
      context.index.elements.get(elementId)!.traceEvents.push(traceEvent)
    }
  })
}

function matchDefinitionReference(
  reference: ExecutionDefinitionReference | null,
  evidenceId: string,
  source: NormalizedExecutionTraceEvent,
  path: readonly string[],
  context: RuntimeProjectionContext,
): ProcessPresentationNode | null {
  if (!reference || sameDefinitionReference(reference, context.graph.document.definition)) {
    return null
  }

  const matches = context.index.definitionReferences.filter((node) =>
    isDefinitionReference(node.details.source)
    && sameDefinitionReference(node.details.source, reference))
  if (matches.length === 1) {
    return matches[0]!
  }

  const subject = definitionReferenceSubject(reference)
  addUnmatchedEvidence(
    context,
    'definition-reference',
    evidenceId,
    subject,
    source,
    'PROCESS_RUNTIME_TRACE_DEFINITION_REFERENCE_UNMATCHED',
    path,
    `Trace lineage definition '${subject}' is absent or ambiguous in the Process presentation graph.`,
  )
  return null
}

function createGraphIndex(graph: ProcessPresentationGraph): GraphIndex {
  const elements = new Map<string, MutableProcessRuntimeElementOverlay>()
  const processNodesByCanonicalId = new Map<string, ProcessPresentationNode[]>()
  const definitionReferences: ProcessPresentationNode[] = []

  for (const node of graph.nodes) {
    elements.set(node.id, {
      elementId: node.id,
      traceEvents: [],
      tokens: [],
      waits: [],
    })
    if (node.category === 'process-node') {
      const matches = processNodesByCanonicalId.get(node.canonicalId) ?? []
      matches.push(node)
      processNodesByCanonicalId.set(node.canonicalId, matches)
    }
    if (node.category === 'definition-reference') {
      definitionReferences.push(node)
    }
  }

  return {
    definitionReferences,
    elements,
    nodes: graph.nodes,
    processNodesByCanonicalId,
  }
}

function findSingleProcessNode(
  canonicalId: string,
  index: GraphIndex,
): ProcessPresentationNode | null {
  const matches = index.processNodesByCanonicalId.get(canonicalId) ?? []
  return matches.length === 1 ? matches[0]! : null
}

function findOwnedElement(
  ownerCanonicalId: string,
  canonicalId: string,
  index: GraphIndex,
): ProcessPresentationNode | null {
  const matches = index.nodes.filter((node) =>
    node.ownerId === ownerCanonicalId
    && node.canonicalId === canonicalId)
  return matches.length === 1 ? matches[0]! : null
}

function findRequestOutcome(
  ownerCanonicalId: string,
  outcome: string,
  index: GraphIndex,
): ProcessPresentationNode | null {
  const matches = index.nodes.filter((node) =>
    node.category === 'request-outcome'
    && node.ownerId === ownerCanonicalId
    && node.role === outcome)
  return matches.length === 1 ? matches[0]! : null
}

function hasOwnedRequestOutcomes(
  ownerCanonicalId: string,
  index: GraphIndex,
): boolean {
  return index.nodes.some((node) =>
    node.category === 'request-outcome'
    && node.ownerId === ownerCanonicalId)
}

function addUnmatchedEvidence(
  context: RuntimeProjectionContext,
  kind: ProcessRuntimeUnmatchedEvidenceKind,
  evidenceId: string,
  reference: string,
  source: ProcessRuntimeUnmatchedEvidenceSource,
  diagnosticCode: ProcessRuntimePresentationDiagnosticCode,
  path: readonly string[],
  message: string,
) {
  const id = runtimeIdentity('unmatched', evidenceId, kind, reference)
  if (!context.unmatchedEvidence.has(id)) {
    context.unmatchedEvidence.set(id, deepFreeze({
      evidenceId,
      id,
      kind,
      reference,
      source: cloneWireValue(source),
    }))
  }
  addDiagnostic(
    context.diagnostics,
    diagnosticCode,
    'warning',
    path,
    message,
    reference,
    evidenceId,
  )
}

function addDiagnostic(
  diagnostics: Map<string, ProcessRuntimePresentationDiagnostic>,
  code: ProcessRuntimePresentationDiagnosticCode,
  severity: ProcessRuntimePresentationDiagnosticSeverity,
  path: readonly string[],
  message: string,
  subject: string | null,
  evidenceId: string | null = null,
) {
  const id = runtimeIdentity('diagnostic', code, evidenceId ?? '', subject ?? '', ...path)
  if (diagnostics.has(id)) {
    return
  }
  diagnostics.set(id, deepFreeze({
    code,
    evidenceId,
    id,
    message,
    path: [...path],
    severity,
    subject,
  }))
}

function freezeProjectionResult(
  overlay: ProcessRuntimePresentationOverlay | null,
  diagnostics: Map<string, ProcessRuntimePresentationDiagnostic>,
): ProcessRuntimePresentationProjectionResult {
  return deepFreeze({
    diagnostics: freezeDiagnostics(diagnostics),
    overlay,
  })
}

function freezeDiagnostics(
  diagnostics: Map<string, ProcessRuntimePresentationDiagnostic>,
): readonly ProcessRuntimePresentationDiagnostic[] {
  return deepFreeze([...diagnostics.values()].sort(compareEvidenceById))
}

function sameDefinitionReference(
  left: DeepReadonly<ExecutionDefinitionReference>,
  right: DeepReadonly<ExecutionDefinitionReference>,
): boolean {
  return left.definitionId === right.definitionId
    && left.revisionId === right.revisionId
    && left.fingerprint.algorithm === right.fingerprint.algorithm
    && left.fingerprint.canonicalization === right.fingerprint.canonicalization
    && left.fingerprint.value === right.fingerprint.value
}

function definitionReferenceSubject(reference: DeepReadonly<ExecutionDefinitionReference>): string {
  return `${reference.definitionId}@${reference.revisionId}`
    + `#${reference.fingerprint.algorithm}:${reference.fingerprint.canonicalization}:${reference.fingerprint.value}`
}

function isDefinitionReference(value: unknown): value is ExecutionDefinitionReference {
  if (!isRecord(value) || !isRecord(value.fingerprint)) {
    return false
  }
  return typeof value.definitionId === 'string'
    && typeof value.revisionId === 'string'
    && typeof value.fingerprint.algorithm === 'string'
    && typeof value.fingerprint.canonicalization === 'string'
    && typeof value.fingerprint.value === 'string'
}

function runtimeIdentity(kind: string, ...parts: readonly string[]): string {
  return `process-runtime/${kind}/${parts.map((part) => encodeURIComponent(part)).join('/')}`
}

function compareEvidenceById<T extends { readonly id: string }>(left: T, right: T): number {
  return compareOrdinal(left.id, right.id)
}
