import { viewChromeSlotKinds } from '@cohesivesystems/presentation-contracts'
import {
  createCollectionChromeRuntime,
  resolveCollectionChromeDataSourceIds,
} from './collection-chrome-runtime'
import type {
  ActionDefinition,
  ActionPlacementDefinition,
  DataSourceDefinition,
  DocumentProfileDefinition,
  FieldPresentationDefinition,
  FlowDefinition,
  InputFormDefinition,
  NavigationDefinition,
  QueryFormDefinition,
  TargetBindingDefinition,
  ViewDefinition,
  WorkspaceDefinition,
} from '@cohesivesystems/presentation-contracts'

export type {
  ActionDefinition,
  ActionEnablementCriterionDefinition,
  ActionEndpointRequestProjectionDefinition,
  ActionEndpointRequestValueBindingDefinition,
  ActionPlacementDefinition,
  ActionResultStateWriteDefinition,
  CollectionChromeDefinition,
  CollectionChromeSlotDefinition,
  CollectionChromeSlotKind,
  CollectionChromeSlotPlacement,
  CollectionColumnDefinition,
  CollectionDetailActivation,
  CollectionRowActionDefinition,
  CollectionRowActionParameterBindingDefinition,
  CollectionSelectionActionDefinition,
  CollectionSelectionActionParameterBindingDefinition,
  CollectionSelectionActionParameterSource,
  CollectionSelectionMode,
  CollectionViewDefinition,
  DataSourceDefinition,
  DataSourceQueryDefinition,
  DataSourceQueryEndpointBindingDefinition,
  DataSourceQueryFieldDefinition,
  DocumentActionStatusNoticeDefinition,
  DocumentProcessTaskNoticeActionDefinition,
  DocumentProcessTaskNoticeActionTargetKind,
  DocumentProcessTaskNoticeDefinition,
  DocumentProfileDefinition,
  DocumentWorkspaceSurfaceSlotDefinition,
  DocumentWorkspaceSurfaceSlotRole,
  FieldDisplayOptions,
  FieldEntityReferenceFallbackKind,
  FieldJsonDisplayMode,
  FieldPresentationDefinition,
  FieldValueIconDefinition,
  FieldValueLabelDefinition,
  FieldValueToneDefinition,
  FlowDefinition,
  FlowStateDefinition,
  FlowTransitionDefinition,
  InputFormDefinition,
  InputFormFieldDefinition,
  InputFormGroupDefinition,
  LayoutNodeDefinition,
  LayoutNodeKind,
  LayoutOrientation,
  PresentationAnnotationDefinition,
  PresentationBadgeDefinition,
  PresentationBindingDefinition,
  PresentationContentDefinition,
  PresentationModuleDefinition,
  PresentationValueDefinition,
  ProcessTaskSelectorDefinition,
  ProcessTaskSelectorMatchDefinition,
  ProjectionDefinition,
  PromptDismissPolicyDefinition,
  PromptDocumentPreviewDefinition,
  PromptStatusMessageDefinition,
  QueryFormDefinition,
  QueryFormUrlPolicyDefinition,
  TargetBindingDefinition,
  ViewChromeSlotDefinition,
  ViewChromeSlotKind,
  ViewChromeSlotPlacement,
  ViewDefinition,
  ViewRegionDefinition,
  WorkspaceDefinition,
  WorkspaceLayoutDefinition,
  WorkspaceLayoutModeDefinition,
  WorkspaceRefDefinition,
} from '@cohesivesystems/presentation-contracts'

type IdentifiedDefinition = { readonly Id: string }

export function getDefaultNavigation<TNavigation extends NavigationDefinition>(
  module: { readonly Navigation: readonly TNavigation[] } | null,
): TNavigation | null {
  return module?.Navigation[0] ?? null
}

export function findPresentationView<TView extends ViewDefinition>(
  module: { readonly Views: readonly ViewDefinition[] } | null,
  viewId: string,
): TView | null {
  return findById(module?.Views, viewId) as TView | null
}

export function findPresentationWorkspace<TWorkspace extends WorkspaceDefinition>(
  module: { readonly Workspaces: readonly WorkspaceDefinition[] } | null,
  workspaceId: string,
): TWorkspace | null {
  return findById(module?.Workspaces, workspaceId) as TWorkspace | null
}

export function findPresentationDocumentProfile<TProfile extends DocumentProfileDefinition>(
  workspace: { readonly DocumentProfiles: readonly DocumentProfileDefinition[] } | null,
  documentProfileId: string,
): TProfile | null {
  return findById(workspace?.DocumentProfiles, documentProfileId) as TProfile | null
}

export function findPresentationDataSource<TDataSource extends DataSourceDefinition>(
  module: { readonly DataSources: readonly DataSourceDefinition[] } | null,
  dataSourceId: string,
): TDataSource | null {
  return findById(module?.DataSources, dataSourceId) as TDataSource | null
}

export function findPresentationQueryForm<TQueryForm extends QueryFormDefinition>(
  module: { readonly QueryForms: readonly QueryFormDefinition[] } | null,
  queryFormId: string,
): TQueryForm | null {
  return findById(module?.QueryForms, queryFormId) as TQueryForm | null
}

export function findPresentationInputForm<TInputForm extends InputFormDefinition>(
  module: { readonly InputForms: readonly InputFormDefinition[] } | null,
  inputFormId: string,
): TInputForm | null {
  return findById(module?.InputForms, inputFormId) as TInputForm | null
}

