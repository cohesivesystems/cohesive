import type {
  ActionDefinition,
  PresentationModuleDefinition,
  ViewDefinition,
} from './module'
import {
  findPresentationAction,
} from './module'
import {
  createPresentationProjectionDiagnostic,
  type PresentationProjectionDiagnostic,
} from './presentation-projection-diagnostics'
import {
  getPresentationViewProjectedActions,
} from './presentation-semantics'
import type {
  ProjectedCollectionActionExecutionContext,
  ProjectedCollectionActionRuntimeRegistry,
  ProjectedCollectionRuntime,
  ProjectedActionPlacementLike,
} from './projected-collection-runtime'

export interface ProjectProjectedCollectionActionIconDiagnosticsOptions {
  readonly actionPlacements: readonly ProjectedActionPlacementLike[]
  readonly module: Pick<PresentationModuleDefinition, 'Targets'> | null
  readonly source: string
  readonly surfaceId?: string | null
  readonly surfaceName?: string | null
}

export interface ProjectProjectedCollectionActionRuntimeDiagnosticsOptions<TData extends object> {
  readonly actionRuntimes: ProjectedCollectionActionRuntimeRegistry<TData>
  readonly collectionRuntime: ProjectedCollectionRuntime<TData>
  readonly module: Pick<PresentationModuleDefinition, 'Actions' | 'Targets'> | null
  readonly projectActionIconDiagnostics?: (
    options: ProjectProjectedCollectionActionIconDiagnosticsOptions,
  ) => readonly PresentationProjectionDiagnostic[]
  readonly view: Pick<ViewDefinition, 'Actions' | 'Chrome' | 'Collection' | 'Id' | 'Name'> | null
}

/**
 * Reports whether projected collection row/selection actions are actually
 * interpreted by the frontend action runtime registry.
 */
export function projectProjectedCollectionActionRuntimeDiagnostics<TData extends object>({
  actionRuntimes,
  collectionRuntime,
  module,
  projectActionIconDiagnostics,
  view,
}: ProjectProjectedCollectionActionRuntimeDiagnosticsOptions<TData>): readonly PresentationProjectionDiagnostic[] {
  if (!view) {
    return []
  }

  const diagnostics: PresentationProjectionDiagnostic[] = []
  diagnostics.push(
    ...projectMissingCatalogActionDiagnostics({ module, view }),
  )
  if (projectActionIconDiagnostics) {
    diagnostics.push(...projectActionIconDiagnostics({
      actionPlacements: getPresentationViewProjectedActions(view)
        .filter((actionRef) =>
          actionRef.contextKind === 'collection-row' ||
          actionRef.contextKind === 'collection-selection')
        .map((actionRef) => actionRef.placement),
      module,
      source: `projected-collection-action-icons:${view.Id}`,
      surfaceId: view.Id,
      surfaceName: view.Name,
    }))
  }

  const firstRow = collectionRuntime.data[0] ?? null
  for (const rowAction of collectionRuntime.actions.rowActions) {
    diagnostics.push(
      ...projectResolvedActionRuntimeDiagnostics({
        action: rowAction.action,
        actionRefSource: rowAction.actionRef.source,
        context: firstRow
          ? collectionRuntime.actions.createRowActionContext(rowAction, firstRow)
          : null,
        contextKind: 'collection-row',
        runtime: actionRuntimes[rowAction.action.Id],
        slotId: rowAction.slotId ?? null,
        view,
      }),
    )
  }

  for (const selectionAction of collectionRuntime.actions.selectionActions) {
    const selectedItem = collectionRuntime.actions.resolveSelectionActionItems({
      selectionActions: [selectionAction],
    })[0]

    diagnostics.push(
      ...projectResolvedActionRuntimeDiagnostics({
        action: selectionAction.action,
        actionRefSource: selectionAction.actionRef.source,
        context: selectedItem?.actionContext ?? null,
        contextKind: 'collection-selection',
        runtime: actionRuntimes[selectionAction.action.Id],
        slotId: selectionAction.slotId ?? null,
        view,
      }),
    )
  }

  return diagnostics
}

function projectMissingCatalogActionDiagnostics({
  module,
  view,
}: {
  readonly module: Pick<PresentationModuleDefinition, 'Actions'> | null
  readonly view: Pick<ViewDefinition, 'Actions' | 'Chrome' | 'Collection' | 'Id' | 'Name'>
}) {
  return getPresentationViewProjectedActions(view).flatMap((actionRef) => {
    if (
      actionRef.contextKind !== 'collection-row' &&
      actionRef.contextKind !== 'collection-selection'
    ) {
      return []
    }

    if (findPresentationAction<ActionDefinition>(module, actionRef.actionId)) {
      return []
    }

    return [
      createDiagnostic({
        actionId: actionRef.actionId,
        actionName: null,
        actionRefSource: actionRef.source,
        contextKind: actionRef.contextKind,
        message:
          `Collection ${formatContextKind(actionRef.contextKind)} action ` +
          `'${actionRef.actionId}' on '${view.Name}' is projected but is not present in the action catalog.`,
        reason: `missing-action.${actionRef.source}`,
        severity: 'error',
        slotId: actionRef.slotId ?? null,
        status: 'unbound',
        target: 'action-catalog',
        view,
      }),
    ]
  })
}

