import {
  canonicalProcessAwaitClauseKinds,
  canonicalProcessNodeKinds,
  type ProcessAwaitClause,
  type ProcessNode,
} from './generated/processes.shapes.generated'

type Equal<TLeft, TRight> =
  (<T>() => T extends TLeft ? 1 : 2) extends
  (<T>() => T extends TRight ? 1 : 2)
    ? true
    : false
type Assert<T extends true> = T

type ProcessNodeKind = ProcessNode['$node']
type CatalogProcessNodeKind = (typeof canonicalProcessNodeKinds)[number]
type ProcessAwaitClauseKind = ProcessAwaitClause['$clause']
type CatalogProcessAwaitClauseKind = (typeof canonicalProcessAwaitClauseKinds)[number]

export type ProcessNodeInventoryIsExact = Assert<Equal<ProcessNodeKind, CatalogProcessNodeKind>>
export type ProcessAwaitClauseInventoryIsExact = Assert<
  Equal<ProcessAwaitClauseKind, CatalogProcessAwaitClauseKind>
>

export function referencedTransition(node: ProcessNode): string | undefined {
  return node.$node === 'invokeTransition'
    ? node.transition.definitionId
    : undefined
}
