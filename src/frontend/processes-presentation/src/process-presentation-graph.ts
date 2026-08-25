import {
  canonicalProcessAwaitClauseKinds,
  canonicalProcessNodeKinds,
  type ExecutionDefinitionDocument,
  type ExecutionDefinitionFingerprint,
  type ExecutionDefinitionMetadata,
  type ExecutionDefinitionReference,
  type ExecutionSourceProvenance,
  type ProcessAwaitClause,
  type ProcessChoiceCase,
  type ProcessDefinition,
  type ProcessEdge,
  type ProcessFallback,
  type ProcessForkBranch,
  type ProcessJoinBranchResult,
  type ProcessMatchCase,
  type ProcessNode,
  type ProcessRequestOutcomeBranch,
  type ValueContract,
} from '@cohesivesystems/processes'
import {
  cloneWireValue,
  deepFreeze,
  isRecord,
  type DeepReadonly,
} from './process-presentation-values'

export type { DeepReadonly } from './process-presentation-values'

export const canonicalProcessPresentationCompatibility = Object.freeze({
  definitionKind: 'process',
  projectionVersion: 'cohesive-process-presentation/v1',
  schemaVersions: Object.freeze(['cohesive-execution/v3'] as const),
})

export type CanonicalProcessNodeKind = (typeof canonicalProcessNodeKinds)[number]
export type CanonicalProcessAwaitClauseKind = (typeof canonicalProcessAwaitClauseKinds)[number]

export type ProcessPresentationElementCategory =
  | 'await-clause'
  | 'choice-case'
  | 'definition-reference'
  | 'fallback'
  | 'fork-branch'
  | 'join-result'
  | 'match-case'
  | 'process-node'
  | 'request-outcome'
  | 'unresolved-node'

export type ProcessPresentationEdgeKind =
  | 'contains'
  | 'control'
  | 'definition-reference'
  | 'join-result'
  | 'reciprocal'

export type ProcessPresentationDiagnosticSeverity = 'error' | 'warning'

export type ProcessPresentationDiagnosticCode =
  | 'PROCESS_CONSTRUCT_DISPOSITION_MISSING'
  | 'PROCESS_CONSTRUCT_DISPOSITION_STALE'
  | 'PROCESS_DEFINITION_INVALID'
  | 'PROCESS_DEFINITION_KIND_UNSUPPORTED'
  | 'PROCESS_DEFINITION_REFERENCE_INVALID'
  | 'PROCESS_DEFINITION_SCHEMA_UNSUPPORTED'
  | 'PROCESS_EDGE_INVALID'
  | 'PROCESS_EDGE_TARGET_UNRESOLVED'
  | 'PROCESS_ELEMENT_ID_DUPLICATED'
  | 'PROCESS_ELEMENT_ID_REQUIRED'
  | 'PROCESS_ENTRY_UNRESOLVED'
  | 'PROCESS_NODE_KIND_REQUIRED'
  | 'PROCESS_NODE_KIND_UNSUPPORTED'
  | 'PROCESS_NODE_TABLE_INVALID'

export interface ProcessPresentationDiagnostic {
  readonly code: ProcessPresentationDiagnosticCode
  readonly id: string
  readonly message: string
  readonly path: readonly string[]
  readonly severity: ProcessPresentationDiagnosticSeverity
  readonly subject: string | null
}

export interface ProcessPresentationDefinitionEvidence {
  readonly reference: DeepReadonly<ExecutionDefinitionReference>
  readonly role: string
}

export interface ProcessPresentationValueContractEvidence {
  readonly contract: DeepReadonly<ValueContract>
  readonly role: string
}

export type ProcessPresentationSource =
  | ProcessAwaitClause
  | ProcessChoiceCase
  | ProcessDefinition
  | ProcessEdge
  | ProcessFallback
  | ProcessForkBranch
  | ProcessJoinBranchResult
  | ProcessMatchCase
  | ProcessNode
  | ProcessRequestOutcomeBranch
  | ExecutionDefinitionReference
  | unknown

export interface ProcessPresentationElementDetails {
  readonly definitionReferences: readonly ProcessPresentationDefinitionEvidence[]
  readonly source: DeepReadonly<ProcessPresentationSource>
  readonly sourceMap: readonly DeepReadonly<ExecutionSourceProvenance>[]
  readonly sourcePath: readonly string[]
  readonly valueContracts: readonly ProcessPresentationValueContractEvidence[]
}

export interface ProcessPresentationNode {
  readonly canonicalId: string
  readonly category: ProcessPresentationElementCategory
  readonly id: string
  readonly label: string
  readonly ownerId: string | null
  readonly processNodeKind: string | null
  readonly role: string | null
  readonly terminal: boolean
  readonly details: ProcessPresentationElementDetails
}

export interface ProcessPresentationEdge {
  readonly canonicalEdgeId: string | null
  readonly id: string
  readonly kind: ProcessPresentationEdgeKind
  readonly role: string
  readonly source: string
  readonly target: string
  readonly details: ProcessPresentationElementDetails
}

export interface ProcessPresentationDocumentEvidence {
  readonly definition: DeepReadonly<ExecutionDefinitionReference>
  readonly kind: string
  readonly metadata: DeepReadonly<ExecutionDefinitionMetadata>
  readonly schemaVersion: string
}

export interface ProcessPresentationGraph {
  readonly diagnostics: readonly ProcessPresentationDiagnostic[]
  readonly document: ProcessPresentationDocumentEvidence
  readonly edges: readonly ProcessPresentationEdge[]
  readonly entryNodeId: string | null
  readonly nodes: readonly ProcessPresentationNode[]
  readonly projectionVersion: string
}

export interface ProcessPresentationProjectionResult {
  readonly diagnostics: readonly ProcessPresentationDiagnostic[]
  readonly graph: ProcessPresentationGraph | null
}

