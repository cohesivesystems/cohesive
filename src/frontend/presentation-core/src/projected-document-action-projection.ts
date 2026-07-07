import type {
  ActionDefinition,
  ActionPlacementDefinition,
  InputFormDefinition,
  PresentationModuleDefinition,
  ViewDefinition,
} from './module'
import {
  findPresentationAction,
} from './module'
import {
  createPresentationEnumDiscriminator,
  matchesPresentationEnum,
} from './target-bindings'
import {
  actionSemanticsKinds,
  actionKinds,
  actionScopeKinds,
  documentWorkspaceActionKinds,
  localDocumentEditorActionKinds,
  presentationBindingKinds,
  preparationKinds,
  type DocumentWorkspaceActionKind,
} from '@cohesive/presentation-contracts'

export interface ProjectedDocumentAction {
  readonly action: ActionDefinition
  readonly placement: ActionPlacementDefinition
}

export interface DocumentProfileActionProjectionOptions {
  readonly actionId?: string | null
  readonly actionPlacements: readonly ActionPlacementDefinition[]
  readonly dataSourceId: string
  readonly flowId?: string | null
  readonly module: Pick<PresentationModuleDefinition, 'Actions'> | null
}

export interface PromptActionProjectionOptions {
  readonly module: Pick<PresentationModuleDefinition, 'Actions'> | null
  readonly view: ViewDefinition | null
}

export type LocalDocumentEditorActionIntent = 'format' | 'reset'

export type PromptLocalActionIntent = 'revert'

/** Finds the profile action that saves the active JSON document resource. */
export function findDocumentSaveAction({
  actionPlacements,
  dataSourceId,
  module,
}: DocumentProfileActionProjectionOptions): ActionDefinition | null {
  const placedActions = resolvePlacedActions(module, actionPlacements)
  const semanticMatch = placedActions.find(({ action }) =>
    matchesDocumentWorkspaceActionSemantics(
      action,
      documentWorkspaceActionKinds.saveReview,
    ) &&
    hasEndpointRequestForDataSource(action, dataSourceId)
  )
  if (semanticMatch) {
    return semanticMatch.action
  }

  return placedActions.find(({ action }) =>
    isPreviewFlowAction(action) &&
    hasEndpointRequestForDataSource(action, dataSourceId) &&
    hasEndpointRequestSource(action, 'document.value'),
  )?.action ?? null
}

/**
 * Finds a long-running profile preview action whose endpoint request is driven
 * by input-form state rather than the current document value.
 */
export function findDocumentProcessPreviewAction({
  actionId,
  actionPlacements,
  dataSourceId,
  flowId,
  module,
}: DocumentProfileActionProjectionOptions): ActionDefinition | null {
  return findDocumentProcessPreviewActions({
    actionId,
    actionPlacements,
    dataSourceId,
    flowId,
    module,
  })[0] ?? null
}

/**
 * Finds long-running profile preview actions whose endpoint requests are driven
 * by input-form state rather than the current document value.
 */
export function findDocumentProcessPreviewActions({
  actionId,
  actionPlacements,
  dataSourceId,
  flowId,
  module,
}: DocumentProfileActionProjectionOptions): readonly ActionDefinition[] {
  const placedActions = resolvePlacedActions(module, actionPlacements)
  const semanticMatches = placedActions
    .filter(({ action }) =>
      matchesActionId(action, actionId) &&
      matchesActionFlow(action, flowId) &&
      matchesDocumentWorkspaceActionSemantics(
        action,
        documentWorkspaceActionKinds.processPreview,
      ) &&
      hasEndpointRequestForDataSource(action, dataSourceId),
    )
    .map(({ action }) => action)
  if (semanticMatches.length > 0) {
    return semanticMatches
  }

  return placedActions
    .filter(({ action }) =>
      matchesActionId(action, actionId) &&
      matchesActionFlow(action, flowId) &&
      isPreviewFlowAction(action) &&
      Boolean(action.Execution?.IsLongRunning) &&
      hasEndpointRequestForDataSource(action, dataSourceId) &&
      hasEndpointRequestSourcePrefix(action, 'input.'),
    )
    .map(({ action }) => action)
}

/** Finds an entity-scoped local editor action such as reset or format. */
export function findLocalDocumentEditorAction({
  actionPlacements,
  intent,
  module,
}: Omit<DocumentProfileActionProjectionOptions, 'dataSourceId'> & {
  readonly intent: LocalDocumentEditorActionIntent
}): ActionDefinition | null {
  const placedActions = resolvePlacedActions(module, actionPlacements)
  const semanticMatch = placedActions.find(({ action }) =>
    matchesLocalDocumentEditorActionSemantics(action, intent),
  )
  if (semanticMatch) {
    return semanticMatch.action
  }

  return placedActions.find(({ action, placement }) =>
    matchesPresentationEnum(action.Kind, localStateActionKind) &&
    matchesPresentationEnum(action.Scope, entityActionScopeKind) &&
    matchesPresentationEnum(action.Binding.Kind, localStateBindingKind) &&
    matchesActionIntent(action, placement, intent),
  )?.action ?? null
}

