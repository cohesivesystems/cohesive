export type PresentationComponentSystemComponents<
  TActions extends object = object,
  TBadges extends object = object,
  TCollectionChrome extends object = object,
  TCollections extends object = object,
  TDocumentWorkspaces extends object = object,
  TFieldValues extends object = object,
  TFeedback extends object = object,
  TForms extends object = object,
  TMetrics extends object = object,
  TNavigation extends object = object,
  TProcesses extends object = object,
  TPrompts extends object = object,
  TRecords extends object = object,
  TSurfaces extends object = object,
  TTabs extends object = object,
  TViewChrome extends object = object,
> = TActions &
  TBadges &
  TCollectionChrome &
  TCollections &
  TDocumentWorkspaces &
  TFieldValues &
  TFeedback &
  TForms &
  TMetrics &
  TNavigation &
  TProcesses &
  TPrompts &
  TRecords &
  TSurfaces &
  TTabs &
  TViewChrome

export interface PresentationComponentSystemRoleGroups<
  TActions extends object = object,
  TBadges extends object = object,
  TCollectionChrome extends object = object,
  TCollections extends object = object,
  TDocumentWorkspaces extends object = object,
  TFieldValues extends object = object,
  TFeedback extends object = object,
  TForms extends object = object,
  TMetrics extends object = object,
  TNavigation extends object = object,
  TProcesses extends object = object,
  TPrompts extends object = object,
  TRecords extends object = object,
  TSurfaces extends object = object,
  TTabs extends object = object,
  TViewChrome extends object = object,
> {
  readonly actions: TActions
  readonly badges: TBadges
  readonly collectionChrome: TCollectionChrome
  readonly collections: TCollections
  readonly documentWorkspaces: TDocumentWorkspaces
  readonly fieldValues: TFieldValues
  readonly feedback: TFeedback
  readonly forms: TForms
  readonly metrics: TMetrics
  readonly navigation: TNavigation
  readonly processes: TProcesses
  readonly prompts: TPrompts
  readonly records: TRecords
  readonly surfaces: TSurfaces
  readonly tabs: TTabs
  readonly viewChrome: TViewChrome
}

export type CreatePresentationComponentSystemOptions<
  TActions extends object = object,
  TBadges extends object = object,
  TCollectionChrome extends object = object,
  TCollections extends object = object,
  TDocumentWorkspaces extends object = object,
  TFieldValues extends object = object,
  TFeedback extends object = object,
  TForms extends object = object,
  TMetrics extends object = object,
  TNavigation extends object = object,
  TProcesses extends object = object,
  TPrompts extends object = object,
  TRecords extends object = object,
  TSurfaces extends object = object,
  TTabs extends object = object,
  TViewChrome extends object = object,
> = PresentationComponentSystemRoleGroups<
  TActions,
  TBadges,
  TCollectionChrome,
  TCollections,
  TDocumentWorkspaces,
  TFieldValues,
  TFeedback,
  TForms,
  TMetrics,
  TNavigation,
  TProcesses,
  TPrompts,
  TRecords,
  TSurfaces,
  TTabs,
  TViewChrome
> & {
  readonly id: string
  readonly target: string
}

export interface PresentationComponentSystem<
  TActions extends object = object,
  TBadges extends object = object,
  TCollectionChrome extends object = object,
  TCollections extends object = object,
  TDocumentWorkspaces extends object = object,
  TFieldValues extends object = object,
  TFeedback extends object = object,
  TForms extends object = object,
  TMetrics extends object = object,
  TNavigation extends object = object,
  TProcesses extends object = object,
  TPrompts extends object = object,
  TRecords extends object = object,
  TSurfaces extends object = object,
  TTabs extends object = object,
  TViewChrome extends object = object,
> extends PresentationComponentSystemRoleGroups<
    TActions,
    TBadges,
    TCollectionChrome,
    TCollections,
    TDocumentWorkspaces,
    TFieldValues,
    TFeedback,
    TForms,
    TMetrics,
    TNavigation,
    TProcesses,
    TPrompts,
    TRecords,
    TSurfaces,
    TTabs,
    TViewChrome
  > {
  readonly components: PresentationComponentSystemComponents<
    TActions,
    TBadges,
    TCollectionChrome,
    TCollections,
    TDocumentWorkspaces,
    TFieldValues,
    TFeedback,
    TForms,
    TMetrics,
    TNavigation,
    TProcesses,
    TPrompts,
    TRecords,
    TSurfaces,
    TTabs,
    TViewChrome
  >
  readonly id: string
  readonly target: string
}