interface MutableElementDetails {
  definitionReferences: ProcessPresentationDefinitionEvidence[]
  source: unknown
  sourceMap: DeepReadonly<ExecutionSourceProvenance>[]
  sourcePath: string[]
  valueContracts: ProcessPresentationValueContractEvidence[]
}

interface MutableNode {
  canonicalId: string
  category: ProcessPresentationElementCategory
  id: string
  label: string
  ownerId: string | null
  processNodeKind: string | null
  role: string | null
  terminal: boolean
  details: MutableElementDetails
}

interface MutableEdge {
  canonicalEdgeId: string | null
  id: string
  kind: ProcessPresentationEdgeKind
  role: string
  source: string
  target: string
  details: MutableElementDetails
}

interface ProjectionContext {
  readonly constructPaths: Map<string, readonly string[]>
  readonly diagnostics: ProcessPresentationDiagnostic[]
  readonly document: ExecutionDefinitionDocument
  readonly edges: MutableEdge[]
  readonly edgePaths: Map<string, readonly string[]>
  readonly nodeIds: Set<string>
  readonly nodes: MutableNode[]
  readonly pendingTargets: Array<{
    readonly edge: MutableEdge
    readonly path: readonly string[]
    readonly targetCanonicalId: string
  }>
}

interface ProcessNodeDisposition<TKind extends CanonicalProcessNodeKind> {
  readonly label: string
  readonly terminal?: boolean
  readonly project: (
    node: Extract<ProcessNode, { readonly $node: TKind }>,
    owner: MutableNode,
    path: readonly string[],
    context: ProjectionContext,
  ) => void
}

type ProcessNodeDispositionLedger = {
  readonly [TKind in CanonicalProcessNodeKind]: ProcessNodeDisposition<TKind>
}

interface ProcessAwaitClauseDisposition<TKind extends CanonicalProcessAwaitClauseKind> {
  readonly label: string
  readonly project: (
    clause: Extract<ProcessAwaitClause, { readonly $clause: TKind }>,
    owner: MutableNode,
    path: readonly string[],
    context: ProjectionContext,
  ) => void
}

type ProcessAwaitClauseDispositionLedger = {
  readonly [TKind in CanonicalProcessAwaitClauseKind]: ProcessAwaitClauseDisposition<TKind>
}

export function processPresentationElementId(
  category: ProcessPresentationElementCategory,
  canonicalId: string,
  ownerId?: string | null,
): string {
  const owner = ownerId ? `${encodeURIComponent(ownerId)}/` : ''
  return `${category}/${owner}${encodeURIComponent(canonicalId)}`
}

export function processPresentationDefinitionReferenceId(
  reference: ExecutionDefinitionReference,
): string {
  return processPresentationElementId(
    'definition-reference',
    definitionReferenceKey(reference),
  )
}

export function projectCanonicalProcessDocument(
  document: ExecutionDefinitionDocument,
): ProcessPresentationProjectionResult {
  const diagnostics: ProcessPresentationDiagnostic[] = []
  if (!isRecord(document)) {
    addDiagnostic(
      diagnostics,
      'PROCESS_DEFINITION_INVALID',
      [],
      'A canonical Process projection requires an execution-definition document object.',
    )
    return freezeProjectionResult(null, diagnostics)
  }

  const kind = readString(document.kind)
  if (kind !== canonicalProcessPresentationCompatibility.definitionKind) {
    addDiagnostic(
      diagnostics,
      'PROCESS_DEFINITION_KIND_UNSUPPORTED',
      ['kind'],
      `Process presentation supports definition kind '${canonicalProcessPresentationCompatibility.definitionKind}', ` +
        `but observed '${kind ?? '<missing>'}'.`,
      kind,
    )
    return freezeProjectionResult(null, diagnostics)
  }

  const metadata = isRecord(document.metadata) ? document.metadata : null
  const schemaVersion = readString(metadata?.schemaVersion)
  if (!schemaVersion || !canonicalProcessPresentationCompatibility.schemaVersions.includes(
    schemaVersion as (typeof canonicalProcessPresentationCompatibility.schemaVersions)[number],
  )) {
    addDiagnostic(
      diagnostics,
      'PROCESS_DEFINITION_SCHEMA_UNSUPPORTED',
      ['metadata', 'schemaVersion'],
      `Process presentation does not support execution schema '${schemaVersion ?? '<missing>'}'.`,
      schemaVersion,
    )
    return freezeProjectionResult(null, diagnostics)
  }

  if (!metadata) {
    addDiagnostic(
      diagnostics,
      'PROCESS_DEFINITION_INVALID',
      ['metadata'],
      'A canonical Process document requires definition metadata.',
    )
    return freezeProjectionResult(null, diagnostics)
  }

  const definition = isRecord(document.definition) ? document.definition : null
  if (!definition) {
    addDiagnostic(
      diagnostics,
      'PROCESS_DEFINITION_INVALID',
      ['definition'],
      'A canonical Process document requires an object definition payload.',
    )
    return freezeProjectionResult(null, diagnostics)
  }

  const context: ProjectionContext = {
    constructPaths: new Map(),
    diagnostics,
    document,
    edgePaths: new Map(),
    edges: [],
    nodeIds: new Set(),
    nodes: [],
    pendingTargets: [],
  }
  verifyDispositionCompleteness(context)
  projectProcessDefinition(definition, context)
  resolvePendingTargets(context)

  const definitionReference = readDefinitionReference(metadata)
  if (!definitionReference) {
    addDiagnostic(
      diagnostics,
      'PROCESS_DEFINITION_REFERENCE_INVALID',
      ['metadata'],
      'Process metadata must retain definition id, revision id, and a complete fingerprint.',
    )
  }

  const entry = readString(definition.entry)
  const entryNodeId = entry ? processPresentationElementId('process-node', entry) : null
  if (!entry || !context.nodeIds.has(entryNodeId!)) {
    addDiagnostic(
      diagnostics,
      'PROCESS_ENTRY_UNRESOLVED',
      ['definition', 'entry'],
      'The Process entry must identify a projected canonical Process node.',
      entry,
    )
  }

  sortProjection(context)
  const frozenDiagnostics = freezeDiagnostics(diagnostics)
  const graph: ProcessPresentationGraph = deepFreeze({
    diagnostics: frozenDiagnostics,
    document: {
      definition: definitionReference ?? missingDefinitionReference(metadata),
      kind,
      metadata: cloneWireValue(metadata),
      schemaVersion,
    },
    edges: context.edges.map(freezeEdge),
    entryNodeId: entry && context.nodeIds.has(entryNodeId!) ? entryNodeId : null,
    nodes: context.nodes.map(freezeNode),
    projectionVersion: canonicalProcessPresentationCompatibility.projectionVersion,
  })
  return deepFreeze({ graph, diagnostics: frozenDiagnostics })
}