/** Finds a prompt's explicit dismiss action from PromptDismiss semantics. */
export function findPromptDismissAction({
  module,
  view,
}: PromptActionProjectionOptions): ActionDefinition | null {
  const actionId = view?.PromptDismiss?.DismissActionId
  const explicitAction = actionId
    ? findPresentationAction<ActionDefinition>(module, actionId)
    : null
  if (explicitAction) {
    return explicitAction
  }

  return resolvePromptActions(module, view).find(({ action }) =>
    matchesDocumentWorkspaceActionSemantics(
      action,
      documentWorkspaceActionKinds.saveCancel,
    ) ||
    matchesDocumentWorkspaceActionSemantics(
      action,
      documentWorkspaceActionKinds.processCancel,
    ),
  )?.action ?? null
}

/** Finds the primary prompt action that commits or accepts the prompt. */
export function findPromptCommitAction({
  module,
  view,
}: PromptActionProjectionOptions): ActionDefinition | null {
  const dismissActionId = view?.PromptDismiss?.DismissActionId
  const promptActions = resolvePromptActions(module, view)
  const semanticMatch = promptActions.find(({ action }) =>
    action.Id !== dismissActionId &&
    (
      matchesDocumentWorkspaceActionSemantics(
        action,
        documentWorkspaceActionKinds.saveCommit,
      ) ||
      matchesDocumentWorkspaceActionSemantics(
        action,
        documentWorkspaceActionKinds.processStart,
      )
    ),
  )
  if (semanticMatch) {
    return semanticMatch.action
  }

  return promptActions.find(({ action, placement }) =>
    action.Id !== dismissActionId &&
    (
      matchesPresentationEnum(action.Kind, transitionActionKind) ||
      matchesPresentationEnum(action.Kind, processStartActionKind)
    ) &&
    (
      placement.Intent === 'primary' ||
      action.Execution?.RequiresConfirmation ||
      matchesPresentationEnum(action.Kind, processStartActionKind)
    ),
  )?.action ?? null
}

/** Finds a secondary prompt-local action by semantic intent. */
export function findPromptLocalAction({
  intent,
  module,
  view,
}: PromptActionProjectionOptions & {
  readonly intent: PromptLocalActionIntent
}): ActionDefinition | null {
  const dismissActionId = view?.PromptDismiss?.DismissActionId
  const promptActions = resolvePromptActions(module, view)
  const semanticKind = documentWorkspaceActionKindByPromptLocalIntent[intent]
  const semanticMatch = promptActions.find(({ action }) =>
    action.Id !== dismissActionId &&
    matchesDocumentWorkspaceActionSemantics(action, semanticKind),
  )
  if (semanticMatch) {
    return semanticMatch.action
  }

  return promptActions.find(({ action, placement }) =>
    action.Id !== dismissActionId &&
    matchesPresentationEnum(action.Scope, flowActionScopeKind) &&
    matchesPresentationEnum(action.Binding.Kind, flowEventBindingKind) &&
    matchesActionIntent(action, placement, intent),
  )?.action ?? null
}

/** Resolves the input-form placement that invokes a given semantic action. */
export function findInputFormActionPlacement(
  inputForm: InputFormDefinition | null,
  action: ActionDefinition | null,
): ActionPlacementDefinition | null {
  if (!inputForm) {
    return null
  }

  return (
    inputForm.Actions.find((candidate) => candidate.ActionId === action?.Id) ??
    inputForm.Actions[0] ??
    null
  )
}

function resolvePlacedActions(
  module: Pick<PresentationModuleDefinition, 'Actions'> | null,
  actionPlacements: readonly ActionPlacementDefinition[],
): readonly ProjectedDocumentAction[] {
  return actionPlacements.flatMap((placement) => {
    const action = findPresentationAction<ActionDefinition>(module, placement.ActionId)
    return action ? [{ action, placement }] : []
  })
}

function resolvePromptActions(
  module: Pick<PresentationModuleDefinition, 'Actions'> | null,
  view: ViewDefinition | null,
): readonly ProjectedDocumentAction[] {
  return resolvePlacedActions(module, view?.Actions ?? [])
}

function isPreviewFlowAction(action: ActionDefinition) {
  return (
    matchesPresentationEnum(action.Kind, flowActionKind) &&
    matchesPresentationEnum(action.Preparation?.Kind ?? '', previewFlowPreparationKind)
  )
}

