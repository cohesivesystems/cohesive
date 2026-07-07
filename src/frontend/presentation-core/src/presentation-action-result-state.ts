import {
  actionResultStateWriteModes,
  type ActionResultPolicy,
  type ActionResultStateWriteDefinition,
  type ActionResultStateWriteMode,
} from '@cohesivesystems/presentation-contracts'

import {
  readObjectPath,
} from './object-path'

/**
 * Route-scoped presentation state keyed by semantic data-source id.
 *
 * Values in this map are action outputs, or values derived from action outputs,
 * that should be projected as local data-source values until the route scope is
 * reset.
 */
export type PresentationActionResultStateValues = Readonly<Record<string, unknown>>

/**
 * Inputs for applying explicit action-result state writes.
 */
export interface ApplyPresentationActionResultStateWritesOptions {
  /** Action endpoint response used as the source value for each write. */
  readonly result: unknown

  /** Current route-scoped action-result state. */
  readonly state: PresentationActionResultStateValues

  /** State-write declarations to apply. */
  readonly writes?: readonly ActionResultStateWriteDefinition[] | null
}

/**
 * Inputs for applying the state-write policy declared by an action result.
 */
export interface ApplyPresentationActionResultPolicyStateWritesOptions {
  /** Action result policy that declares state writes, when one is present. */
  readonly policy?: ActionResultPolicy | null

  /** Action endpoint response used as the source value for each policy write. */
  readonly result: unknown

  /** Current route-scoped action-result state. */
  readonly state: PresentationActionResultStateValues
}

/**
 * Applies the state writes declared by an action result policy.
 *
 * The returned state preserves object identity when the policy has no writes,
 * which lets React callers avoid unnecessary rebinding work.
 */
export function applyPresentationActionResultPolicyStateWrites({
  policy,
  result,
  state,
}: ApplyPresentationActionResultPolicyStateWritesOptions) {
  return applyPresentationActionResultStateWrites({
    result,
    state,
    writes: policy?.StateWrites,
  })
}

/**
 * Applies backend-declared action response writes to route-local presentation
 * data-source state.
 *
 * Each write targets a semantic data-source id and reads either the full action
 * response or a dot-separated response path. The function returns a new state
 * object only when at least one valid write is applied.
 */
export function applyPresentationActionResultStateWrites({
  result,
  state,
  writes,
}: ApplyPresentationActionResultStateWritesOptions): PresentationActionResultStateValues {
  if (!writes?.length) {
    return state
  }

  let next: Record<string, unknown> | null = null
  for (const write of writes) {
    if (!write.TargetDataSourceId) {
      continue
    }

    next ??= { ...state }
    applyPresentationActionResultStateWrite(next, write, result)
  }

  return next ?? state
}

/**
 * Applies a single write declaration to a mutable state accumulator.
 */
function applyPresentationActionResultStateWrite(
  state: Record<string, unknown>,
  write: ActionResultStateWriteDefinition,
  result: unknown,
) {
  const mode = normalizeActionResultStateWriteMode(write.Mode)
  if (mode === 'clear') {
    delete state[write.TargetDataSourceId]
    return
  }

  const value = write.SourcePath ? readObjectPath(result, write.SourcePath) : result
  switch (mode) {
    case 'append':
      state[write.TargetDataSourceId] = appendPresentationStateValue(
        state[write.TargetDataSourceId],
        value,
      )
      return
    case 'merge':
      state[write.TargetDataSourceId] = mergePresentationStateValue(
        state[write.TargetDataSourceId],
        value,
      )
      return
    case 'replace':
    default:
      state[write.TargetDataSourceId] = value
      return
  }
}

/**
 * Appends one scalar or array-valued write source to the current target value.
 */
function appendPresentationStateValue(current: unknown, value: unknown) {
  const currentItems = Array.isArray(current) ? current : current === undefined ? [] : [current]
  const nextItems = Array.isArray(value) ? value : [value]
  return [...currentItems, ...nextItems]
}

/**
 * Merges object writes shallowly and falls back to replacement for non-objects.
 */
function mergePresentationStateValue(current: unknown, value: unknown) {
  if (isRecord(current) && isRecord(value)) {
    return {
      ...current,
      ...value,
    }
  }

  return value
}

/**
 * Narrows values that can participate in shallow merge writes.
 */
function isRecord(value: unknown): value is Record<string, unknown> {
  return Boolean(value) && typeof value === 'object' && !Array.isArray(value)
}

/**
 * Normalizes generated numeric enum values and JSON string enum values into the
 * runtime write-mode labels used by the state applicator.
 */
function normalizeActionResultStateWriteMode(
  mode: ActionResultStateWriteMode | string,
): 'replace' | 'merge' | 'append' | 'clear' {
  if (mode === actionResultStateWriteModes.merge || matchesModeLabel(mode, 'merge')) {
    return 'merge'
  }

  if (mode === actionResultStateWriteModes.append || matchesModeLabel(mode, 'append')) {
    return 'append'
  }

  if (mode === actionResultStateWriteModes.clear || matchesModeLabel(mode, 'clear')) {
    return 'clear'
  }

  return 'replace'
}

/**
 * Compares a generated JSON enum label case-insensitively.
 */
function matchesModeLabel(value: unknown, label: string) {
  return typeof value === 'string' && value.toLocaleLowerCase() === label
}