function projectProcessDefinition(
  definition: Readonly<Record<string, unknown>>,
  context: ProjectionContext,
) {
  const nodes = definition.nodes
  if (!Array.isArray(nodes)) {
    addDiagnostic(
      context.diagnostics,
      'PROCESS_NODE_TABLE_INVALID',
      ['definition', 'nodes'],
      'A canonical Process definition requires a node array.',
    )
    return
  }

  for (let index = 0; index < nodes.length; index += 1) {
    const path = ['definition', 'nodes', String(index)]
    const rawNode = nodes[index]
    if (!isRecord(rawNode)) {
      addDiagnostic(
        context.diagnostics,
        'PROCESS_DEFINITION_INVALID',
        path,
        'A Process node must be an object.',
      )
      continue
    }

    const id = readString(rawNode.id)
    if (!id) {
      addDiagnostic(
        context.diagnostics,
        'PROCESS_ELEMENT_ID_REQUIRED',
        [...path, 'id'],
        'A Process node requires a stable canonical identity.',
      )
      continue
    }

    const kind = readString(rawNode.$node)
    if (!kind) {
      const owner = addElementNode({
        canonicalId: id,
        category: 'process-node',
        context,
        label: 'Unknown Process node',
        path,
        processNodeKind: null,
        source: rawNode,
      })
      addDiagnostic(
        context.diagnostics,
        'PROCESS_NODE_KIND_REQUIRED',
        [...path, '$node'],
        'A Process node requires its persisted wire discriminator.',
        owner?.canonicalId ?? id,
      )
      continue
    }

    if (!isCanonicalProcessNodeKind(kind)) {
      addElementNode({
        canonicalId: id,
        category: 'process-node',
        context,
        label: `Unsupported Process node (${kind})`,
        path,
        processNodeKind: kind,
        source: rawNode,
      })
      addDiagnostic(
        context.diagnostics,
        'PROCESS_NODE_KIND_UNSUPPORTED',
        [...path, '$node'],
        `Process node kind '${kind}' is outside the projector disposition inventory.`,
        id,
      )
      continue
    }

    const disposition = processNodeDispositions[kind] as ProcessNodeDisposition<typeof kind>
    const owner = addElementNode({
      canonicalId: id,
      category: 'process-node',
      context,
      label: disposition.label,
      path,
      processNodeKind: kind,
      source: rawNode,
      terminal: disposition.terminal,
    })
    if (!owner) {
      continue
    }

    disposition.project(
      rawNode as unknown as Extract<ProcessNode, { readonly $node: typeof kind }>,
      owner,
      path,
      context,
    )
  }
}