export function createPresentationComponentSystem<
  TActions extends object,
  TBadges extends object,
  TCollectionChrome extends object,
  TCollections extends object,
  TDocumentWorkspaces extends object,
  TFieldValues extends object,
  TFeedback extends object,
  TForms extends object,
  TMetrics extends object,
  TNavigation extends object,
  TProcesses extends object,
  TPrompts extends object,
  TRecords extends object,
  TSurfaces extends object,
  TTabs extends object,
  TViewChrome extends object,
>({
  actions,
  badges,
  collectionChrome,
  collections,
  documentWorkspaces,
  fieldValues,
  feedback,
  forms,
  id,
  metrics,
  navigation,
  processes,
  prompts,
  records,
  surfaces,
  tabs,
  target,
  viewChrome,
}: CreatePresentationComponentSystemOptions<
  TActions,
  TBadges,
  TCollectionChrome,
  TCollections,
  TDocumentWorkspaces,
  TFieldValues,
  TFeedback,
  TForms,
  TMetrics,
  TNavigation,
  TProcesses,
  TPrompts,
  TRecords,
  TSurfaces,
  TTabs,
  TViewChrome
>): PresentationComponentSystem<
  TActions,
  TBadges,
  TCollectionChrome,
  TCollections,
  TDocumentWorkspaces,
  TFieldValues,
  TFeedback,
  TForms,
  TMetrics,
  TNavigation,
  TProcesses,
  TPrompts,
  TRecords,
  TSurfaces,
  TTabs,
  TViewChrome
> {
  return {
    actions,
    badges,
    collectionChrome,
    collections,
    components: createPresentationComponentSystemComponents({
      actions,
      badges,
      collectionChrome,
      collections,
      documentWorkspaces,
      fieldValues,
      feedback,
      forms,
      metrics,
      navigation,
      processes,
      prompts,
      records,
      surfaces,
      tabs,
      viewChrome,
    }),
    documentWorkspaces,
    fieldValues,
    feedback,
    forms,
    id,
    metrics,
    navigation,
    processes,
    prompts,
    records,
    surfaces,
    tabs,
    target,
    viewChrome,
  }
}

export function createPresentationComponentSystemComponents<
  TActions extends object,
  TBadges extends object,
  TCollectionChrome extends object,
  TCollections extends object,
  TDocumentWorkspaces extends object,
  TFieldValues extends object,
  TFeedback extends object,
  TForms extends object,
  TMetrics extends object,
  TNavigation extends object,
  TProcesses extends object,
  TPrompts extends object,
  TRecords extends object,
  TSurfaces extends object,
  TTabs extends object,
  TViewChrome extends object,
>({
  actions,
  badges,
  collectionChrome,
  collections,
  documentWorkspaces,
  fieldValues,
  feedback,
  forms,
  metrics,
  navigation,
  processes,
  prompts,
  records,
  surfaces,
  tabs,
  viewChrome,
}: PresentationComponentSystemRoleGroups<
  TActions,
  TBadges,
  TCollectionChrome,
  TCollections,
  TDocumentWorkspaces,
  TFieldValues,
  TFeedback,
  TForms,
  TMetrics,
  TNavigation,
  TProcesses,
  TPrompts,
  TRecords,
  TSurfaces,
  TTabs,
  TViewChrome
>): PresentationComponentSystemComponents<
  TActions,
  TBadges,
  TCollectionChrome,
  TCollections,
  TDocumentWorkspaces,
  TFieldValues,
  TFeedback,
  TForms,
  TMetrics,
  TNavigation,
  TProcesses,
  TPrompts,
  TRecords,
  TSurfaces,
  TTabs,
  TViewChrome
> {
  return {
    ...actions,
    ...badges,
    ...collectionChrome,
    ...collections,
    ...documentWorkspaces,
    ...fieldValues,
    ...feedback,
    ...forms,
    ...metrics,
    ...navigation,
    ...processes,
    ...prompts,
    ...records,
    ...surfaces,
    ...tabs,
    ...viewChrome,
  }
}