function projectResolvedActionRuntimeDiagnostics<TData extends object>({
  action,
  actionRefSource,
  context,
  contextKind,
  runtime,
  slotId,
  view,
}: {
  readonly action: ActionDefinition
  readonly actionRefSource: string
  readonly context: ProjectedCollectionActionExecutionContext<TData> | null
  readonly contextKind: 'collection-row' | 'collection-selection'
  readonly runtime: ProjectedCollectionActionRuntimeRegistry<TData>[string]
  readonly slotId: string | null
  readonly view: Pick<ViewDefinition, 'Id' | 'Name'>
}) {
  const diagnostics: PresentationProjectionDiagnostic[] = []

  if (!runtime?.execute) {
    diagnostics.push(createDiagnostic({
      action,
      actionRefSource,
      contextKind,
      message:
        `Collection ${formatContextKind(contextKind)} action '${action.Name}' ` +
        `on '${view.Name}' has no projected frontend action runtime binding.`,
      reason: `missing-runtime.${actionRefSource}`,
      severity: 'warning',
      slotId,
      status: 'unbound',
      target: 'collection-action-runtime-registry',
      view,
    }))
    return diagnostics
  }

  if (runtime.isHidden || runtime.isDisabled) {
    diagnostics.push(createDiagnostic({
      action,
      actionRefSource,
      contextKind,
      message:
        `Collection ${formatContextKind(contextKind)} action '${action.Name}' ` +
        `on '${view.Name}' has a runtime binding, but it is disabled or hidden.`,
      reason: `disabled-runtime.${actionRefSource}`,
      severity: 'info',
      slotId,
      status: 'bound',
      target: 'collection-action-runtime-registry',
      view,
    }))
  }

  if (context && runtime.canExecute && !runtime.canExecute(context)) {
    diagnostics.push(createDiagnostic({
      action,
      actionRefSource,
      contextKind,
      message:
        `Collection ${formatContextKind(contextKind)} action '${action.Name}' ` +
        `on '${view.Name}' has a runtime binding, but it cannot execute for the current context.`,
      reason: `disabled-runtime.${actionRefSource}`,
      severity: 'info',
      slotId,
      status: 'bound',
      target: 'collection-action-runtime-registry',
      view,
    }))
  }

  return diagnostics
}

function createDiagnostic({
  action,
  actionId,
  actionName,
  actionRefSource,
  category = 'missing-binding',
  contextKind,
  message,
  reason,
  severity,
  slotId,
  status,
  target,
  view,
}: {
  readonly action?: ActionDefinition
  readonly actionId?: string
  readonly actionName?: string | null
  readonly actionRefSource: string
  readonly category?: PresentationProjectionDiagnostic['category']
  readonly contextKind: 'collection-row' | 'collection-selection'
  readonly message: string
  readonly reason: string
  readonly severity: PresentationProjectionDiagnostic['severity']
  readonly slotId: string | null
  readonly status: NonNullable<PresentationProjectionDiagnostic['interpretation']>['status']
  readonly target: string
  readonly view: Pick<ViewDefinition, 'Id' | 'Name'>
}) {
  const resolvedActionId = action?.Id ?? actionId ?? 'unknown-action'
  return createPresentationProjectionDiagnostic({
    category,
    details: {
      actionId: resolvedActionId,
      actionKind: action?.Kind ?? null,
      actionRefSource,
      actionScope: action?.Scope ?? null,
      bindingKind: action?.Binding.Kind ?? null,
      contextKind,
      slotId,
      viewId: view.Id,
    },
    id: `collection-action-runtime.${view.Id}.${resolvedActionId}.${reason}`,
    interpretation: {
      status,
      target,
    },
    message,
    severity,
    source: 'projected-collection-action-runtime',
    subject: {
      id: resolvedActionId,
      kind: 'action',
      name: action?.Name ?? actionName ?? null,
    },
    suggestedNextStep:
      status === 'unbound'
        ? 'Add a generic collection action runtime binding for this projected action.'
        : undefined,
  })
}

function formatContextKind(contextKind: 'collection-row' | 'collection-selection') {
  return contextKind === 'collection-row' ? 'row' : 'selection'
}