const processNodeDispositions: ProcessNodeDispositionLedger = {
  invokeTransition: {
    label: 'Invoke transition',
    project(node, owner, path, context) {
      addDefinitionReference(owner, 'transition', node.transition, [...path, 'transition'], context)
      addContinuation(owner, node.continuation, 'continuation', [...path, 'continuation'], context)
    },
  },
  evaluateRelation: {
    label: 'Evaluate relation',
    project(node, owner, path, context) {
      addDefinitionReference(owner, 'relation', node.relation, [...path, 'relation'], context)
      addContinuation(owner, node.continuation, 'continuation', [...path, 'continuation'], context)
    },
  },
  request: {
    label: 'Request',
    project(node, owner, path, context) {
      addContractReference(owner, 'request-contract', node.contract, [...path, 'contract'], context)
      projectRequestOutcomes(owner, node.outcomes, [...path, 'outcomes'], context)
    },
  },
  emitEvent: {
    label: 'Emit event',
    project(node, owner, path, context) {
      addContractReference(owner, 'domain-event-contract', node.contract, [...path, 'contract'], context)
      addControlEdge(owner, node.next, 'next', [...path, 'next'], context)
    },
  },
  sendSignal: {
    label: 'Send signal',
    project(node, owner, path, context) {
      addContractReference(owner, 'signal-contract', node.contract, [...path, 'contract'], context)
      addControlEdge(owner, node.next, 'next', [...path, 'next'], context)
    },
  },
  choice: {
    label: 'Choice',
    project(node, owner, path, context) {
      projectChoiceCases(owner, node.cases, [...path, 'cases'], context)
      projectFallback(owner, node.fallback, [...path, 'fallback'], context)
    },
  },
  match: {
    label: 'Match',
    project(node, owner, path, context) {
      addValueContract(owner, 'match-value', node.contract)
      projectMatchCases(owner, node.cases, [...path, 'cases'], context)
      projectFallback(owner, node.fallback, [...path, 'fallback'], context)
    },
  },
  fork: {
    label: 'Fork',
    project(node, owner, path, context) {
      projectForkBranches(owner, node.branches, [...path, 'branches'], context)
      const join = readString(node.join)
      if (join) {
        addDerivedEdge(owner, processPresentationElementId('process-node', join), 'reciprocal', 'join', path, context)
      } else {
        addDiagnostic(
          context.diagnostics,
          'PROCESS_EDGE_TARGET_UNRESOLVED',
          [...path, 'join'],
          'A Fork requires the stable identity of its reciprocal Join.',
          owner.canonicalId,
        )
      }
    },
  },
  join: {
    label: 'Join',
    project(node, owner, path, context) {
      addControlEdge(owner, node.next, 'next', [...path, 'next'], context)
      projectJoinResults(owner, node.fork, node.result, [...path, 'result'], context)
    },
  },
  awaitMatch: {
    label: 'Await match',
    project(node, owner, path, context) {
      projectAwaitClauses(owner, node.clauses, [...path, 'clauses'], context)
    },
  },
  timer: {
    label: 'Timer',
    project(node, owner, path, context) {
      addControlEdge(owner, node.next, 'next', [...path, 'next'], context)
    },
  },
  reply: {
    label: 'Reply',
    project(node, owner, path, context) {
      addContractReference(owner, 'reply-contract', node.contract, [...path, 'contract'], context)
      addControlEdge(owner, node.next, 'next', [...path, 'next'], context)
    },
  },
  durableCut: {
    label: 'Durable cut',
    project(node, owner, path, context) {
      addControlEdge(owner, node.resume, 'resume', [...path, 'resume'], context)
    },
  },
  invokeProcess: {
    label: 'Invoke process',
    project(node, owner, path, context) {
      addDefinitionReference(owner, 'child-process', node.process, [...path, 'process'], context)
      addContractReference(owner, 'request-contract', node.contract, [...path, 'contract'], context)
      projectRequestOutcomes(owner, node.outcomes, [...path, 'outcomes'], context)
    },
  },
  forEachPartition: {
    label: 'For each partition',
    project(node, owner, path, context) {
      addDefinitionReference(owner, 'child-process', node.process, [...path, 'process'], context)
      addContractReference(owner, 'request-contract', node.contract, [...path, 'contract'], context)
      addValueContract(owner, 'partition-output', node.partition?.contract)
      addControlEdge(owner, node.completed, 'completed', [...path, 'completed'], context)
      addControlEdge(owner, node.failed, 'failed', [...path, 'failed'], context)
    },
  },
  repeatAcrossActivation: {
    label: 'Repeat across activation',
    project(node, owner, path, context) {
      addValueContract(owner, 'progress', node.progressContract)
      addValueContract(owner, 'state', node.stateContract)
      addControlEdge(owner, node.repeat, 'repeat', [...path, 'repeat'], context)
      addControlEdge(owner, node.completed, 'completed', [...path, 'completed'], context)
      addControlEdge(owner, node.exhausted, 'exhausted', [...path, 'exhausted'], context)
      addControlEdge(owner, node.stalled, 'stalled', [...path, 'stalled'], context)
    },
  },
  cancellationFinalizer: {
    label: 'Cancellation finalizer',
    project(node, owner, path, context) {
      addDefinitionReference(owner, 'cancellation-finalizer-process', node.process, [...path, 'process'], context)
      addContractReference(owner, 'request-contract', node.contract, [...path, 'contract'], context)
    },
  },
  return: {
    label: 'Return',
    terminal: true,
    project() {},
  },
  fail: {
    label: 'Fail',
    terminal: true,
    project() {},
  },
}

const processAwaitClauseDispositions: ProcessAwaitClauseDispositionLedger = {
  interaction: {
    label: 'Await interaction',
    project(clause, owner, path, context) {
      addContractReference(owner, 'interaction-contract', clause.contract, [...path, 'contract'], context)
      addValueContract(owner, 'input', clause.input?.contract)
    },
  },
  timer: {
    label: 'Await timer',
    project() {},
  },
}

export const canonicalProcessPresentationDispositionKinds = Object.freeze(
  Object.keys(processNodeDispositions).sort() as CanonicalProcessNodeKind[],
)

export const canonicalProcessAwaitClausePresentationDispositionKinds = Object.freeze(
  Object.keys(processAwaitClauseDispositions).sort() as CanonicalProcessAwaitClauseKind[],
)

function projectRequestOutcomes(
  owner: MutableNode,
  rawOutcomes: unknown,
  path: readonly string[],
  context: ProjectionContext,
) {
  forEachElement(rawOutcomes, path, context, (rawOutcome, outcomePath) => {
    const id = readString(rawOutcome.id)
    if (!id) {
      missingElementId('Request outcome', outcomePath, owner, context)
      return
    }

    const role = readString(rawOutcome.outcome) ?? 'outcome'
    const outcome = addElementNode({
      canonicalId: id,
      category: 'request-outcome',
      context,
      label: `Request outcome: ${role}`,
      ownerId: owner.canonicalId,
      path: outcomePath,
      role,
      source: rawOutcome,
    })
    if (!outcome) {
      return
    }

    addContainmentEdge(owner, outcome, 'outcome', outcomePath, context)
    addContinuation(outcome, rawOutcome.continuation, role, [...outcomePath, 'continuation'], context)
  })
}

function projectChoiceCases(
  owner: MutableNode,
  rawCases: unknown,
  path: readonly string[],
  context: ProjectionContext,
) {
  forEachElement(rawCases, path, context, (rawCase, casePath) => {
    const id = readString(rawCase.id)
    if (!id) {
      missingElementId('Choice case', casePath, owner, context)
      return
    }

    const choiceCase = addElementNode({
      canonicalId: id,
      category: 'choice-case',
      context,
      label: 'Choice case',
      ownerId: owner.canonicalId,
      path: casePath,
      role: 'case',
      source: rawCase,
    })
    if (!choiceCase) {
      return
    }

    addContainmentEdge(owner, choiceCase, 'case', casePath, context)
    addControlEdge(choiceCase, rawCase.next, 'selected', [...casePath, 'next'], context)
  })
}