function hasEndpointRequestForDataSource(
  action: ActionDefinition,
  dataSourceId: string,
) {
  return action.EndpointRequests.some((request) =>
    !request.DataSourceId || request.DataSourceId === dataSourceId,
  )
}

function hasEndpointRequestSource(action: ActionDefinition, source: string) {
  return action.EndpointRequests.some((request) =>
    request.BodyFields.some((binding) => binding.Source.Field === source),
  )
}

function hasEndpointRequestSourcePrefix(action: ActionDefinition, prefix: string) {
  return action.EndpointRequests.some((request) =>
    request.BodyFields.some((binding) => binding.Source.Field?.startsWith(prefix)),
  )
}

function matchesActionId(
  action: Pick<ActionDefinition, 'Id'>,
  actionId: string | null | undefined,
) {
  return !actionId || action.Id === actionId
}

function matchesActionFlow(
  action: ActionDefinition,
  flowId: string | null | undefined,
) {
  return !flowId || action.Preparation?.FlowId === flowId
}

function matchesActionIntent(
  action: ActionDefinition,
  placement: ActionPlacementDefinition,
  intent: string,
) {
  return [
    action.Id,
    action.Name,
    action.Binding.Id,
    placement.Label,
    ...action.Annotations.flatMap((annotation) => [
      annotation.Name,
      typeof annotation.Value === 'string' ? annotation.Value : '',
    ]),
  ].some((value) => normalizeActionToken(value).includes(intent))
}

function matchesLocalDocumentEditorActionSemantics(
  action: ActionDefinition,
  intent: LocalDocumentEditorActionIntent,
) {
  if (
    !action.Semantics ||
    !matchesPresentationEnum(action.Semantics.Kind, localDocumentEditorActionSemanticsKind)
  ) {
    return false
  }

  const localDocumentEditor = action.Semantics.LocalDocumentEditor
  if (!localDocumentEditor) {
    return false
  }

  return matchesPresentationEnum(
    localDocumentEditor.Kind,
    localDocumentEditorActionKindByIntent[intent],
  )
}

function matchesDocumentWorkspaceActionSemantics(
  action: ActionDefinition,
  kind: DocumentWorkspaceActionKind,
) {
  if (
    !action.Semantics ||
    !matchesPresentationEnum(action.Semantics.Kind, documentWorkspaceActionSemanticsKind)
  ) {
    return false
  }

  const documentWorkspace = action.Semantics.DocumentWorkspace
  if (!documentWorkspace) {
    return false
  }

  return matchesPresentationEnum(documentWorkspace.Kind, { value: kind })
}

function normalizeActionToken(value: string | null | undefined) {
  return (value ?? '').replace(/[^a-zA-Z0-9]+/g, '').toLocaleLowerCase()
}

const localDocumentEditorActionSemanticsKind = createPresentationEnumDiscriminator(
  actionSemanticsKinds,
  'localDocumentEditor',
  'LocalDocumentEditor',
)

const documentWorkspaceActionSemanticsKind = createPresentationEnumDiscriminator(
  actionSemanticsKinds,
  'documentWorkspace',
  'DocumentWorkspace',
)

const localDocumentEditorActionKindByIntent = {
  format: createPresentationEnumDiscriminator(
    localDocumentEditorActionKinds,
    'format',
    'Format',
  ),
  reset: createPresentationEnumDiscriminator(
    localDocumentEditorActionKinds,
    'reset',
    'Reset',
  ),
} as const

const documentWorkspaceActionKindByPromptLocalIntent = {
  revert: documentWorkspaceActionKinds.saveRevert,
} as const

const entityActionScopeKind = createPresentationEnumDiscriminator(
  actionScopeKinds,
  'entity',
  'Entity',
)

const flowActionScopeKind = createPresentationEnumDiscriminator(
  actionScopeKinds,
  'flow',
  'Flow',
)

const flowActionKind = createPresentationEnumDiscriminator(
  actionKinds,
  'flowAction',
  'FlowAction',
)

const localStateActionKind = createPresentationEnumDiscriminator(
  actionKinds,
  'localStateAction',
  'LocalStateAction',
)

const processStartActionKind = createPresentationEnumDiscriminator(
  actionKinds,
  'processStartAction',
  'ProcessStartAction',
)

const transitionActionKind = createPresentationEnumDiscriminator(
  actionKinds,
  'transitionAction',
  'TransitionAction',
)

const flowEventBindingKind = createPresentationEnumDiscriminator(
  presentationBindingKinds,
  'flowEvent',
  'FlowEvent',
)

const localStateBindingKind = createPresentationEnumDiscriminator(
  presentationBindingKinds,
  'localState',
  'LocalState',
)

const previewFlowPreparationKind = createPresentationEnumDiscriminator(
  preparationKinds,
  'previewFlow',
  'PreviewFlow',
)
