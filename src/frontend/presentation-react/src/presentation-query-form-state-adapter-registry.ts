import type { PresentationModuleDefinition } from '@cohesivesystems/presentation-core'
import type {
  PresentationQueryFormStateAdapter,
  PresentationQueryFormStateAdapterRegistry,
} from './presentation-query-form-state'
import { createProjectedQueryFormStateAdapter } from './projected-query-form-state-adapter'

export function resolvePresentationQueryFormStateAdapters(
  module: PresentationModuleDefinition | null,
  registry: PresentationQueryFormStateAdapterRegistry,
): readonly PresentationQueryFormStateAdapter[] {
  if (!module) {
    return Object.values(registry).filter(isPresentationQueryFormStateAdapter)
  }

  return module.QueryForms
    .map((queryForm) => registry[queryForm.Id] ?? createProjectedQueryFormStateAdapter(queryForm.Id))
    .filter(isPresentationQueryFormStateAdapter)
}

function isPresentationQueryFormStateAdapter(
  value: PresentationQueryFormStateAdapter | undefined,
): value is PresentationQueryFormStateAdapter {
  return Boolean(value)
}