function projectMatchCases(
  owner: MutableNode,
  rawCases: unknown,
  path: readonly string[],
  context: ProjectionContext,
) {
  forEachElement(rawCases, path, context, (rawCase, casePath) => {
    const id = readString(rawCase.id)
    if (!id) {
      missingElementId('Match case', casePath, owner, context)
      return
    }

    const matchCase = addElementNode({
      canonicalId: id,
      category: 'match-case',
      context,
      label: 'Match case',
      ownerId: owner.canonicalId,
      path: casePath,
      role: 'case',
      source: rawCase,
    })
    if (!matchCase) {
      return
    }

    const pattern = isRecord(rawCase.pattern) ? rawCase.pattern : null
    addValueContract(matchCase, 'pattern', pattern?.contract)
    addContainmentEdge(owner, matchCase, 'case', casePath, context)
    addControlEdge(matchCase, rawCase.next, 'selected', [...casePath, 'next'], context)
  })
}

function projectFallback(
  owner: MutableNode,
  rawFallback: unknown,
  path: readonly string[],
  context: ProjectionContext,
) {
  if (rawFallback === null || rawFallback === undefined) {
    return
  }
  if (!isRecord(rawFallback)) {
    addDiagnostic(
      context.diagnostics,
      'PROCESS_DEFINITION_INVALID',
      path,
      'A Process fallback must be an object.',
      owner.canonicalId,
    )
    return
  }

  const id = readString(rawFallback.id)
  if (!id) {
    missingElementId('Fallback', path, owner, context)
    return
  }

  const fallback = addElementNode({
    canonicalId: id,
    category: 'fallback',
    context,
    label: 'Fallback',
    ownerId: owner.canonicalId,
    path,
    role: 'fallback',
    source: rawFallback,
  })
  if (!fallback) {
    return
  }

  addContainmentEdge(owner, fallback, 'fallback', path, context)
  addControlEdge(fallback, rawFallback.next, 'fallback', [...path, 'next'], context)
}

function projectForkBranches(
  owner: MutableNode,
  rawBranches: unknown,
  path: readonly string[],
  context: ProjectionContext,
) {
  forEachElement(rawBranches, path, context, (rawBranch, branchPath) => {
    const id = readString(rawBranch.id)
    if (!id) {
      missingElementId('Fork branch', branchPath, owner, context)
      return
    }

    const branch = addElementNode({
      canonicalId: id,
      category: 'fork-branch',
      context,
      label: 'Fork branch',
      ownerId: owner.canonicalId,
      path: branchPath,
      role: readString(rawBranch.capacityDomain) ?? 'branch',
      source: rawBranch,
    })
    if (!branch) {
      return
    }

    addContainmentEdge(owner, branch, 'branch', branchPath, context)
    addControlEdge(branch, rawBranch.start, 'start', [...branchPath, 'start'], context)
  })
}

function projectAwaitClauses(
  owner: MutableNode,
  rawClauses: unknown,
  path: readonly string[],
  context: ProjectionContext,
) {
  forEachElement(rawClauses, path, context, (rawClause, clausePath) => {
    const id = readString(rawClause.id)
    if (!id) {
      missingElementId('Await clause', clausePath, owner, context)
      return
    }

    const clauseKind = readString(rawClause.$clause)
    const disposition = clauseKind && isCanonicalProcessAwaitClauseKind(clauseKind)
      ? processAwaitClauseDispositions[clauseKind] as ProcessAwaitClauseDisposition<CanonicalProcessAwaitClauseKind>
      : null
    const clause = addElementNode({
      canonicalId: id,
      category: 'await-clause',
      context,
      label: disposition?.label ?? `Unsupported Await clause (${clauseKind ?? 'missing'})`,
      ownerId: owner.canonicalId,
      path: clausePath,
      role: clauseKind ?? 'unknown',
      source: rawClause,
    })
    if (!clause) {
      return
    }

    addContainmentEdge(owner, clause, 'clause', clausePath, context)
    if (!disposition || !clauseKind) {
      addDiagnostic(
        context.diagnostics,
        'PROCESS_DEFINITION_INVALID',
        [...clausePath, '$clause'],
        `Await clause kind '${clauseKind ?? '<missing>'}' is unsupported.`,
        id,
      )
    } else {
      disposition.project(
        rawClause as unknown as Extract<ProcessAwaitClause, { readonly $clause: typeof clauseKind }>,
        clause,
        clausePath,
        context,
      )
    }
    addContinuation(clause, rawClause.continuation, 'selected', [...clausePath, 'continuation'], context)
  })
}

function projectJoinResults(
  owner: MutableNode,
  rawForkId: unknown,
  rawResult: unknown,
  path: readonly string[],
  context: ProjectionContext,
) {
  if (rawResult === null || rawResult === undefined) {
    return
  }
  if (!isRecord(rawResult)) {
    addDiagnostic(
      context.diagnostics,
      'PROCESS_DEFINITION_INVALID',
      path,
      'A Join result projection must be an object.',
      owner.canonicalId,
    )
    return
  }

  const forkId = readString(rawForkId)
  addValueContract(owner, 'join-result', rawResult.resultContract)
  forEachElement(rawResult.branches, [...path, 'branches'], context, (rawBranch, branchPath) => {
    const branchId = readString(rawBranch.branch)
    if (!branchId) {
      missingElementId('Join result branch', branchPath, owner, context)
      return
    }

    const result = addElementNode({
      canonicalId: branchId,
      category: 'join-result',
      context,
      label: 'Join branch result',
      ownerId: owner.canonicalId,
      path: branchPath,
      registerConstruct: false,
      role: 'result',
      source: rawBranch,
    })
    if (!result) {
      return
    }

    addValueContract(result, 'join-result', rawResult.resultContract)
    addContainmentEdge(owner, result, 'result', branchPath, context)
    addDerivedEdge(
      result,
      processPresentationElementId('fork-branch', branchId, forkId),
      'join-result',
      'branch',
      branchPath,
      context,
    )
  })
}