export function findPresentationInputFormForView<TInputForm extends InputFormDefinition>(
  module: {
    readonly InputForms: readonly InputFormDefinition[]
    readonly QueryForms: readonly QueryFormDefinition[]
  } | null,
  view: Pick<ViewDefinition, 'Collection' | 'DataSourceIds' | 'Subject'> | null,
): TInputForm | null {
  if (!view) {
    return null
  }

  const inputFormId = view.Subject.InputFormId
  if (inputFormId) {
    return findPresentationInputForm<TInputForm>(module, inputFormId)
  }

  const queryFormId = view.Subject.QueryFormId
  if (queryFormId) {
    const queryForm = findPresentationQueryForm(module, queryFormId)
    return queryForm
      ? findPresentationInputForm<TInputForm>(module, queryForm.FormId)
      : null
  }

  const formDataSourceIds = new Set([
    ...view.DataSourceIds,
    ...(view.Subject.DataSourceId ? [view.Subject.DataSourceId] : []),
    ...resolveCollectionChromeDataSourceIds(view.Collection),
  ])
  const inputForm = module?.InputForms.find((candidate) =>
    formDataSourceIds.has(candidate.StateDataSourceId) ||
    formDataSourceIds.has(candidate.Target.DataSourceId ?? ''),
  )
  return (inputForm as TInputForm | undefined) ?? null
}

export function findPresentationQueryFormForView<TQueryForm extends QueryFormDefinition>(
  module: {
    readonly InputForms: readonly InputFormDefinition[]
    readonly QueryForms: readonly QueryFormDefinition[]
  } | null,
  view: Pick<ViewDefinition, 'Collection' | 'DataSourceIds' | 'Subject'> | null,
): TQueryForm | null {
  if (!view) {
    return null
  }

  const queryFormId = view.Subject.QueryFormId
  if (queryFormId) {
    return findPresentationQueryForm<TQueryForm>(module, queryFormId)
  }

  const collectionQueryFormId = createCollectionChromeRuntime(view.Collection).queryFormSlot
    ?.QueryFormId
  if (collectionQueryFormId) {
    return findPresentationQueryForm<TQueryForm>(module, collectionQueryFormId)
  }

  const queryDataSourceIds = new Set([
    ...view.DataSourceIds,
    ...(view.Subject.DataSourceId ? [view.Subject.DataSourceId] : []),
    ...resolveCollectionChromeDataSourceIds(view.Collection),
  ])
  const queryForm = module?.QueryForms.find((candidate) =>
    queryDataSourceIds.has(candidate.Target.State.DraftDataSourceId) ||
    queryDataSourceIds.has(candidate.Target.State.ResultDataSourceId) ||
    queryDataSourceIds.has(candidate.Target.Result.DataSourceId) ||
    queryDataSourceIds.has(findPresentationInputForm(module, candidate.FormId)?.StateDataSourceId ?? ''),
  )
  return (queryForm as TQueryForm | undefined) ?? null
}

export function findPresentationAction<TAction extends ActionDefinition>(
  module: { readonly Actions: readonly ActionDefinition[] } | null,
  actionId: string,
): TAction | null {
  return findById(module?.Actions, actionId) as TAction | null
}

export function findPresentationFlow<TFlow extends FlowDefinition>(
  module: { readonly Flows: readonly FlowDefinition[] } | null,
  flowId: string,
): TFlow | null {
  return findById(module?.Flows, flowId) as TFlow | null
}

export function findPresentationField<TField extends FieldPresentationDefinition>(
  module: { readonly Fields: readonly FieldPresentationDefinition[] } | null,
  fieldId: string,
): TField | null {
  return findById(module?.Fields, fieldId) as TField | null
}

export function findPresentationFieldByFieldPath<TField extends FieldPresentationDefinition>(
  module: { readonly Fields: readonly FieldPresentationDefinition[] } | null,
  field: string,
): TField | null {
  const match = module?.Fields.find((candidate) => candidate.Field === field)
  return (match as TField | undefined) ?? null
}

export function findPresentationTargetBinding<TTarget extends TargetBindingDefinition>(
  module: { readonly Targets: readonly TargetBindingDefinition[] } | null,
  targetId: string,
): TTarget | null {
  return findById(module?.Targets, targetId) as TTarget | null
}

export function resolvePresentationViewActionPlacements(
  view: Pick<ViewDefinition, 'Actions' | 'Chrome'> | null,
): readonly ActionPlacementDefinition[] {
  const chromeActions =
    view?.Chrome?.Slots.filter((slot) => isActionsChromeSlotKind(slot.Kind))
      .flatMap((slot) => slot.Actions) ?? []
  const actionPlacements = [...chromeActions, ...(view?.Actions ?? [])]
  const seen = new Set<string>()

  return actionPlacements.filter((placement) => {
    const key = `${placement.Region}:${placement.ActionId}`
    if (seen.has(key)) {
      return false
    }

    seen.add(key)
    return true
  })
}

function findById<TDefinition extends IdentifiedDefinition>(
  definitions: readonly TDefinition[] | null | undefined,
  id: string,
) {
  return definitions?.find((definition) => definition.Id === id) ?? null
}

function isActionsChromeSlotKind(value: string | number) {
  return value === viewChromeSlotKinds.actions || String(value).toLocaleLowerCase() === 'actions'
}
