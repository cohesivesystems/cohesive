import type {
  ActionDefinition,
  PresentationModuleDefinition,
} from './module'
import {
  actionKindLabels,
  actionSemanticsKindLabels,
  actionScopeKindLabels,
  documentWorkspaceActionKindLabels,
  localDocumentEditorActionKindLabels,
  preparationKindLabels,
  presentationBindingKindLabels,
  type ActionKind,
  type ActionScopeKind,
  type ActionSemanticsKind,
  type DocumentWorkspaceActionKind,
  type LocalDocumentEditorActionKind,
  type PreparationKind,
  type PresentationBindingKind,
} from '@cohesivesystems/presentation-contracts'

export interface PresentationActionRuntime<TExecuteContext = unknown, TLabel = string> {
  readonly canExecute?: (context: TExecuteContext) => boolean
  readonly disabledReason?: TLabel
  readonly execute?: (context: TExecuteContext) => Promise<void> | void
  readonly isDisabled?: boolean
  readonly isHidden?: boolean
  readonly isPending?: boolean
  readonly label?: TLabel
  readonly pendingLabel?: TLabel
}

export interface PresentationActionRuntimeBindingContext {
  readonly action: ActionDefinition
  readonly actionId: string
  readonly module: PresentationModuleDefinition
}

export interface PresentationActionRuntimeBinding<TExecuteContext = unknown, TLabel = string> {
  readonly id: string
  readonly matches: (context: PresentationActionRuntimeBindingContext) => boolean
  readonly project: (context: PresentationActionRuntimeBindingContext) => PresentationActionRuntime<TExecuteContext, TLabel> | null | undefined
}

export interface PresentationActionRuntimeBindingSpec<TExecuteContext = unknown, TLabel = string> {
  /** Optional exact action id match for narrowly scoped or legacy bindings. */
  readonly actionId?: string | readonly string[]

  /** Optional infrastructure binding match, such as endpoint, flow event, or local state. */
  readonly bindingKind?: PresentationBindingKind | readonly PresentationBindingKind[]

  /** Optional sub-kind match for DocumentWorkspace action semantics. */
  readonly documentWorkspaceKind?:
    | DocumentWorkspaceActionKind
    | readonly DocumentWorkspaceActionKind[]

  readonly id: string

  /** Optional semantic action kind match. Prefer semantics-specific fields when possible. */
  readonly kind?: ActionKind | readonly ActionKind[]

  /** Optional sub-kind match for LocalDocumentEditor action semantics. */
  readonly localDocumentEditorKind?:
    | LocalDocumentEditorActionKind
    | readonly LocalDocumentEditorActionKind[]

  readonly predicate?: (context: PresentationActionRuntimeBindingContext) => boolean

  readonly preparationKind?: PreparationKind | readonly PreparationKind[]

  /** Optional first-class action semantics match. */
  readonly semanticsKind?: ActionSemanticsKind | readonly ActionSemanticsKind[]

  readonly project: (context: PresentationActionRuntimeBindingContext) => PresentationActionRuntime<TExecuteContext, TLabel> | null | undefined

  readonly scope?: ActionScopeKind | readonly ActionScopeKind[]
}

/**
 * Creates an adapter-neutral runtime binding for a semantic presentation
 * action. The binding decides whether an ActionDefinition matches, then
 * projects frontend-local runtime state for that action.
 */
export function createPresentationActionRuntimeBinding<
  TExecuteContext = unknown,
  TLabel = string,
>({
  actionId,
  bindingKind,
  documentWorkspaceKind,
  id,
  kind,
  localDocumentEditorKind,
  predicate,
  preparationKind,
  project,
  semanticsKind,
  scope,
}: PresentationActionRuntimeBindingSpec<
  TExecuteContext,
  TLabel
>): PresentationActionRuntimeBinding<TExecuteContext, TLabel> {
  return {
    id,
    matches: (context) =>
      matchesStringOption(context.action.Id, actionId) &&
      matchesEnumOption(context.action.Kind, kind, actionKindLabels) &&
      matchesEnumOption(context.action.Scope, scope, actionScopeKindLabels) &&
      matchesEnumOption(context.action.Binding.Kind, bindingKind, presentationBindingKindLabels) &&
      matchesEnumOption(context.action.Preparation?.Kind, preparationKind, preparationKindLabels) &&
      matchesEnumOption(context.action.Semantics?.Kind, semanticsKind, actionSemanticsKindLabels) &&
      matchesEnumOption(
        context.action.Semantics?.DocumentWorkspace?.Kind,
        documentWorkspaceKind,
        documentWorkspaceActionKindLabels,
      ) &&
      matchesEnumOption(
        context.action.Semantics?.LocalDocumentEditor?.Kind,
        localDocumentEditorKind,
        localDocumentEditorActionKindLabels,
      ) &&
      (predicate?.(context) ?? true),
    project,
  }
}

function matchesStringOption(
  value: string | null | undefined,
  expected: string | readonly string[] | null | undefined,
) {
  if (!expected) {
    return true
  }

  return toArray(expected).includes(value ?? '')
}

function matchesEnumOption<TValue extends string | number>(
  value: string | number | null | undefined,
  expected: TValue | readonly TValue[] | null | undefined,
  labels: Readonly<Record<number, string>>,
) {
  if (expected === null || expected === undefined) {
    return true
  }

  return toArray(expected).some((candidate) =>
    matchesGeneratedEnumValue(value, candidate, labels),
  )
}

function matchesGeneratedEnumValue<TValue extends string | number>(
  value: string | number | null | undefined,
  expected: TValue,
  labels: Readonly<Record<number, string>>,
) {
  if (value === expected) {
    return true
  }

  const expectedLabel = typeof expected === 'number'
    ? labels[expected] ?? String(expected)
    : String(expected)
  const actualLabel = typeof value === 'number'
    ? labels[value] ?? String(value)
    : String(value ?? '')

  return normalizeEnumLabel(actualLabel) === normalizeEnumLabel(expectedLabel)
}

function normalizeEnumLabel(value: string) {
  return value.replace(/[^a-zA-Z0-9]+/g, '').toLocaleLowerCase()
}

function toArray<TValue>(value: TValue | readonly TValue[]) {
  return Array.isArray(value) ? value : [value]
}