function addElementNode({
  canonicalId,
  category,
  context,
  label,
  ownerId = null,
  path,
  processNodeKind = null,
  registerConstruct = true,
  role = null,
  source,
  terminal = false,
}: {
  readonly canonicalId: string
  readonly category: ProcessPresentationElementCategory
  readonly context: ProjectionContext
  readonly label: string
  readonly ownerId?: string | null
  readonly path: readonly string[]
  readonly processNodeKind?: string | null
  readonly registerConstruct?: boolean
  readonly role?: string | null
  readonly source: unknown
  readonly terminal?: boolean
}): MutableNode | null {
  if (registerConstruct) {
    const priorPath = context.constructPaths.get(canonicalId)
    if (priorPath) {
      addDiagnostic(
        context.diagnostics,
        'PROCESS_ELEMENT_ID_DUPLICATED',
        path,
        `Canonical Process element identity '${canonicalId}' is duplicated; first observed at '${formatPath(priorPath)}'.`,
        canonicalId,
      )
      return null
    }
    context.constructPaths.set(canonicalId, [...path])
  }

  const id = processPresentationElementId(category, canonicalId, ownerId)
  if (context.nodeIds.has(id)) {
    return context.nodes.find((node) => node.id === id) ?? null
  }

  const node: MutableNode = {
    canonicalId,
    category,
    details: createDetails(source, path, context.document),
    id,
    label,
    ownerId,
    processNodeKind,
    role,
    terminal,
  }
  context.nodeIds.add(id)
  context.nodes.push(node)
  return node
}

function addContainmentEdge(
  owner: MutableNode,
  child: MutableNode,
  role: string,
  path: readonly string[],
  context: ProjectionContext,
) {
  addDerivedEdge(owner, child.id, 'contains', role, path, context)
}

function addDerivedEdge(
  owner: MutableNode,
  target: string,
  kind: Exclude<ProcessPresentationEdgeKind, 'control' | 'definition-reference'>,
  role: string,
  path: readonly string[],
  context: ProjectionContext,
) {
  const id = derivedEdgeId(kind, owner.id, target, role)
  if (context.edges.some((edge) => edge.id === id)) {
    return
  }
  context.edges.push({
    canonicalEdgeId: null,
    details: createDetails(null, path, context.document),
    id,
    kind,
    role,
    source: owner.id,
    target,
  })
  context.pendingTargets.push({ edge: context.edges.at(-1)!, path, targetCanonicalId: target })
}

function addControlEdge(
  owner: MutableNode,
  rawEdge: unknown,
  role: string,
  path: readonly string[],
  context: ProjectionContext,
) {
  if (!isRecord(rawEdge)) {
    addDiagnostic(
      context.diagnostics,
      'PROCESS_EDGE_INVALID',
      path,
      `Process control edge '${role}' must be an object.`,
      owner.canonicalId,
    )
    return
  }

  const edgeId = readString(rawEdge.id)
  const targetCanonicalId = readString(rawEdge.target)
  if (!edgeId || !targetCanonicalId) {
    addDiagnostic(
      context.diagnostics,
      'PROCESS_EDGE_INVALID',
      path,
      `Process control edge '${role}' requires stable edge and target identities.`,
      edgeId ?? owner.canonicalId,
    )
    return
  }

  const priorPath = context.edgePaths.get(edgeId)
  if (priorPath) {
    addDiagnostic(
      context.diagnostics,
      'PROCESS_ELEMENT_ID_DUPLICATED',
      path,
      `Canonical Process edge identity '${edgeId}' is duplicated; first observed at '${formatPath(priorPath)}'.`,
      edgeId,
    )
    return
  }
  context.edgePaths.set(edgeId, [...path])

  const edge: MutableEdge = {
    canonicalEdgeId: edgeId,
    details: createDetails(rawEdge, path, context.document),
    id: `control/${encodeURIComponent(edgeId)}`,
    kind: 'control',
    role,
    source: owner.id,
    target: processPresentationElementId('process-node', targetCanonicalId),
  }
  context.edges.push(edge)
  context.pendingTargets.push({ edge, path, targetCanonicalId })
}

function addContinuation(
  owner: MutableNode,
  rawContinuation: unknown,
  role: string,
  path: readonly string[],
  context: ProjectionContext,
) {
  if (!isRecord(rawContinuation)) {
    addDiagnostic(
      context.diagnostics,
      'PROCESS_EDGE_INVALID',
      path,
      `Process continuation '${role}' must be an object.`,
      owner.canonicalId,
    )
    return
  }

  const output = isRecord(rawContinuation.output) ? rawContinuation.output : null
  addValueContract(owner, `${role}-output`, output?.contract)
  addControlEdge(owner, rawContinuation.edge, role, [...path, 'edge'], context)
}

function addContractReference(
  owner: MutableNode,
  role: string,
  rawContract: unknown,
  path: readonly string[],
  context: ProjectionContext,
) {
  if (!isRecord(rawContract)) {
    addDiagnostic(
      context.diagnostics,
      'PROCESS_DEFINITION_REFERENCE_INVALID',
      path,
      `Interaction contract '${role}' must retain an exact definition reference.`,
      owner.canonicalId,
    )
    return
  }
  addDefinitionReference(owner, role, rawContract.definition, [...path, 'definition'], context)
}

function addDefinitionReference(
  owner: MutableNode,
  role: string,
  rawReference: unknown,
  path: readonly string[],
  context: ProjectionContext,
) {
  const reference = readDefinitionReference(rawReference)
  if (!reference) {
    addDiagnostic(
      context.diagnostics,
      'PROCESS_DEFINITION_REFERENCE_INVALID',
      path,
      `Definition link '${role}' must retain definition id, revision id, and a complete fingerprint.`,
      owner.canonicalId,
    )
    return
  }

  const frozenReference = cloneWireValue(reference)
  owner.details.definitionReferences.push(deepFreeze({ reference: frozenReference, role }))
  const key = definitionReferenceKey(reference)
  const referenceNodeId = processPresentationDefinitionReferenceId(reference)
  let referenceNode = context.nodes.find((node) => node.id === referenceNodeId)
  if (!referenceNode) {
    referenceNode = addElementNode({
      canonicalId: key,
      category: 'definition-reference',
      context,
      label: `Definition: ${reference.definitionId}`,
      path,
      registerConstruct: false,
      role: 'definition',
      source: reference,
    }) ?? undefined
  }
  if (!referenceNode) {
    return
  }

  const edgeId = derivedEdgeId('definition-reference', owner.id, referenceNode.id, role)
  if (context.edges.some((edge) => edge.id === edgeId)) {
    return
  }
  context.edges.push({
    canonicalEdgeId: null,
    details: createDetails(reference, path, context.document),
    id: edgeId,
    kind: 'definition-reference',
    role,
    source: owner.id,
    target: referenceNode.id,
  })
}

function addValueContract(owner: MutableNode, role: string, rawContract: unknown) {
  if (!isRecord(rawContract)) {
    return
  }
  owner.details.valueContracts.push(deepFreeze({
    contract: cloneWireValue(rawContract) as DeepReadonly<ValueContract>,
    role,
  }))
}

function resolvePendingTargets(context: ProjectionContext) {
  for (const pending of context.pendingTargets) {
    if (context.nodeIds.has(pending.edge.target)) {
      continue
    }

    const targetCanonicalId = pending.edge.kind === 'control'
      ? pending.targetCanonicalId
      : pending.edge.target
    const unresolvedId = processPresentationElementId('unresolved-node', targetCanonicalId)
    addElementNode({
      canonicalId: targetCanonicalId,
      category: 'unresolved-node',
      context,
      label: `Unresolved target: ${targetCanonicalId}`,
      path: pending.path,
      registerConstruct: false,
      role: 'unresolved-target',
      source: null,
    })
    pending.edge.target = unresolvedId

    addDiagnostic(
      context.diagnostics,
      'PROCESS_EDGE_TARGET_UNRESOLVED',
      pending.path,
      `Projected edge '${pending.edge.id}' targets an unresolved semantic element.`,
      targetCanonicalId,
    )
  }
}

function verifyDispositionCompleteness(context: ProjectionContext) {
  verifyInventoryDispositionCompleteness(
    canonicalProcessNodeKinds,
    Object.keys(processNodeDispositions),
    'Process construct',
    context,
  )
  verifyInventoryDispositionCompleteness(
    canonicalProcessAwaitClauseKinds,
    Object.keys(processAwaitClauseDispositions),
    'Process Await clause',
    context,
  )
}

function verifyInventoryDispositionCompleteness(
  authoritativeValues: readonly string[],
  dispositionValues: readonly string[],
  label: string,
  context: ProjectionContext,
) {
  const authoritative = new Set<string>(authoritativeValues)
  const dispositions = new Set<string>(dispositionValues)
  for (const kind of [...authoritative].sort()) {
    if (!dispositions.has(kind)) {
      addDiagnostic(
        context.diagnostics,
        'PROCESS_CONSTRUCT_DISPOSITION_MISSING',
        [],
        `Canonical ${label} '${kind}' has no presentation disposition.`,
        kind,
      )
    }
  }
  for (const kind of [...dispositions].sort()) {
    if (!authoritative.has(kind)) {
      addDiagnostic(
        context.diagnostics,
        'PROCESS_CONSTRUCT_DISPOSITION_STALE',
        [],
        `${label} presentation disposition '${kind}' is absent from the canonical inventory.`,
        kind,
      )
    }
  }
}

function forEachElement(
  rawElements: unknown,
  path: readonly string[],
  context: ProjectionContext,
  project: (element: Readonly<Record<string, unknown>>, path: readonly string[]) => void,
) {
  if (!Array.isArray(rawElements)) {
    addDiagnostic(
      context.diagnostics,
      'PROCESS_DEFINITION_INVALID',
      path,
      'A canonical Process element collection must be an array.',
    )
    return
  }

  for (let index = 0; index < rawElements.length; index += 1) {
    const elementPath = [...path, String(index)]
    const element = rawElements[index]
    if (!isRecord(element)) {
      addDiagnostic(
        context.diagnostics,
        'PROCESS_DEFINITION_INVALID',
        elementPath,
        'A canonical Process element must be an object.',
      )
      continue
    }
    project(element, elementPath)
  }
}

function missingElementId(
  label: string,
  path: readonly string[],
  owner: MutableNode,
  context: ProjectionContext,
) {
  addDiagnostic(
    context.diagnostics,
    'PROCESS_ELEMENT_ID_REQUIRED',
    [...path, 'id'],
    `${label} requires a stable canonical identity.`,
    owner.canonicalId,
  )
}

function createDetails(
  source: unknown,
  path: readonly string[],
  document: ExecutionDefinitionDocument,
): MutableElementDetails {
  return {
    definitionReferences: [],
    source: cloneWireValue(source),
    sourceMap: sourceMapForPath(document, path),
    sourcePath: [...path],
    valueContracts: [],
  }
}

function sourceMapForPath(
  document: ExecutionDefinitionDocument,
  path: readonly string[],
): DeepReadonly<ExecutionSourceProvenance>[] {
  const entries = document.metadata?.sourceMap?.entries
  if (!Array.isArray(entries)) {
    return []
  }

  const definitionPath = path[0] === 'definition' ? path.slice(1) : path
  let deepest = -1
  const matches: ExecutionSourceProvenance[] = []
  for (const entry of entries) {
    const mapped = entry?.semanticPath?.segments
    if (
      !Array.isArray(mapped) ||
      !isStringArray(mapped) ||
      mapped.length < deepest ||
      !isPrefix(mapped, definitionPath)
    ) {
      continue
    }
    if (mapped.length > deepest) {
      deepest = mapped.length
      matches.length = 0
    }
    matches.push(entry)
  }
  return matches
    .sort((left, right) => sourceMapKey(left).localeCompare(sourceMapKey(right)))
    .map((entry) => cloneWireValue(entry))
}

function sortProjection(context: ProjectionContext) {
  context.nodes.sort((left, right) => left.id.localeCompare(right.id))
  context.edges.sort((left, right) => left.id.localeCompare(right.id))
  context.diagnostics.sort((left, right) => left.id.localeCompare(right.id))
}

function freezeNode(node: MutableNode): ProcessPresentationNode {
  return deepFreeze({
    ...node,
    details: freezeDetails(node.details),
  })
}

function freezeEdge(edge: MutableEdge): ProcessPresentationEdge {
  return deepFreeze({
    ...edge,
    details: freezeDetails(edge.details),
  })
}

function freezeDetails(details: MutableElementDetails): ProcessPresentationElementDetails {
  details.definitionReferences.sort((left, right) =>
    `${left.role}:${definitionReferenceKey(left.reference)}`.localeCompare(
      `${right.role}:${definitionReferenceKey(right.reference)}`,
    ))
  details.valueContracts.sort((left, right) => left.role.localeCompare(right.role))
  return deepFreeze({
    definitionReferences: details.definitionReferences,
    source: details.source,
    sourceMap: details.sourceMap,
    sourcePath: details.sourcePath,
    valueContracts: details.valueContracts,
  })
}

function readDefinitionReference(raw: unknown): ExecutionDefinitionReference | null {
  if (!isRecord(raw)) {
    return null
  }
  const definitionId = readString(raw.definitionId)
  const revisionId = readString(raw.revisionId)
  const fingerprint = readFingerprint(raw.fingerprint)
  return definitionId && revisionId && fingerprint
    ? { definitionId, fingerprint, revisionId }
    : null
}

function readFingerprint(raw: unknown): ExecutionDefinitionFingerprint | null {
  if (!isRecord(raw)) {
    return null
  }
  const algorithm = readString(raw.algorithm)
  const canonicalization = readString(raw.canonicalization)
  const value = readString(raw.value)
  return algorithm && canonicalization && value
    ? { algorithm, canonicalization, value }
    : null
}

function missingDefinitionReference(
  metadata: Readonly<Record<string, unknown>>,
): DeepReadonly<ExecutionDefinitionReference> {
  return deepFreeze({
    definitionId: readString(metadata.definitionId) ?? '<missing>',
    fingerprint: {
      algorithm: '<missing>',
      canonicalization: '<missing>',
      value: '<missing>',
    },
    revisionId: readString(metadata.revisionId) ?? '<missing>',
  })
}

function definitionReferenceKey(reference: DeepReadonly<ExecutionDefinitionReference>): string {
  return [
    reference.definitionId,
    reference.revisionId,
    reference.fingerprint.algorithm,
    reference.fingerprint.canonicalization,
    reference.fingerprint.value,
  ].join('|')
}

function addDiagnostic(
  diagnostics: ProcessPresentationDiagnostic[],
  code: ProcessPresentationDiagnosticCode,
  path: readonly string[],
  message: string,
  subject: string | null = null,
  severity: ProcessPresentationDiagnosticSeverity = 'error',
) {
  diagnostics.push(deepFreeze({
    code,
    id: `diagnostic/${encodeURIComponent(code)}/${encodeURIComponent(formatPath(path))}/${encodeURIComponent(subject ?? '')}`,
    message,
    path: [...path],
    severity,
    subject,
  }))
}

function freezeProjectionResult(
  graph: ProcessPresentationGraph | null,
  diagnostics: ProcessPresentationDiagnostic[],
): ProcessPresentationProjectionResult {
  diagnostics.sort((left, right) => left.id.localeCompare(right.id))
  return deepFreeze({ diagnostics: freezeDiagnostics(diagnostics), graph })
}

function freezeDiagnostics(
  diagnostics: ProcessPresentationDiagnostic[],
): readonly ProcessPresentationDiagnostic[] {
  return deepFreeze([...diagnostics])
}

function derivedEdgeId(
  kind: ProcessPresentationEdgeKind,
  source: string,
  target: string,
  role: string,
) {
  return `derived/${encodeURIComponent(kind)}/${encodeURIComponent(source)}/${encodeURIComponent(role)}/${encodeURIComponent(target)}`
}

function isCanonicalProcessNodeKind(value: string): value is CanonicalProcessNodeKind {
  return (canonicalProcessNodeKinds as readonly string[]).includes(value)
}

function isCanonicalProcessAwaitClauseKind(value: string): value is CanonicalProcessAwaitClauseKind {
  return (canonicalProcessAwaitClauseKinds as readonly string[]).includes(value)
}

function readString(value: unknown): string | null {
  return typeof value === 'string' && value.trim().length > 0 ? value : null
}

function isStringArray(value: readonly unknown[]): value is readonly string[] {
  return value.every((item) => typeof item === 'string')
}

function isPrefix(prefix: readonly string[], value: readonly string[]) {
  return prefix.length <= value.length && prefix.every((segment, index) => segment === value[index])
}

function sourceMapKey(source: ExecutionSourceProvenance) {
  return `${source.semanticPath?.segments.join('/') ?? ''}|${source.reference}|${source.description ?? ''}`
}

function formatPath(path: readonly string[]) {
  return path.length === 0
    ? '/'
    : `/${path.map((segment) => segment.replaceAll('~', '~0').replaceAll('/', '~1')).join('/')}`
}
